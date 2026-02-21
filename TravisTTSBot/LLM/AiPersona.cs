namespace DiscordTTSBot.LLM
{
	public class AiPersona
	{
		public required string[] Keywords { get; init; }
		public required string SystemPrompt { get; init; }
		public required string Voice { get; init; }
		public double SentencePauseSeconds { get; init; } = 0.5;
		public List<ChatMessage> History { get; } = new();

		public record ChatMessage(string Role, string Content);
	}
}
