using System.Net.Http.Json;

namespace DiscordTTSBot.TTS
{
	public class LocalApiTTSProvider : ITTSProvider
	{
		private readonly HttpClient _httpClient;
		private readonly string _baseUrl;
		private readonly List<string> _availableVoices = new();
		private bool _healthy;
		private bool _wokenUp;

		public string Name => "local";
		public string DefaultVoice => "Trav";
		public bool IsReady => _healthy;
		public Func<Task>? OnWakeUp { get; set; }

		public void ResetHealth()
		{
			_healthy = false;
			_wokenUp = false;
		}

		public LocalApiTTSProvider(string baseUrl = "http://192.168.1.67:8880")
		{
			_baseUrl = baseUrl.TrimEnd('/');
			_httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
		}

		/// <summary>
		/// Registers a voice name that this provider supports.
		/// </summary>
		public void AddVoice(string voice)
		{
			if (!_availableVoices.Contains(voice, StringComparer.OrdinalIgnoreCase))
				_availableVoices.Add(voice);
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
					cts.CancelAfter(TimeSpan.FromSeconds(2));
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

		public async Task<Stream> SynthesizeAsync(string text, string voice, CancellationToken cancellationToken = default)
		{
			if (!_healthy)
				await WaitForHealthyAsync(cancellationToken);

			using var requestClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
			var response = await requestClient.PostAsJsonAsync($"{_baseUrl}/v1/audio/speech", new
			{
				model = "qwen3-tts",
				input = text,
				voice,
				response_format = "mp3",
				speed = 1.0
			}, cancellationToken);

			response.EnsureSuccessStatusCode();

			var stream = new MemoryStream();
			await response.Content.CopyToAsync(stream, cancellationToken);
			stream.Position = 0;
			return stream;
		}

		public bool IsValidVoice(string voice) =>
			_availableVoices.Contains(voice, StringComparer.OrdinalIgnoreCase);

		public IReadOnlyList<string> GetAvailableVoices() => _availableVoices;
	}
}
