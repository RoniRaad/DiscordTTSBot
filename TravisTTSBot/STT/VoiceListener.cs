using NetCord.Gateway.Voice;

namespace DiscordTTSBot.STT
{
	public class VoiceListener
	{
		private readonly TranscriptionService _transcriptionService;
		private readonly Dictionary<uint, MemoryStream> _buffers = new();
		private readonly Dictionary<uint, OpusDecoder> _decoders = new();
		private readonly Dictionary<uint, DateTime> _lastFrameTime = new();
		private readonly HashSet<ulong> _listenedUsers = new();
		private readonly Timer _silenceTimer;

		// 48kHz * 2 channels * 2 bytes * 0.1s = ~19200 bytes
		private const int MinBufferBytes = 19200;
		// 20ms frame at 48kHz stereo = 960 samples/channel * 2 channels * 2 bytes
		private const int MaxPcmFrameBytes = 960 * 2 * 2;
		private static readonly TimeSpan SilenceThreshold = TimeSpan.FromSeconds(0.8);

		/// <summary>
		/// Called when a user's speech has been transcribed.
		/// Parameters: userId, transcribed text.
		/// </summary>
		public Func<ulong, string, Task>? OnTranscription { get; set; }

		public VoiceListener(TranscriptionService transcriptionService)
		{
			_transcriptionService = transcriptionService;
			_silenceTimer = new Timer(CheckSilence, null, 500, 500);
		}

		public bool AddUser(ulong userId)
		{
			lock (_listenedUsers)
				return _listenedUsers.Add(userId);
		}

		public bool RemoveUser(ulong userId)
		{
			lock (_listenedUsers)
				return _listenedUsers.Remove(userId);
		}

		public bool IsListening(ulong userId)
		{
			lock (_listenedUsers)
				return _listenedUsers.Contains(userId);
		}

		public void OnVoiceReceive(VoiceClient voiceClient, VoiceReceiveEventArgs args)
		{
			var ssrc = args.Ssrc;
			var frame = args.Frame;

			if (frame.Length == 0)
				return;

			// Filter: only buffer audio from users we're listening to
			if (!voiceClient.Cache.SsrcUsers.TryGetValue(ssrc, out var userId))
				return;

			lock (_listenedUsers)
			{
				if (!_listenedUsers.Contains(userId))
					return;
			}

			lock (_buffers)
			{
				if (!_decoders.TryGetValue(ssrc, out var decoder))
				{
					decoder = new OpusDecoder(VoiceChannels.Stereo);
					_decoders[ssrc] = decoder;
					_ssrcToUser[ssrc] = userId;
					Console.WriteLine($"[STT] Started buffering audio from user {userId} (SSRC {ssrc})");
				}

				if (!_buffers.TryGetValue(ssrc, out var buffer))
				{
					buffer = new MemoryStream();
					_buffers[ssrc] = buffer;
				}

				// Decode Opus frame to PCM
				Span<byte> pcmFrame = stackalloc byte[MaxPcmFrameBytes];
				var decoded = decoder.Decode(frame, pcmFrame, 960);
				var pcmBytes = decoded * 2 * 2; // samples * channels * sizeof(short)
				buffer.Write(pcmFrame[..pcmBytes]);
				_lastFrameTime[ssrc] = DateTime.UtcNow;
			}
		}

		// Maps SSRC → userId so we can resolve after the lock
		private readonly Dictionary<uint, ulong> _ssrcToUser = new();

		private void CheckSilence(object? state)
		{
			List<(uint ssrc, ulong userId, byte[] pcm)>? completed = null;
			List<uint>? expired = null;

			lock (_buffers)
			{
				var now = DateTime.UtcNow;
				foreach (var (ssrc, lastTime) in _lastFrameTime)
				{
					if (now - lastTime < SilenceThreshold)
						continue;

					expired ??= new();
					expired.Add(ssrc);

					if (!_buffers.TryGetValue(ssrc, out var buffer))
						continue;

					if (buffer.Length >= MinBufferBytes && _ssrcToUser.TryGetValue(ssrc, out var userId))
					{
						completed ??= new();
						completed.Add((ssrc, userId, buffer.ToArray()));
					}

					buffer.Dispose();
					_buffers.Remove(ssrc);
					_ssrcToUser.Remove(ssrc);

					if (_decoders.Remove(ssrc, out var decoder))
						decoder.Dispose();
				}

				if (expired is not null)
				{
					foreach (var ssrc in expired)
						_lastFrameTime.Remove(ssrc);
				}
			}

			if (completed is null)
				return;

			// Fire and forget transcriptions outside the lock
			foreach (var (ssrc, userId, pcmData) in completed)
			{
				_ = Task.Run(async () =>
				{
					try
					{
						var duration = pcmData.Length / (48000.0 * 2 * 2);
						Console.WriteLine($"[STT] Sending {duration:F1}s of audio from user {userId} for transcription...");
						var text = await _transcriptionService.TranscribeAsync(pcmData);
						if (!string.IsNullOrWhiteSpace(text))
						{
							Console.WriteLine($"[STT] User {userId}: {text}");
							if (text.Trim() == "Thank you.")
								return;

							if (OnTranscription is not null)
								await OnTranscription(userId, text);
						}
						else
						{
							Console.WriteLine($"[STT] User {userId}: (empty transcription)");
						}
					}
					catch (Exception ex)
					{
						Console.Error.WriteLine($"[STT] Transcription failed for user {userId}: {ex.Message}");
					}
				});
			}
		}
	}
}
