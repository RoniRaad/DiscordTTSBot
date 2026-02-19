using System.Net.Http.Json;

namespace DiscordTTSBot.TTS
{
	public class LocalApiTTSProvider : ITTSProvider
	{
		private readonly HttpClient _httpClient;
		private readonly string _baseUrl;
		private readonly string _voiceRefDir;
		private bool _healthy;
		private bool _wokenUp;

		public string Name => "local";
		public string DefaultVoice => "Trav";
		public Func<Task>? OnWakeUp { get; set; }

		public void ResetHealth()
		{
			_healthy = false;
			_wokenUp = false;
		}

		public LocalApiTTSProvider(string baseUrl = "http://192.168.1.67:8880", string voiceRefDir = "VoiceRefs")
		{
			_baseUrl = baseUrl.TrimEnd('/');
			_voiceRefDir = Path.GetFullPath(voiceRefDir);
			_httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

			if (!Directory.Exists(_voiceRefDir))
				Directory.CreateDirectory(_voiceRefDir);
		}

		public async Task WaitForHealthyAsync(CancellationToken cancellationToken = default)
		{
			if (_healthy)
				return;

			if (!_wokenUp && OnWakeUp is not null)
			{
				_wokenUp = true;
				try { await OnWakeUp(); }
				catch (Exception ex) { Console.Error.WriteLine($"OnWakeUp failed: {ex.Message}"); }
			}

			Console.WriteLine($"Waiting for local TTS server at {_baseUrl}...");

			while (!cancellationToken.IsCancellationRequested)
			{
				try
				{
					using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
					cts.CancelAfter(TimeSpan.FromSeconds(5));
					var response = await _httpClient.GetAsync($"{_baseUrl}/v1/models", cts.Token);
					if (response.IsSuccessStatusCode)
					{
						_healthy = true;
						Console.WriteLine("Local TTS server is ready.");
						return;
					}
				}
				catch (Exception) when (!cancellationToken.IsCancellationRequested)
				{
					// Server not up yet
				}

				await Task.Delay(5000, cancellationToken);
			}
		}

		public async Task<Stream> SynthesizeAsync(string text, string voice)
		{
			if (!_healthy)
				await WaitForHealthyAsync();

			if (!IsValidVoice(voice))
				voice = DefaultVoice;

			var refAudioPath = GetRefAudioPath(voice);
			if (refAudioPath is null)
				throw new InvalidOperationException($"No reference audio found for voice '{voice}'");

			var refAudioBytes = await File.ReadAllBytesAsync(refAudioPath);
			var refAudioBase64 = Convert.ToBase64String(refAudioBytes);

			var refTextPath = Path.Combine(_voiceRefDir, Path.GetFileNameWithoutExtension(refAudioPath) + ".txt");
			var refText = File.Exists(refTextPath) ? await File.ReadAllTextAsync(refTextPath) : null;

			using var requestClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
			var response = await requestClient.PostAsJsonAsync($"{_baseUrl}/v1/audio/voice-clone", new
			{
				input = text,
				ref_audio = refAudioBase64,
				ref_text = refText,
				x_vector_only_mode = refText is null,
				response_format = "mp3",
				speed = 1.0
			});

			response.EnsureSuccessStatusCode();

			var stream = new MemoryStream();
			await response.Content.CopyToAsync(stream);
			stream.Position = 0;
			return stream;
		}

		public bool IsValidVoice(string voice) => GetRefAudioPath(voice) is not null;

		public IReadOnlyList<string> GetAvailableVoices()
		{
			if (!Directory.Exists(_voiceRefDir))
				return [];

			return Directory.GetFiles(_voiceRefDir)
				.Where(f => IsAudioFile(f))
				.Select(f => Path.GetFileNameWithoutExtension(f))
				.ToList();
		}

		private string? GetRefAudioPath(string voice)
		{
			if (!Directory.Exists(_voiceRefDir))
				return null;

			return Directory.GetFiles(_voiceRefDir)
				.FirstOrDefault(f => IsAudioFile(f)
					&& string.Equals(Path.GetFileNameWithoutExtension(f), voice, StringComparison.OrdinalIgnoreCase));
		}

		private static bool IsAudioFile(string path)
		{
			var ext = Path.GetExtension(path).ToLowerInvariant();
			return ext is ".wav" or ".mp3" or ".flac" or ".ogg" or ".opus";
		}
	}
}
