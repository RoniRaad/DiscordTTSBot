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

				// Write in frame-aligned chunks for smooth pacing
				const int chunkSize = 3840 * 10; // 10 opus frames = 200ms
				var buffer = new byte[chunkSize];
				int bytesRead;
				while ((bytesRead = await pcmStream.ReadAsync(buffer, cancellationToken)) > 0)
					await opusStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);

				if (pauseSeconds > 0)
				{
					var silenceBytes = (int)(48000 * 2 * 2 * pauseSeconds);
					var silenceBuffer = new byte[Math.Min(silenceBytes, chunkSize)];
					var remaining = silenceBytes;
					while (remaining > 0)
					{
						var toWrite = Math.Min(remaining, silenceBuffer.Length);
						await opusStream.WriteAsync(silenceBuffer.AsMemory(0, toWrite), cancellationToken);
						remaining -= toWrite;
					}
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

		/// <summary>
		/// Opens a persistent voice/opus stream for the guild/channel.
		/// Call PlayOnStream() to write PCM data, then DisposeStream() when done.
		/// This avoids creating/destroying streams between sentences.
		/// </summary>
		public static async Task<VoicePlaybackSession> OpenPlaybackSessionAsync(
			GatewayClient client, ulong guildId, ulong channelId,
			CancellationToken cancellationToken = default)
		{
			await _voiceLock.WaitAsync(CancellationToken.None);
			try
			{
				var voiceClient = await EnsureVoiceClientAsync(client, guildId, channelId);
				await voiceClient.EnterSpeakingStateAsync(new SpeakingProperties(SpeakingFlags.Microphone));

				var voiceStream = voiceClient.CreateVoiceStream();
				var opusStream = new OpusEncodeStream(voiceStream, PcmFormat.Short, VoiceChannels.Stereo, OpusApplication.Voip);

				return new VoicePlaybackSession(voiceStream, opusStream, _voiceLock);
			}
			catch
			{
				_voiceLock.Release();
				throw;
			}
		}

		public sealed class VoicePlaybackSession : IAsyncDisposable
		{
			private readonly IDisposable _voiceStream;
			private readonly OpusEncodeStream _opusStream;
			private readonly SemaphoreSlim _lock;
			private bool _disposed;

			// Write in chunks of exactly 10 opus frames (10 * 20ms = 200ms).
			// Each PCM frame at 48kHz stereo s16le = 3840 bytes.
			// This keeps writes small enough that the SpeedNormalizingStream
			// can pace them smoothly without building up a large backlog
			// that causes burst/catch-up stuttering.
			private const int FrameSize = 3840; // 20ms of PCM at 48kHz stereo s16le
			private const int FramesPerChunk = 10;
			private const int ChunkSize = FrameSize * FramesPerChunk; // 38400 bytes = 200ms

			internal VoicePlaybackSession(IDisposable voiceStream, OpusEncodeStream opusStream, SemaphoreSlim voiceLock)
			{
				_voiceStream = voiceStream;
				_opusStream = opusStream;
				_lock = voiceLock;
			}

			/// <summary>
			/// Writes a PCM stream to the ongoing opus stream in frame-aligned chunks,
			/// with optional silence pause after.
			/// </summary>
			public async Task PlayPcmAsync(MemoryStream pcmStream, double pauseSeconds = 0, CancellationToken cancellationToken = default)
			{
				pcmStream.Position = 0;
				// Write in small frame-aligned chunks so the SpeedNormalizingStream
				// can pace output smoothly without large bursts after delays.
				var buffer = new byte[ChunkSize];
				int bytesRead;
				while ((bytesRead = await pcmStream.ReadAsync(buffer, cancellationToken)) > 0)
				{
					await _opusStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
				}

				if (pauseSeconds > 0)
				{
					// Write silence in chunks too, to avoid one large allocation
					// and to keep the pacing smooth.
					var silenceBytes = (int)(48000 * 2 * 2 * pauseSeconds);
					var silenceBuffer = new byte[Math.Min(silenceBytes, ChunkSize)];
					var remaining = silenceBytes;
					while (remaining > 0)
					{
						var toWrite = Math.Min(remaining, silenceBuffer.Length);
						await _opusStream.WriteAsync(silenceBuffer.AsMemory(0, toWrite), cancellationToken);
						remaining -= toWrite;
					}
				}
			}

			public async ValueTask DisposeAsync()
			{
				if (_disposed) return;
				_disposed = true;

				try
				{
					await _opusStream.FlushAsync();
					_opusStream.Dispose();
					_voiceStream.Dispose();
				}
				finally
				{
					_lock.Release();
				}
			}
		}
	}
}
