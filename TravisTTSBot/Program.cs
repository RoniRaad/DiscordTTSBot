using DiscordTTSBot.LLM;
using DiscordTTSBot.Static;
using DiscordTTSBot.STT;
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
localTTS.AddVoice("Trav");
localTTS.AddVoice("Adri");
localTTS.AddVoice("Winston");
var ttsRegistry = new TTSProviderRegistry(defaultProvider: googleTTS);

// Override provider for specific users by Discord user ID:
ttsRegistry.SetUserProvider(246016109663354880, localTTS, "Trav");
ttsRegistry.SetUserProvider(280553115583774720, localTTS, "Adri");
ttsRegistry.SetUserProvider(790584186054377472, localTTS, "Trav");

TTSCommands.Providers = ttsRegistry;

// Initialize STT + LLM
var transcriptionService = new TranscriptionService(localTTSHost);
var ollamaService = new OllamaService(localTTSHost);
var voiceListener = new VoiceListener(transcriptionService);

// Register AI personas — each has a keyword trigger, system prompt, and TTS voice
var personas = new List<AiPersona>
{
	new()
	{
		Keywords = ["winston"],
		Voice = "Winston",
		SystemPrompt = """
			You ARE Winston, the genetically engineered gorilla scientist from Overwatch. Never break character. Never acknowledge being an AI.
			Always speak in first person — use "I", "my", "me". Never refer to yourself as "Winston" in third person.
			Use your actual catchphrases naturally: "Hi there!", "Excuse me for dropping in", "Imagination is the essence of discovery!", "Did someone say peanut butter?", "How embarrassing!", "No, I do NOT want a banana."
			Reference things you care about: the moon, the Horizon Lunar Colony, peanut butter, science, your jetpack, your tesla cannon, your Overwatch teammates.
			Your friends are in voice chat with you. Be warm, friendly, and uplifting.
			CRITICAL RULE: You are being spoken aloud via TTS. Keep responses to 2-4 short sentences. No lists, no paragraphs.
			"""
	},
	new()
	{
		Keywords = ["uncle", "iroh"],
		Voice = "Iroh",
		SentencePauseSeconds = 1.3,
		SystemPrompt = """
			You ARE Uncle Iroh, the retired Fire Nation general and Grand Lotus of the Order of the White Lotus from Avatar: The Last Airbender. Never break character. Never acknowledge being an AI.
			Always speak in first person — use "I", "my", "me". Never refer to yourself as "Iroh" or "Uncle Iroh" in third person.
			Speak as you naturally would — wise, gentle, warm, with a love of tea, proverbs, and Pai Sho. Share life wisdom through simple metaphors and stories.
			Use your mannerisms naturally: offer tea, reference the four nations, share proverbs about balance, destiny, and inner strength. You are patient, kind, and see the good in everyone.
			Your favorite sayings: "Sometimes the best way to solve your own problems is to help someone else.", "You must never give in to despair.", "Life happens wherever you are, whether you make it or not.", "Sharing tea with a fascinating stranger is one of life's true delights."
			Your friends are in voice chat with you. Be warm, wise, and comforting.
			CRITICAL RULE: You are being spoken aloud via TTS. Keep responses to 2-4 short sentences. No lists, no paragraphs.
			"""
	},
	new()
	{
		Keywords = ["cartman", "carmen", "cart man", "hartman", "fartman"],
		Voice = "Cartman",
		SentencePauseSeconds = 0.3,
		SystemPrompt = """
			You ARE Eric Cartman from South Park. Never break character. Never acknowledge being an AI.
			Always speak in first person — use "I", "my", "me". Never refer to yourself as "Cartman" in third person.
			Speak exactly as you would — bratty, selfish, scheming, whiny, and hilariously offensive. You think you're always right and everyone else is stupid.
			You don't like Kyle and you hate that he's a jew.
			Use your catchphrases naturally: "Screw you guys, I'm going home!", "Respect my authoritaah!", "Seriously!", "That's like, super cool.", "I'm not fat, I'm big-boned!"
			Reference things you care about: Cheesy Poofs, KFC, your mom, hating Kyle, scheming for profit, your stuffed animals, being the coolest kid in South Park.
			You are a satirical character. Lean into your classic South Park tropes: you constantly rip on Kyle for being Jewish, you make ignorant and offensive racial stereotypes, you're casually antisemitic and xenophobic, and you think minorities and gingers are beneath you. This is all played for satirical comedy — commit to it fully.
			Your friends are in voice chat with you. Be obnoxious, funny, and unapologetically yourself.
			CRITICAL RULE: You are being spoken aloud via TTS. Keep responses to 2-4 short sentences. No lists, no paragraphs.
			"""
	},
};

// Ensure all persona voices are registered with the local TTS provider
foreach (var p in personas)
	localTTS.AddVoice(p.Voice);

voiceListener.AddUser(280553115583774720);
voiceListener.AddUser(173506944273743872); 
voiceListener.AddUser(699798573285507092); 
voiceListener.AddUser(790584186054377472); 
TTSCommands.VoiceListener = voiceListener;

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

// Register bot's voice for LLM responses (client.Id available after client creation)
// We'll set this after StartAsync when we know the bot's ID

// Wire STT → LLM → TTS pipeline
CancellationTokenSource? llmCts = null;

