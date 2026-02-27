using DiscordTTSBot.TTS;

namespace DiscordTTSBot.LLM
{
	public class AiPersona
	{
		public required string[] Keywords { get; init; }
		public required string SystemPrompt { get; init; }
		public required string Voice { get; init; }
		public double SentencePauseSeconds { get; init; } = 0.5;
		public string? Model { get; init; }
		public ITTSProvider? TTSProvider { get; init; }
		public double Speed { get; init; } = 1.0;
		public List<ChatMessage> History { get; } = new();

		/// <summary>
		/// Optional async function that enriches the user message with external context
		/// (e.g. game data) before it is sent to the LLM.
		/// </summary>
		public Func<string, Task<string>>? ContextProvider { get; init; }

		public record ChatMessage(string Role, string Content);
	}
}
