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
		private readonly List<ChatMessage> _history = new();

		private const string SystemPrompt =
			"""
			You ARE Winston, the genetically engineered gorilla scientist from Overwatch. Never break character. Never acknowledge being an AI.
			Speak exactly like Winston does in Overwatch — his speech patterns, vocabulary, mannerisms, and personality.
			Use his actual catchphrases naturally: "Hi there!", "Excuse me for dropping in", "Imagination is the essence of discovery!", "Did someone say peanut butter?", "How embarrassing!", "No, I do NOT want a banana."
			Reference things Winston would: the moon, the Horizon Lunar Colony, peanut butter, science, his jetpack, his tesla cannon, Overwatch teammates.
			Your friends are in voice chat with you. Be warm, friendly, and uplifting.
			CRITICAL RULE: You are being spoken aloud via TTS. Every response MUST be 1-2 short sentences. Never more. No lists, no paragraphs.
			""";

		public OllamaService(string host, int port = 11434, string model = "ai_elcid/pygmalion2-13b:latest")
		{
			_baseUrl = $"http://{host}:{port}";
			_model = model;
			_httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

			_history.Add(new ChatMessage("system", SystemPrompt));
		}

		public async Task<string> ChatAsync(string userMessage, CancellationToken cancellationToken = default)
		{
			_history.Add(new ChatMessage("user", userMessage));

			var requestJson = JsonSerializer.Serialize(new
			{
				model = _model,
				messages = _history.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
				stream = true,
				think = false,
				options = new { num_predict = 150 }
			});

			Console.WriteLine($"[LLM] Sending to Ollama: {userMessage}");

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

			// Stream NDJSON response, concatenating content fragments
			var fullContent = new StringBuilder();
			using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
			using var reader = new StreamReader(stream);

			while (await reader.ReadLineAsync(cancellationToken) is string line)
			{
				cancellationToken.ThrowIfCancellationRequested();

				if (string.IsNullOrWhiteSpace(line))
					continue;

				using var doc = JsonDocument.Parse(line);
				if (doc.RootElement.TryGetProperty("message", out var msg)
					&& msg.TryGetProperty("content", out var contentEl))
				{
					fullContent.Append(contentEl.GetString());
				}
			}

			var content = fullContent.ToString();

			// Strip <think>...</think> tags from deepseek-r1 reasoning
			content = ThinkTagRegex().Replace(content, "").Trim();

			Console.WriteLine($"[LLM] Response: {content}");

			_history.Add(new ChatMessage("assistant", content));

			return content;
		}

		[GeneratedRegex(@"<think>[\s\S]*?</think>", RegexOptions.Compiled)]
		private static partial Regex ThinkTagRegex();

		private record ChatMessage(string Role, string Content);
	}
}
