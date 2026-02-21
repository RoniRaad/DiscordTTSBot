using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DiscordTTSBot.LLM
{
	public partial class OllamaService
	{
		private readonly HttpClient _httpClient;
		private readonly string _baseUrl;
		private readonly string _model;

		public OllamaService(string host, int port = 11434, string model = "llama2-uncensored:latest")
		{
			_baseUrl = $"http://{host}:{port}";
			_model = model;
			_httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
		}

		/// <summary>
		/// Sends a message to Ollama using the given persona's system prompt and
		/// conversation history, returning the response text and a TTS voice instruct.
		/// </summary>
		public async Task<(string Text, string? Instruct)> ChatAsync(AiPersona persona, string userMessage, CancellationToken cancellationToken = default)
		{
			// Initialize history with system prompt on first use
			if (persona.History.Count == 0)
			{
				persona.History.Add(new AiPersona.ChatMessage("system", persona.SystemPrompt));
			}

			persona.History.Add(new AiPersona.ChatMessage("user", userMessage));

			var requestJson = JsonSerializer.Serialize(new
			{
				model = _model,
				messages = persona.History.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
				stream = false,
				think = false,
				options = new { num_predict = 300 }
			});

			Console.WriteLine($"[LLM/{persona.Keywords[0]}] Sending to Ollama: {userMessage}");

			using var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/chat")
			{
				Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
			};

			using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

			if (!response.IsSuccessStatusCode)
			{
				var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
				throw new HttpRequestException($"{(int)response.StatusCode}: {errorBody}");
			}

			var body = await response.Content.ReadAsStringAsync(cancellationToken);
			using var doc = JsonDocument.Parse(body);

			var rawContent = doc.RootElement
				.GetProperty("message")
				.GetProperty("content")
				.GetString() ?? "";

			var content = ThinkTagRegex().Replace(rawContent, "").Trim();

			// Extract [VOICE: ...] instruct tag before cleaning
			string? instruct = null;

			content = CleanText(content);

			Console.WriteLine($"[LLM/{persona.Keywords[0]}] Response: {content}");
			if (instruct is not null)
				Console.WriteLine($"[LLM/{persona.Keywords[0]}] Voice instruct: {instruct}");

			persona.History.Add(new AiPersona.ChatMessage("assistant", content));
			return (content, instruct);
		}

		[GeneratedRegex(@"\[VOICE:\s*([^\]]+)\]", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
		private static partial Regex VoiceTagRegex();

		private static string CleanText(string text)
		{
			var hashIdx = text.IndexOf("###");
			if (hashIdx >= 0)
				text = text[..hashIdx];

			text = text
				.Replace("\n", " ").Replace("\r", " ")   // actual newlines
				.Replace("\\n", " ").Replace("\\r", " ") // escaped newlines from LLM
				.Replace("\"", "").Replace("*", "")
				.Replace("{", "").Replace("}", "");       // code artifacts

			return text.Trim();
		}

		[GeneratedRegex(@"<think>[\s\S]*?</think>", RegexOptions.Compiled)]
		private static partial Regex ThinkTagRegex();
	}
}
