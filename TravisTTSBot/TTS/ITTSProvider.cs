namespace DiscordTTSBot.TTS
{
	public interface ITTSProvider
	{
		string Name { get; }

		Task<Stream> SynthesizeAsync(string text, string voice);

		bool IsValidVoice(string voice);

		IReadOnlyList<string> GetAvailableVoices();

		string DefaultVoice { get; }
	}
}
