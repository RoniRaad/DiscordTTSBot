using System.Text.Json;
using System.Text.RegularExpressions;

namespace DiscordTTSBot.STT
{
	public class TranscriptionService
	{
		private readonly HttpClient _httpClient;
		private readonly string _baseUrl;
		private readonly string _debugDir;
		private int _fileCounter;

		/// <summary>
		/// Optional prompt to bias Whisper toward expected vocabulary.
		/// Passed as the "prompt" field in the OpenAI-compatible API.
		/// </summary>
		public string? Prompt { get; set; }

		private readonly string _model;

		public TranscriptionService(string host, int port = 8001, string model = "Systran/faster-whisper-medium")
		{
			_baseUrl = $"http://{host}:{port}";
			_model = model;
			_httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
			_debugDir = Path.GetFullPath("stt_debug");
			if (!Directory.Exists(_debugDir))
				Directory.CreateDirectory(_debugDir);
		}

		public async Task<string?> TranscribeAsync(byte[] pcmData)
		{
			// Whisper expects 16kHz mono — downsample from Discord's 48kHz stereo
			var monoData = StereoToMono(pcmData);
			var downsampledData = Downsample(monoData, fromRate: 48000, toRate: 16000);
			var wavData = ConvertPcmToWav(downsampledData, sampleRate: 16000, channels: 1, bitsPerSample: 16);

			using var content = new MultipartFormDataContent
			{
				{ new ByteArrayContent(wavData), "file", "audio.wav" },
				{ new StringContent(_model), "model" },
				{ new StringContent("en"), "language" }
			};

			if (!string.IsNullOrEmpty(Prompt))
				content.Add(new StringContent(Prompt), "prompt");

			var response = await _httpClient.PostAsync($"{_baseUrl}/v1/audio/transcriptions", content);
			var json = await response.Content.ReadAsStringAsync();

			if (!response.IsSuccessStatusCode)
				throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase}: {json}");
			using var doc = JsonDocument.Parse(json);

			if (doc.RootElement.TryGetProperty("text", out var textElement))
				return textElement.GetString();

			return json;
		}

		/// <summary>
		/// Converts stereo 16-bit PCM to mono by averaging left and right channels.
		/// </summary>
		private static byte[] StereoToMono(byte[] stereoData)
		{
			var sampleCount = stereoData.Length / 4; // 2 channels * 2 bytes per sample
			var mono = new byte[sampleCount * 2];

			for (int i = 0; i < sampleCount; i++)
			{
				var left = BitConverter.ToInt16(stereoData, i * 4);
				var right = BitConverter.ToInt16(stereoData, i * 4 + 2);
				var avg = (short)((left + right) / 2);
				BitConverter.TryWriteBytes(mono.AsSpan(i * 2), avg);
			}

			return mono;
		}

		/// <summary>
		/// Downsamples mono 16-bit PCM by simple integer decimation (e.g. 48kHz → 16kHz = keep every 3rd sample).
		/// </summary>
		private static byte[] Downsample(byte[] data, int fromRate, int toRate)
		{
			var ratio = fromRate / toRate;
			var srcSamples = data.Length / 2;
			var dstSamples = srcSamples / ratio;
			var result = new byte[dstSamples * 2];

			for (int i = 0; i < dstSamples; i++)
			{
				var srcOffset = i * ratio * 2;
				result[i * 2] = data[srcOffset];
				result[i * 2 + 1] = data[srcOffset + 1];
			}

			return result;
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
