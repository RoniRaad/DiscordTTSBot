using DiscordTTSBot.Static;
using DiscordTTSBot.STT;
using DiscordTTSBot.TTS;
using NetCord.Gateway;
using NetCord.Gateway.Voice;
using NetCord.Services.Commands;
using NetCord.Rest;
using TTSBot.Static;

namespace TTSBot.Modules
{
	public class TTSCommands : CommandModule<CommandContext>
	{
		public static TTSProviderRegistry Providers { get; set; } = null!;
		public static GatewayClient Client { get; set; } = null!;

		[Command("t")]
		public async Task PlayTTS([CommandParameter(Remainder = true)] string words)
		{
			if (Context.Message.GuildId is not ulong guildId)
				return;

			var guild = Context.Client.Cache.Guilds[guildId];
			if (!guild.VoiceStates.TryGetValue(Context.User.Id, out var voiceState))
				return;

			if (voiceState.ChannelId is not ulong channelId)
				return;

			await PlayTTSAsync(Context.Client, guildId, channelId, Context.User.Id, words, Context.Message.ChannelId);
		}

		[Command("voices")]
		public async Task VoicesCommand()
		{
			var provider = Providers.GetProviderForUser(Context.User.Id);
			var voices = provider.GetAvailableVoices();
			var sample = string.Join(", ", voices.Take(20));
			var msg = $"Using provider: **{provider.Name}** ({voices.Count} voices)\nExamples: {sample}";
			if (voices.Count > 20)
				msg += $"\n...and {voices.Count - 20} more.";
			await Context.Message.ReplyAsync(msg);
		}

		[Command("setvoice")]
		public async Task SetVoice([CommandParameter(Remainder = true)] string voice)
		{
			var provider = Providers.GetProviderForUser(Context.User.Id);
			if (!provider.IsValidVoice(voice))
			{
				await Context.Message.ReplyAsync("Error: Given voice is not valid!");
				return;
			}

			await UserSettingsHelper.SetUserVoice(Context.User.Id, voice);
			await Context.Message.ReplyAsync("Voice set successfully!");
		}

		private static readonly SemaphoreSlim _voiceLock = new(1, 1);
		private static readonly Dictionary<ulong, VoiceClient> _voiceClients = new();

		public static VoiceListener? VoiceListener { get; set; }
		public static Func<Task>? OnLastVoiceDisconnect { get; set; }

		public static (ulong guildId, ulong channelId)? GetActiveVoiceInfo()
		{
			_voiceLock.Wait();
			try
			{
				foreach (var (guildId, voiceClient) in _voiceClients)
				{
					if (voiceClient.ChannelId is ulong channelId)
						return (guildId, channelId);
				}
				return null;
			}
			finally
			{
				_voiceLock.Release();
			}
		}

		public static void DisconnectFromGuild(ulong guildId)
		{
			_voiceLock.Wait();
			try
			{
				if (_voiceClients.Remove(guildId, out var voiceClient))
				{
					voiceClient.Dispose();

					// Tell Discord to leave the voice channel
					_ = Client.UpdateVoiceStateAsync(new VoiceStateProperties(guildId, null));

					if (_voiceClients.Count == 0 && OnLastVoiceDisconnect is not null)
					{
						_ = Task.Run(async () =>
						{
							try { await OnLastVoiceDisconnect(); }
							catch (Exception ex) { Console.Error.WriteLine($"OnLastVoiceDisconnect failed: {ex.Message}"); }
						});
					}
				}
			}
			finally
			{
				_voiceLock.Release();
			}
		}

		/// <summary>
		/// Ensures a voice client exists for the guild/channel, creating one if needed.
		/// Must be called while holding _voiceLock.
		/// </summary>
		private static async Task<VoiceClient> EnsureVoiceClientAsync(GatewayClient client, ulong guildId, ulong channelId)
		{
			if (_voiceClients.TryGetValue(guildId, out var existingClient)
				&& existingClient.ChannelId == channelId)
				return existingClient;

			if (existingClient is not null)
			{
				existingClient.Dispose();
				_voiceClients.Remove(guildId);
			}

			var voiceConfig = VoiceListener is not null
				? new VoiceClientConfiguration { ReceiveHandler = new VoiceReceiveHandler() }
				: null;

			var voiceClient = await client.JoinVoiceChannelAsync(guildId, channelId, voiceConfig);
			try { await voiceClient.StartAsync(); }
			catch (Exception ex) { Console.WriteLine($"An error occured: {ex.Message}"); }

			if (VoiceListener is VoiceListener listener)
			{
				Console.WriteLine("[STT] Voice receiving enabled, listening for audio...");
				voiceClient.VoiceReceive += args =>
				{
					listener.OnVoiceReceive(voiceClient, args);
					return default;
				};
			}

			_voiceClients[guildId] = voiceClient;
			return voiceClient;
		}

