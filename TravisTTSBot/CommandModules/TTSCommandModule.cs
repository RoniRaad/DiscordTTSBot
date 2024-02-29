using DiscordTTSBot.Static;
using DSharpPlus;
using DSharpPlus.CommandsNext;
using DSharpPlus.CommandsNext.Attributes;
using DSharpPlus.Entities;
using DSharpPlus.VoiceNext;
using Google.Cloud.TextToSpeech.V1;
using TTSBot.Static;

namespace TTSBot.Modules
{
	public class TTSCommands : BaseCommandModule
	{
		public static TextToSpeechClient TTSClient;

		static TTSCommands()
		{
			var ttsCredentialsJson = Environment.GetEnvironmentVariable("GOOGLE_APPLICATION_CREDENTIALS_JSON");
			if (ttsCredentialsJson is null)
			{
				Console.Error.WriteLine("GOOGLE_APPLICATION_CREDENTIALS_JSON environment variable is not set. Aborting");
				Environment.Exit(1);
			}

			var ttsClientBuilder = new TextToSpeechClientBuilder();
			ttsClientBuilder.JsonCredentials = ttsCredentialsJson;
			TTSClient =  ttsClientBuilder.Build();
		}

		[Command("t"), Description("Plays TTS in your channel.")]
		public async Task PlayTTS(CommandContext ctx, [RemainingText, Description("The words to TTS")] string words)
		{
			if (ctx.Member is null)
				return;

			await PlayTTSAsync(ctx.Member, ctx.Client, words);
		}

		[Command("voices"), Description("Plays a youtube or soundcloud link in your channel.")]
		public async Task VoicesCommand(CommandContext ctx)
		{
			await ctx.RespondAsync("Voice options can be found here. https://cloud.google.com/text-to-speech/docs/voices");
		}

		[Command("setvoice"), Description("Sets the users prefered tts voice.")]
		public async Task SetVoice(CommandContext ctx, [RemainingText, Description("The voice to set")] string voice)
		{
			var ttsclient = TextToSpeechClient.Create();
			var isValidVoice = ttsclient.ListVoices(new ListVoicesRequest()).Voices.Where(v => v.Name == voice).Any();
			if (!isValidVoice)
			{
				await ctx.RespondAsync("Error: Given voice is not valid!");
				return;
			}

			await UserSettingsHelper.SetUserVoice(ctx.User.Id, voice);
			await ctx.RespondAsync("Voice set successfully!");
		}

		public static async Task PlayTTSAsync(DiscordMember author, DiscordClient client, string words)
		{
			Console.WriteLine($"Recieved tts command from user. Input: {words}");
			var channel = author?.VoiceState?.Channel;

			if (channel is null)
				return;
		
			var speech = TTSClient.SynthesizeSpeech(new()
			{
				Input = new()
				{
					Text = words,
				},
				AudioConfig = new()
				{
					AudioEncoding = AudioEncoding.OggOpus,
				},
				Voice = new()
				{
					Name = UserSettingsHelper.GetUserVoice(author.Id),
					LanguageCode = "en-Us"
				},

			});

			using (var speechStream = new MemoryStream())
			{
				speech.AudioContent.WriteTo(speechStream);
				speechStream.Position = 0;
				var stream = await StreamHelpers.ConvertToDiscordAudioFormat(speechStream);
				stream.Position = 0;

				var audioClient = client.GetVoiceNext();
				if (audioClient is null)
				{
					return;
				}

				var connection = audioClient.GetConnection(author.Guild);

				if (connection?.TargetChannel?.Id != channel.Id)
					connection = await channel.ConnectAsync();

				var sink = connection.GetTransmitSink();

				try
				{
					await stream.CopyToAsync(sink);
				}
				finally
				{
					await stream.FlushAsync();
					await sink.FlushAsync();
					await connection.WaitForPlaybackFinishAsync();
				}
			}
		}
	}
}