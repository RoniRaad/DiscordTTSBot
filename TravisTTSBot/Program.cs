using DiscordTTSBot.Static;
using DiscordTTSBot.TTS;
using NetCord;
using NetCord.Gateway;
using NetCord.Services;
using NetCord.Services.Commands;
using Renci.SshNet;
using TTSBot.Modules;

// Initialize TTS providers
var googleTTS = new GoogleTTSProvider();
var localTTSHost = Environment.GetEnvironmentVariable("LOCAL_TTS_HOST") ?? "192.168.1.67";
var localTTS = new LocalApiTTSProvider(baseUrl: $"http://{localTTSHost}:8880");
var ttsRegistry = new TTSProviderRegistry(defaultProvider: googleTTS);

// Override provider for specific users by Discord user ID:
ttsRegistry.SetUserProvider(790584186054377472, localTTS, "Trav");
ttsRegistry.SetUserProvider(280553115583774720, localTTS, "Adri");

TTSCommands.Providers = ttsRegistry;

var token = Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN");

if (token is null)
{
	Console.Error.WriteLine("DISCORD_BOT_TOKEN environment variable is not set. Aborting");
	Environment.Exit(1);
}

// WOL before health check, SSH shutdown when bot leaves all voice sessions
var wolMac = Environment.GetEnvironmentVariable("WOL_MAC_ADDRESS");
var wolBroadcast = Environment.GetEnvironmentVariable("WOL_BROADCAST_IP");
var sshHost = localTTSHost;
var sshUsername = Environment.GetEnvironmentVariable("SSH_USERNAME");
var sshPassword = Environment.GetEnvironmentVariable("SSH_PASSWORD");
var sshKeyPath = Environment.GetEnvironmentVariable("SSH_KEY_PATH");

localTTS.OnWakeUp = async () =>
{
	if (wolMac is not null)
	{
		await WakeOnLan.SendAsync(wolMac, wolBroadcast);
		Console.WriteLine("WOL sent for local TTS server.");
	}
};

TTSCommands.OnLastVoiceDisconnect = () =>
{
	if (sshUsername is null)
		return Task.CompletedTask;

	try
	{
		AuthenticationMethod auth = sshKeyPath is not null
			? new PrivateKeyAuthenticationMethod(sshUsername, new PrivateKeyFile(sshKeyPath))
			: new PasswordAuthenticationMethod(sshUsername, sshPassword!);

		using var sshClient = new SshClient(new ConnectionInfo(sshHost, sshUsername, auth));
		sshClient.Connect();
		sshClient.RunCommand("sudo shutdown -h now");
		sshClient.Disconnect();
		Console.WriteLine($"Shutdown command sent to {sshHost} via SSH.");
		localTTS.ResetHealth();
	}
	catch (Exception ex)
	{
		Console.Error.WriteLine($"SSH shutdown failed: {ex.Message}");
	}

	return Task.CompletedTask;
};

var client = new GatewayClient(new BotToken(token), new GatewayClientConfiguration
{
	Intents = GatewayIntents.GuildVoiceStates | GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.DirectMessages | GatewayIntents.MessageContent,
});

TTSCommands.Client = client;

CommandService<CommandContext> commandService = new();
commandService.AddModules(typeof(TTSCommands).Assembly);

client.MessageCreate += async message =>
{
	if (message.Author.IsBot)
		return;

	// Handle commands with "!" prefix
	if (message.Content.StartsWith('!'))
	{
		var result = await commandService.ExecuteAsync(prefixLength: 1, new CommandContext(message, client));
		if (result is IFailResult failResult)
		{
			try
			{
				await message.ReplyAsync(failResult.Message);
			}
			catch { }
		}
		return;
	}

	// Auto-TTS: if user is muted in voice and message is in a "tts" channel
	await HandleAutoTTS(message);
};

// Auto-leave voice channel when bot is the only one left
// Note: NetCord fires this event BEFORE updating the cache, so we must
// account for the triggering user's new state manually.
client.VoiceStateUpdate += voiceState =>
{
	_ = Task.Run(() =>
	{
		try
		{
			var guildId = voiceState.GuildId;
			var guild = client.Cache.Guilds[guildId];
			var botId = client.Id;

			// Ignore if the bot itself triggered this event
			if (voiceState.UserId == botId)
				return;

			// Check if the bot is in a voice channel in this guild
			if (!guild.VoiceStates.TryGetValue(botId, out var botVoiceState))
				return;

			if (botVoiceState.ChannelId is not ulong botChannelId)
				return;

			// Count non-bot users in the bot's channel.
			// The cache is stale (updated after this handler), so we override
			// the triggering user's channel with their new state from the event.
			var triggerUserInCache = false;
			var usersInChannel = 0;

			foreach (var vs in guild.VoiceStates.Values)
			{
				if (vs.UserId == botId)
					continue;

				if (vs.UserId == voiceState.UserId)
				{
					triggerUserInCache = true;
					if (voiceState.ChannelId == botChannelId)
						usersInChannel++;
				}
				else if (vs.ChannelId == botChannelId)
				{
					usersInChannel++;
				}
			}

			// If the triggering user wasn't in cache yet (just joined voice),
			// check if they're joining the bot's channel
			if (!triggerUserInCache && voiceState.ChannelId == botChannelId)
				usersInChannel++;

			if (usersInChannel == 0)
			{
				Console.WriteLine($"Bot is alone in voice channel {botChannelId}, disconnecting...");
				TTSCommands.DisconnectFromGuild(guildId);
			}
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"Error in voice state handler: {ex.Message}");
		}
	});

	return default;
};

async Task HandleAutoTTS(Message message)
{
	if (message.GuildId is not ulong guildId)
		return;

	if (message.Channel is not TextGuildChannel textChannel)
		return;

	if (!textChannel.Name.EndsWith("tts"))
		return;

	var guild = client.Cache.Guilds[guildId];
	if (!guild.VoiceStates.TryGetValue(message.Author.Id, out var voiceState))
		return;

	if (!voiceState.IsSelfMuted)
		return;

	if (voiceState.ChannelId is not ulong channelId)
		return;

	await TTSCommands.PlayTTSAsync(client, guildId, channelId, message.Author.Id, message.Content, message.ChannelId);
}

await client.StartAsync();

Console.WriteLine("Client connected...");

await Task.Delay(-1);
