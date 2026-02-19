using DiscordTTSBot.Static;
using DiscordTTSBot.TTS;
using NetCord.Gateway;
using NetCord.Gateway.Voice;
using NetCord.Services.Commands;
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

			await PlayTTSAsync(Context.Client, guildId, channelId, Context.User.Id, words);
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

		public static Func<Task>? OnLastVoiceDisconnect { get; set; }

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

		public static async Task PlayTTSAsync(GatewayClient client, ulong guildId, ulong channelId, ulong userId, string words)
		{
			Console.WriteLine($"Received tts command from user. Input: {words}");

			var provider = Providers.GetProviderForUser(userId);
			var voice = Providers.GetVoiceOverride(userId) ?? UserSettingsHelper.GetUserVoice(userId);
			using var audioStream = await provider.SynthesizeAsync(words, voice);

			var pcmStream = await StreamHelpers.ConvertToDiscordAudioFormat(audioStream);
			pcmStream.Position = 0;

			await _voiceLock.WaitAsync();
			try
			{
				// Reuse existing voice client if already connected to the same channel,
				// otherwise dispose the old one and create a new connection
				if (_voiceClients.TryGetValue(guildId, out var existingClient)
					&& existingClient.ChannelId == channelId)
				{
					await existingClient.EnterSpeakingStateAsync(new SpeakingProperties(SpeakingFlags.Microphone));

					using var voiceStream = existingClient.CreateVoiceStream();
					using var opusStream = new OpusEncodeStream(voiceStream, PcmFormat.Short, VoiceChannels.Stereo, OpusApplication.Voip);

					await pcmStream.CopyToAsync(opusStream);
					await opusStream.FlushAsync();
				}
				else
				{
					if (existingClient is not null)
					{
						existingClient.Dispose();
						_voiceClients.Remove(guildId);
					}

					var voiceClient = await client.JoinVoiceChannelAsync(guildId, channelId);
					try
					{
                        await voiceClient.StartAsync();
                    }
					catch(Exception ex)
					{
						Console.WriteLine($"An error occured: {ex.Message}");
					}

                    await voiceClient.EnterSpeakingStateAsync(new SpeakingProperties(SpeakingFlags.Microphone));

					_voiceClients[guildId] = voiceClient;

					using var voiceStream = voiceClient.CreateVoiceStream();
					try
					{
						using var opusStream = new OpusEncodeStream(voiceStream, PcmFormat.Short, VoiceChannels.Stereo, OpusApplication.Voip);
                        await pcmStream.CopyToAsync(opusStream);
                        await opusStream.FlushAsync();
                    }
					catch (Exception ex) {
                        Console.WriteLine($"An error occured: {ex.Message}");
                    }
				}
			}
			finally
			{
				_voiceLock.Release();
			}
		}
	}
}
