using System.Text.Json;

namespace DiscordTTSBot.STT
{
	public class TranscriptionService
	{
		private readonly HttpClient _httpClient;
		private readonly string _baseUrl;
		private readonly string _debugDir;
		private int _fileCounter;

		public TranscriptionService(string host, int port = 8001)
		{
			_baseUrl = $"http://{host}:{port}";
			_httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
			_debugDir = Path.GetFullPath("stt_debug");
			if (!Directory.Exists(_debugDir))
				Directory.CreateDirectory(_debugDir);
		}

		public async Task<string?> TranscribeAsync(byte[] pcmData)
		{
			var wavData = ConvertPcmToWav(pcmData, sampleRate: 48000, channels: 2, bitsPerSample: 16);

			using var content = new MultipartFormDataContent
            {
                { new ByteArrayContent(wavData), "file", "audio.wav" },
                { new StringContent("en_US"), "source_language" }
            };

			var response = await _httpClient.PostAsync($"{_baseUrl}/transcribe", content);
			var json = await response.Content.ReadAsStringAsync();

			if (!response.IsSuccessStatusCode)
				throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}: {json}");
			using var doc = JsonDocument.Parse(json);

			if (doc.RootElement.TryGetProperty("text", out var textElement))
				return textElement.GetString();

			return json;
		}

		private static byte[] ConvertPcmToWav(byte[] pcmData, int sampleRate, int channels, int bitsPerSample)
		{
			var byteRate = sampleRate * channels * bitsPerSample / 8;
			var blockAlign = channels * bitsPerSample / 8;

			using var ms = new MemoryStream();
			using var writer = new BinaryWriter(ms);

			// RIFF header
			writer.Write("RIFF"u8);
			writer.Write(36 + pcmData.Length);
			writer.Write("WAVE"u8);

			// fmt chunk
			writer.Write("fmt "u8);
			writer.Write(16); // chunk size
			writer.Write((short)1); // PCM format
			writer.Write((short)channels);
			writer.Write(sampleRate);
			writer.Write(byteRate);
			writer.Write((short)blockAlign);
			writer.Write((short)bitsPerSample);

			// data chunk
			writer.Write("data"u8);
			writer.Write(pcmData.Length);
			writer.Write(pcmData);

			return ms.ToArray();
		}
	}
}
