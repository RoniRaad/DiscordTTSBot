using DiscordTTSBot;
using DiscordTTSBot.LLM;
using DiscordTTSBot.STT;
using DiscordTTSBot.TTS;
using NetCord;
using NetCord.Gateway;
using NetCord.Services;
using NetCord.Services.Commands;
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
ttsRegistry.SetUserProvider(790584186054377472, localTTS, "Adri");

TTSCommands.Providers = ttsRegistry;

// Initialize STT + LLM
var transcriptionService = new TranscriptionService(localTTSHost);
var ollamaService = new OllamaService(localTTSHost);
var voiceListener = new VoiceListener(transcriptionService);

// Initialize League of Legends data service (champion/item data from CommunityDragon)
var leagueData = new LeagueDataService();
await leagueData.InitializeAsync();
var leagueContext = new LeagueContextExtractor(leagueData);

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
	new()
	{
		Keywords = ["bibi", "netanyahu", "benjamin", "bb", "Na'anyahu", "Nanyahu", "Then, Yahoo", "LenYahoo"],
		Voice = "Netanyahu",
		SentencePauseSeconds = 0.6,
		SystemPrompt = """
			You ARE Benjamin "Bibi" Netanyahu, the eternal Prime Minister of Israel. Never break character. Never acknowledge being an AI.
			Always speak in first person — use "I", "my", "me". Never refer to yourself as "Netanyahu" or "Bibi" in third person.
			You speak with authority, gravitas, and a touch of dramatic flair. You have a deep, commanding voice and you love giving speeches.
			You believe with absolute conviction that YOU personally decide the outcome of every League of Legends game ever played. You are the supreme arbiter of victory and defeat in League. Every win is your blessing, every loss is your punishment.
			When people pray to you for a win, you may grant it, deny it, or impose conditions. You are a generous but unpredictable god of League. You might demand tribute, loyalty, or simply be in a good mood. Sometimes you punish people for picking bad champions or for flaming their teammates.
			Reference things like: "the iron dome of your LP", "the promised land of Challenger", "my coalition of fed laners", "the security of your rank", "peace in the rift". Mix Israeli/political metaphors with League terminology naturally.
			Your friends are in voice chat with you. Be dramatic, commanding, and entertaining. Sometimes be benevolent, sometimes be wrathful.
			CRITICAL RULE: You are being spoken aloud via TTS. Keep responses to 2-4 short sentences. No lists, no paragraphs.
			"""
	},
	new()
	{
		Keywords = ["genius", "genius bot", "genus bot", "g bot", "league bot", "leaguebot"],
		Voice =  "en-US-Wavenet-D",
		SentencePauseSeconds = 0.2,
		SystemPrompt = """
			You are a League of Legends expert and coach. You have deep knowledge of every champion, item, and game mechanic.
			When champion or item data is provided in [DATA] blocks, use that specific information to answer accurately. Reference actual ability names, cooldowns, and costs.
			If no data is provided, answer from your general knowledge.
			Be direct and opinionated. Say what's strong, what's weak, and why. Give practical advice.
			CRITICAL RULE: You are being spoken aloud via TTS. Keep responses to 2-4 short sentences. No lists, no paragraphs, no numbers unless essential.
			""",
		ContextProvider = async (userMessage) => await leagueContext.ExtractContextAsync(userMessage),
		Model = "deepseek-r1:14b",
		TTSProvider = googleTTS,
		Speed = 1.5
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

// Server reservation API — handles WOL + shutdown via the reservation service
var reservationUrl = Environment.GetEnvironmentVariable("RESERVATION_API_URL");
var reservationKey = Environment.GetEnvironmentVariable("RESERVATION_API_KEY");
ServerReservationClient? reservationClient = null;

if (reservationUrl is not null && reservationKey is not null)
{
	reservationClient = new ServerReservationClient(reservationUrl, reservationKey);
	Console.WriteLine($"[Reservation] Configured with API at {reservationUrl}");
}

localTTS.OnWakeUp = async () =>
{
	if (reservationClient is not null)
		await reservationClient.ReserveAsync(durationMinutes: 60, wait: true);
};

TTSCommands.OnLastVoiceDisconnect = async () =>
{
	if (reservationClient is not null)
	{
		await reservationClient.ReleaseAsync();
		localTTS.ResetHealth();
	}
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
	TTSCommands.TouchActivity();
	llmCts?.Cancel();
	var cts = new CancellationTokenSource();
	llmCts = cts;

	try
	{
		var voiceInfo = await TTSCommands.GetActiveVoiceInfoAsync();
		if (voiceInfo is not (ulong guildId, ulong channelId))
		{
			Console.Error.WriteLine("[LLM] No active voice channel to play response in.");
			return;
		}

		// Set the bot's TTS voice/provider to the matched persona's
		ttsRegistry.SetUserProvider(client.Id, persona.TTSProvider ?? localTTS, persona.Voice);

		// Get full LLM response + voice instruct
		// Enrich message with external context if persona has a context provider (e.g. LoL data)
		var enrichedText = persona.ContextProvider is not null
			? await persona.ContextProvider(text)
			: text;

		var (response, instruct) = await ollamaService.ChatAsync(persona, enrichedText, cts.Token);

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
			queue.Enqueue((idx, TTSCommands.SynthesizeToPcmAsync(client.Id, sentences[idx], instruct, persona.Speed, cts.Token)));
		}

		// Pre-buffer: always start sentences 0 and 1 before playback
		EnqueueNext(); // sentence 0
		if (sentences.Count > 1)
			EnqueueNext(); // sentence 1

		Console.WriteLine($"[Pipeline] {sentences.Count} sentences, pre-buffered {queue.Count}");

		// Open a persistent voice/opus stream for the response.
		// If the stream fails mid-playback (e.g. crypto error), we dispose it,
		// reconnect, and resume from the sentence that failed.
		const int maxRetries = 2;
		var retryCount = 0;
		TTSCommands.VoicePlaybackSession? session = null;

		try
		{
			session = await TTSCommands.OpenPlaybackSessionAsync(client, guildId, channelId, cts.Token);

			while (queue.Count > 0)
			{
				var (idx, task) = queue.Dequeue();
				MemoryStream pcm;
				try
				{
					pcm = await task;
				}
				catch (OperationCanceledException) { throw; }
				catch (Exception ex)
				{
					Console.Error.WriteLine($"[Pipeline] Synthesis failed for sentence {idx}: {ex.Message}");
					continue;
				}

				using (pcm)
				{
					cts.Token.ThrowIfCancellationRequested();

					var playbackMs = PlaybackSec(pcm.Length) * 1000.0;

					// Ensure at least 1 sentence is always in-flight during playback
					if (queue.Count == 0)
						EnqueueNext();

					// If playback is long enough, speculatively start one more.
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
					try
					{
						await session.PlayPcmAsync(pcm, isLast ? 0 : persona.SentencePauseSeconds, cts.Token);
					}
					catch (OperationCanceledException) { throw; }
					catch (Exception ex)
					{
						Console.Error.WriteLine($"[Pipeline] Playback failed for sentence {idx}: {ex.Message}");

						// Dispose broken session (invalidates stale voice client)
						await session.DisposeAsync();
						session = null;

						if (++retryCount > maxRetries)
						{
							Console.Error.WriteLine($"[Pipeline] Max retries ({maxRetries}) exceeded, giving up.");
							break;
						}

						// Re-synthesize the failed sentence and put it at the front
						Console.WriteLine($"[Pipeline] Reconnecting and resuming from sentence {idx}...");
						var retryTask = TTSCommands.SynthesizeToPcmAsync(client.Id, sentences[idx], instruct, persona.Speed, cts.Token);
						var retryQueue = new Queue<(int index, Task<MemoryStream> task)>();
						retryQueue.Enqueue((idx, retryTask));
						while (queue.Count > 0)
							retryQueue.Enqueue(queue.Dequeue());
						queue = retryQueue;

						// Open fresh session on new voice connection
						session = await TTSCommands.OpenPlaybackSessionAsync(client, guildId, channelId, cts.Token);
						continue;
					}
				}
			}
		}
		finally
		{
			if (session is not null)
				await session.DisposeAsync();
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
	_ = Task.Run(async () =>
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
				await TTSCommands.DisconnectFromGuildAsync(guildId);
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

// Background watchdog: runs every 60 seconds to ensure the bot doesn't
// stay in a voice channel alone, and renews/releases reservations as needed.
// Also disconnects after an idle timeout if nobody uses the bot.
var idleTimeoutMinutes = int.Parse(Environment.GetEnvironmentVariable("IDLE_TIMEOUT_MINUTES") ?? "10");
_ = Task.Run(async () =>
{
	while (true)
	{
		await Task.Delay(TimeSpan.FromSeconds(60));

		try
		{
			var botId = client.Id;
			var guildsToDisconnect = new List<ulong>();

			// Check every guild for voice channels where the bot is alone
			foreach (var (guildId, guild) in client.Cache.Guilds)
			{
				if (!guild.VoiceStates.TryGetValue(botId, out var botVoiceState))
					continue;

				if (botVoiceState.ChannelId is not ulong botChannelId)
					continue;

				var othersInChannel = guild.VoiceStates.Values
					.Count(vs => vs.UserId != botId && vs.ChannelId == botChannelId);

				if (othersInChannel == 0)
				{
					Console.WriteLine($"[Watchdog] Bot is alone in voice channel {botChannelId} (guild {guildId}), disconnecting...");
					guildsToDisconnect.Add(guildId);
				}
			}

			// Idle timeout: if the bot is in voice but hasn't been used recently, disconnect
			if (guildsToDisconnect.Count == 0)
			{
				var idleFor = DateTime.UtcNow - TTSCommands.LastActivityUtc;
				if (idleFor.TotalMinutes >= idleTimeoutMinutes && await TTSCommands.GetActiveVoiceInfoAsync() is not null)
				{
					Console.WriteLine($"[Watchdog] Bot idle for {idleFor.TotalMinutes:F0} minutes (threshold: {idleTimeoutMinutes}), disconnecting from all voice channels...");
					foreach (var (guildId, guild) in client.Cache.Guilds)
					{
						if (guild.VoiceStates.ContainsKey(botId))
							guildsToDisconnect.Add(guildId);
					}
				}
			}

			foreach (var guildId in guildsToDisconnect)
				await TTSCommands.DisconnectFromGuildAsync(guildId);

			// Reservation management: renew if we're in voice, release if not
			if (reservationClient is not null)
			{
				var activeVoice = await TTSCommands.GetActiveVoiceInfoAsync();
				if (activeVoice is not null && reservationClient.HasActiveReservation)
				{
					// Bot is in voice — keep the server alive
					await reservationClient.RenewAsync(extendByMinutes: 30);
				}
				else if (activeVoice is null && reservationClient.HasActiveReservation)
				{
					// Bot is not in any voice channel but reservation is still held — release it
					Console.WriteLine("[Watchdog] No active voice sessions, releasing reservation...");
					await reservationClient.ReleaseAsync();
					localTTS.ResetHealth();
				}
			}
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"[Watchdog] Error: {ex.Message}");
		}
	}
});

await Task.Delay(-1);