		public static async Task PlayTTSAsync(GatewayClient client, ulong guildId, ulong channelId, ulong userId, string words, ulong? textChannelId = null, CancellationToken cancellationToken = default)
		{
			Console.WriteLine($"Received tts command from user. Input: {words}");

			var provider = Providers.GetProviderForUser(userId);

			if (!provider.IsReady && textChannelId is ulong notifyChannelId)
			{
				try
				{
					await client.Rest.SendMessageAsync(notifyChannelId, new MessageProperties { Content = "Initializing TTS server, this may take up to two minutes..." });
				}
				catch { }
			}

			var voice = Providers.GetVoiceOverride(userId) ?? UserSettingsHelper.GetUserVoice(userId);
			using var audioStream = await provider.SynthesizeAsync(words, voice, null, cancellationToken);

			cancellationToken.ThrowIfCancellationRequested();

			await _voiceLock.WaitAsync(CancellationToken.None);
			try
			{
				var voiceClient = await EnsureVoiceClientAsync(client, guildId, channelId);
				await voiceClient.EnterSpeakingStateAsync(new SpeakingProperties(SpeakingFlags.Microphone));

				using var voiceStream = voiceClient.CreateVoiceStream();
				using var opusStream = new OpusEncodeStream(voiceStream, PcmFormat.Short, VoiceChannels.Stereo, OpusApplication.Voip);

				// Stream TTS audio through FFmpeg directly into the opus encoder
				await StreamHelpers.StreamToDiscordAudioFormat(audioStream, opusStream, cancellationToken);
				await opusStream.FlushAsync(cancellationToken);
			}
			catch (OperationCanceledException)
			{
				Console.WriteLine("[TTS] Playback cancelled.");
				throw;
			}
			catch (Exception ex)
			{
				Console.WriteLine($"An error occured: {ex.Message}");
			}
			finally
			{
				_voiceLock.Release();
			}
		}

		/// <summary>
		/// Synthesizes text to PCM audio (s16le stereo 48kHz) via the user's TTS provider + FFmpeg.
		/// </summary>
		public static async Task<MemoryStream> SynthesizeToPcmAsync(ulong userId, string text, string? instruct = null, CancellationToken cancellationToken = default)
		{
			var provider = Providers.GetProviderForUser(userId);
			var voice = Providers.GetVoiceOverride(userId) ?? UserSettingsHelper.GetUserVoice(userId);
			using var audioStream = await provider.SynthesizeAsync(text, voice, instruct, cancellationToken);
			var pcm = await StreamHelpers.ConvertToDiscordAudioFormat(audioStream, cancellationToken);
			pcm.Position = 0;
			return (MemoryStream)pcm;
		}

		/// <summary>
		/// Plays a single pre-synthesized PCM stream, optionally appending silence between sentences.
		/// </summary>
		public static async Task PlayPcmAsync(GatewayClient client, ulong guildId, ulong channelId, MemoryStream pcmStream, double pauseSeconds = 0, CancellationToken cancellationToken = default)
		{
			pcmStream.Position = 0;

			await _voiceLock.WaitAsync(CancellationToken.None);
			try
			{
				var voiceClient = await EnsureVoiceClientAsync(client, guildId, channelId);
				await voiceClient.EnterSpeakingStateAsync(new SpeakingProperties(SpeakingFlags.Microphone));

				using var voiceStream = voiceClient.CreateVoiceStream();
				using var opusStream = new OpusEncodeStream(voiceStream, PcmFormat.Short, VoiceChannels.Stereo, OpusApplication.Voip);

				await pcmStream.CopyToAsync(opusStream, cancellationToken);

				// Insert silence gap between sentences
				if (pauseSeconds > 0)
				{
					var silenceBytes = (int)(48000 * 2 * 2 * pauseSeconds);
					var silence = new byte[silenceBytes];
					await opusStream.WriteAsync(silence, cancellationToken);
				}

				await opusStream.FlushAsync(cancellationToken);
			}
			catch (OperationCanceledException)
			{
				Console.WriteLine("[TTS] Playback cancelled.");
				throw;
			}
			catch (Exception ex)
			{
				Console.WriteLine($"An error occured: {ex.Message}");
			}
			finally
			{
				_voiceLock.Release();
			}
		}
	}
}
