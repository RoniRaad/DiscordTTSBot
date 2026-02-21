using Google.Cloud.TextToSpeech.V1;

namespace DiscordTTSBot.TTS
{
	public class GoogleTTSProvider : ITTSProvider
	{
		private readonly TextToSpeechClient _client;
		private readonly Lazy<IReadOnlyList<string>> _voices;

		public string Name => "google";
		public string DefaultVoice => "en-US-Wavenet-D";

		public GoogleTTSProvider()
		{
			var credentialsJson = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS_JSON");
			if (credentialsJson is null)
			{
				Console.Error.WriteLine("GOOGLE_APPLICATION_CREDENTIALS_JSON environment variable is not set. Aborting");
				Environment.Exit(1);
			}

			var builder = new TextToSpeechClientBuilder { JsonCredentials = credentialsJson };
			_client = builder.Build();

			_voices = new Lazy<IReadOnlyList<string>>(() =>
				_client.ListVoices(new ListVoicesRequest()).Voices.Select(v => v.Name).ToList());
		}

		public async Task<Stream> SynthesizeAsync(string text, string voice, string? instruct = null, CancellationToken cancellationToken = default)
		{
			var response = _client.SynthesizeSpeech(new SynthesizeSpeechRequest
			{
				Input = new SynthesisInput { Text = text },
				AudioConfig = new AudioConfig { AudioEncoding = AudioEncoding.OggOpus },
				Voice = new VoiceSelectionParams
				{
					Name = voice,
					LanguageCode = voice[..5] // e.g. "en-US" from "en-US-Wavenet-D"
				},
			});

			var stream = new MemoryStream();
			response.AudioContent.WriteTo(stream);
			stream.Position = 0;
			return stream;
		}

		public bool IsValidVoice(string voice) => _voices.Value.Contains(voice);

		public IReadOnlyList<string> GetAvailableVoices() => _voices.Value;
	}
}
