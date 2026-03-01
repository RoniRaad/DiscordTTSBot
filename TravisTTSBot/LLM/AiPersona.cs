using DiscordTTSBot.TTS;

namespace DiscordTTSBot.LLM
{
	public enum LlmBackend { Ollama, OpenAI, Gemini }

	public class AiPersona
	{
		public required string[] Keywords { get; init; }
		public required string SystemPrompt { get; init; }
		public required string Voice { get; init; }
		public double SentencePauseSeconds { get; init; } = 0.5;
		public string? Model { get; init; }
		public LlmBackend Backend { get; init; } = LlmBackend.Ollama;
		public ITTSProvider? TTSProvider { get; init; }
		public double Speed { get; init; } = 1.0;
		public List<ChatMessage> History { get; } = new();

		/// <summary>
		/// Optional async function that enriches the user message with external context
		/// (e.g. game data) before it is sent to the LLM.
		/// </summary>
		public Func<string, Task<string>>? ContextProvider { get; init; }

		/// <summary>
		/// Optional list of tools the LLM can call (OpenAI function calling format).
		/// </summary>
		public List<AiTool>? Tools { get; init; }

		/// <summary>
		/// Handler that executes tool calls. Takes (toolName, argumentsJson) and returns the result string.
		/// Required if Tools is set.
		/// </summary>
		public Func<string, string, Task<string>>? ToolHandler { get; init; }

		public record ChatMessage(string Role, string Content, string? ToolCalls = null, string? ToolCallId = null);
	}

	public record AiTool(string Name, string Description, object Parameters);
}
