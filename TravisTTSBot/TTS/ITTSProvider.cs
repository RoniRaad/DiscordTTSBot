namespace DiscordTTSBot.TTS
{
	public interface ITTSProvider
	{
		string Name { get; }

		Task<Stream> SynthesizeAsync(string text, string voice, string? instruct = null, double speed = 1.0, CancellationToken cancellationToken = default);

		bool IsValidVoice(string voice);

		IReadOnlyList<string> GetAvailableVoices();

		string DefaultVoice { get; }

		bool IsReady => true;
	}
}