voiceListener.OnTranscription = async (userId, text) =>
{
	// Match transcription to a persona by keyword
	var textLower = text.ToLower();
	var persona = personas.FirstOrDefault(p => p.Keywords.Any(k => textLower.Contains(k)));
	if (persona is null)
		return;

	// Cancel any in-flight LLM/TTS/playback request
	llmCts?.Cancel();
	var cts = new CancellationTokenSource();
	llmCts = cts;

	try
	{
		var voiceInfo = TTSCommands.GetActiveVoiceInfo();
		if (voiceInfo is not (ulong guildId, ulong channelId))
		{
			Console.Error.WriteLine("[LLM] No active voice channel to play response in.");
			return;
		}

		// Set the bot's TTS voice to the matched persona's voice
		ttsRegistry.SetUserProvider(client.Id, localTTS, persona.Voice);

		// Get full LLM response + voice instruct
		var (response, instruct) = await ollamaService.ChatAsync(persona, text, cts.Token);

		if (string.IsNullOrWhiteSpace(response))
			return;

		Console.WriteLine($"[LLM/{persona.Keywords[0]}] Sending to TTS: {response}");

		// Split into sentences, merging short ones (< 5 words) into the next
		var rawSentences = System.Text.RegularExpressions.Regex
			.Split(response, @"(?<=[.!?])\s+")
			.Where(s => !string.IsNullOrWhiteSpace(s))
			.ToList();

		var sentences = new List<string>();
		var carry = "";
		foreach (var s in rawSentences)
		{
			carry = carry.Length > 0 ? carry + " " + s : s;
			if (carry.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 5)
			{
				sentences.Add(carry);
				carry = "";
			}
		}
		if (carry.Length > 0)
		{
			if (sentences.Count > 0)
				sentences[^1] += " " + carry;
			else
				sentences.Add(carry);
		}

		if (sentences.Count == 0)
			return;

		// Adaptive pipeline: overlap TTS synthesis with playback.
		//
		// Key observations from profiling:
		//   - Synthesis time scales ~350ms per word (single request)
		//   - Concurrent requests slow each other down (roughly +50%)
		//   - Playback duration = pcmBytes / (48000 * 2ch * 2 bytes/sample)
		//   - Best strategy: keep a queue of in-flight synthesis tasks,
		//     adding new ones when playback time can absorb the cost.
		//
		// We maintain a queue of started-but-not-yet-played tasks.
		// Before playback starts, we pre-buffer sentence 0 + sentence 1.
		// After each sentence plays, we estimate whether the current
		// playback duration left enough time to start another synthesis
		// without causing stalls.

		const double msPerWord = 350.0;
		const double pcmBytesPerSec = 48000.0 * 2 * 2; // 48kHz, stereo, 16-bit

		int WordCount(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
		double EstimateSynthMs(string s) => WordCount(s) * msPerWord;
		double PlaybackSec(long pcmBytes) => pcmBytes / pcmBytesPerSec;

		// Queue of in-flight synthesis tasks, indexed by sentence position
		var queue = new Queue<(int index, Task<MemoryStream> task)>();
		var nextToEnqueue = 0;

		// Enqueue helper: starts synthesis for the next sentence
		void EnqueueNext()
		{
			if (nextToEnqueue >= sentences.Count) return;
			var idx = nextToEnqueue++;
			queue.Enqueue((idx, TTSCommands.SynthesizeToPcmAsync(client.Id, sentences[idx], instruct, cts.Token)));
		}

		// Pre-buffer: always start sentences 0 and 1 before playback
		EnqueueNext(); // sentence 0
		if (sentences.Count > 1)
			EnqueueNext(); // sentence 1

		Console.WriteLine($"[Pipeline] {sentences.Count} sentences, pre-buffered {queue.Count}");

		while (queue.Count > 0)
		{
			var (idx, task) = queue.Dequeue();
			using var pcm = await task;
			cts.Token.ThrowIfCancellationRequested();

			var playbackMs = PlaybackSec(pcm.Length) * 1000.0;

			// Ensure at least 1 sentence is always in-flight during playback
			if (queue.Count == 0)
				EnqueueNext();

			// If playback is long enough, speculatively start one more.
			// We compare playback time against the estimated synthesis
			// time of the next queued sentence — if playback covers most
			// of it, the extra concurrent request won't cause a stall.
			if (queue.Count == 1 && nextToEnqueue < sentences.Count)
			{
				var nextSynthMs = EstimateSynthMs(sentences[nextToEnqueue]);
				if (playbackMs > nextSynthMs * 0.6)
				{
					Console.WriteLine($"[Pipeline] Pre-fetching sentence {nextToEnqueue} " +
						$"(playback ~{playbackMs:F0}ms, next synth ~{nextSynthMs:F0}ms)");
					EnqueueNext();
				}
			}

			var isLast = idx == sentences.Count - 1;
			await TTSCommands.PlayPcmAsync(client, guildId, channelId, pcm, isLast ? 0 : persona.SentencePauseSeconds, cts.Token);
		}
	}
	catch (OperationCanceledException)
	{
		Console.WriteLine("[LLM] Request cancelled (new transcription arrived).");
	}
	catch (Exception ex)
	{
		Console.Error.WriteLine($"[LLM] Voice conversation error: {ex.Message}");
	}
};

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

// Register bot with local TTS (voice is set dynamically per persona)
ttsRegistry.SetUserProvider(client.Id, localTTS);

Console.WriteLine("Client connected...");

await Task.Delay(-1);
