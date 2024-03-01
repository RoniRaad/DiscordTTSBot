using DSharpPlus;
using DSharpPlus.CommandsNext;
using DSharpPlus.VoiceNext;
using Microsoft.Extensions.Logging;
using TTSBot.Modules;

var token = Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN");

if (token is null)
{
	Console.Error.WriteLine("DISCORD_BOT_TOKEN environment variable is not set. Aborting");
	Environment.Exit(1);
}

var cfg = new DiscordConfiguration
{
	Token = token,
	TokenType = TokenType.Bot,
	AutoReconnect = true,
	MinimumLogLevel = LogLevel.Error,
	Intents = DiscordIntents.GuildVoiceStates | DiscordIntents.Guilds | DiscordIntents.GuildMessages | DiscordIntents.DirectMessages | DiscordIntents.GuildMessageTyping | DiscordIntents.MessageContents,
};

var _client = new DiscordClient(cfg);

var ccfg = new CommandsNextConfiguration
{
	StringPrefixes = new[] { "!" },
	EnableDms = true,
	EnableMentionPrefix = true
};

_client.MessageCreated += _client_MessageCreated;

Task _client_MessageCreated(DiscordClient sender, DSharpPlus.EventArgs.MessageCreateEventArgs args)
{
	Task.Run(() => MessageCreated(sender, args));

	return Task.CompletedTask;
}

async Task MessageCreated(DiscordClient sender, DSharpPlus.EventArgs.MessageCreateEventArgs args)
{
	var member = await args.Guild.GetMemberAsync(args.Author.Id);
	if (member is null)
		return;

	if (member.VoiceState?.IsSelfMuted is false
		|| args.Message.Content[0] == '!' 
		|| !args.Channel.Name.EndsWith("tts") 
		|| args.Author.IsBot)
		return;

	await TTSCommands.PlayTTSAsync(member, sender, args.Message.Content);
}

CommandsNextExtension commands = _client.UseCommandsNext(ccfg);
commands.RegisterCommands<TTSCommands>();
var voiceNextExt = _client.UseVoiceNext();



await _client.ConnectAsync();

await Task.Delay(-1);