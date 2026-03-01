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

		// OpenAI configuration
		private readonly HttpClient _openAiClient;
		private readonly string _openAiModel;

		// Gemini configuration
		private readonly HttpClient _geminiClient;
		private readonly string _geminiModel;
		private readonly string? _geminiApiKey;

		public OllamaService(string host, int port = 11434, string model = "llama2-uncensored:latest",
			string openAiModel = "gpt-4o-mini", string geminiModel = "gemini-2.5-flash")
		{
			_baseUrl = $"http://{host}:{port}";
			_model = model;
			_httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

			_openAiModel = openAiModel;
			_openAiClient = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
			_openAiClient.BaseAddress = new Uri("https://api.openai.com/");

			var openAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
			if (!string.IsNullOrEmpty(openAiKey))
				_openAiClient.DefaultRequestHeaders.Authorization =
					new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", openAiKey);

			_geminiModel = geminiModel;
			_geminiApiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
			_geminiClient = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
			_geminiClient.BaseAddress = new Uri("https://generativelanguage.googleapis.com/");
		}

		/// <summary>
		/// Sends a message to the configured LLM backend using the given persona's system prompt and
		/// conversation history, returning the response text and a TTS voice instruct.
		/// Supports tool calling if the persona has tools defined.
		/// </summary>
		public async Task<(string Text, string? Instruct)> ChatAsync(AiPersona persona, string userMessage, CancellationToken cancellationToken = default)
		{
			// Gemini has a completely different API shape — delegate to its own flow
			if (persona.Backend == LlmBackend.Gemini)
				return await ChatGeminiAsync(persona, userMessage, cancellationToken);

			if (persona.History.Count == 0)
			{
				persona.History.Add(new AiPersona.ChatMessage("system", persona.SystemPrompt));
			}

			persona.History.Add(new AiPersona.ChatMessage("user", userMessage));

			var model = persona.Model ?? (persona.Backend == LlmBackend.OpenAI ? _openAiModel : _model);
			var hasTools = persona.Tools is { Count: > 0 } && persona.ToolHandler is not null;
			var backendName = persona.Backend == LlmBackend.OpenAI ? "OpenAI" : "Ollama";

			Console.WriteLine($"[LLM/{persona.Keywords[0]}] Sending to {backendName} ({model}): {userMessage}");

			// First call — may include tools
			var responseMessage = await CallChatAsync(persona, model, includeTools: hasTools, cancellationToken);

			// Check if the model wants to call tools
			if (hasTools && responseMessage.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.GetArrayLength() > 0)
			{
				// Add assistant message with tool calls to history
				persona.History.Add(new AiPersona.ChatMessage("assistant", "", SerializeToolCalls(toolCalls)));

				// Execute each tool call
				foreach (var toolCall in toolCalls.EnumerateArray())
				{
					var function = toolCall.GetProperty("function");
					var toolName = function.GetProperty("name").GetString() ?? "";

					// OpenAI returns arguments as a JSON string, Ollama as an object
					var argsElement = function.GetProperty("arguments");
					var argsJson = argsElement.ValueKind == JsonValueKind.String
						? argsElement.GetString() ?? "{}"
						: argsElement.GetRawText();

					// Extract tool_call id for OpenAI (required in tool response)
					var toolCallId = toolCall.TryGetProperty("id", out var idProp)
						? idProp.GetString()
						: null;

					Console.WriteLine($"[LLM/{persona.Keywords[0]}] Tool call: {toolName}({argsJson})");

					var result = await persona.ToolHandler!(toolName, argsJson);
					Console.WriteLine($"[LLM/{persona.Keywords[0]}] Tool result: {(result.Length > 200 ? result[..200] + "..." : result)}");

					persona.History.Add(new AiPersona.ChatMessage("tool", result, ToolCallId: toolCallId));
				}

				// Second call — no tools, get the final text response
				responseMessage = await CallChatAsync(persona, model, includeTools: false, cancellationToken);
			}

			var content = ExtractContent(responseMessage);

			// Retry once if the model returned an empty response (e.g. only <think> tags)
			if (string.IsNullOrWhiteSpace(content))
			{
				Console.WriteLine($"[LLM/{persona.Keywords[0]}] Empty response, retrying...");
				responseMessage = await CallChatAsync(persona, model, includeTools: false, cancellationToken);
				content = ExtractContent(responseMessage);
			}

			Console.WriteLine($"[LLM/{persona.Keywords[0]}] Response: {content}");

			if (!string.IsNullOrWhiteSpace(content))
				persona.History.Add(new AiPersona.ChatMessage("assistant", content));

			return (content, null);
		}

		private Task<JsonElement> CallChatAsync(AiPersona persona, string model, bool includeTools, CancellationToken cancellationToken)
		{
			return persona.Backend switch
			{
				LlmBackend.OpenAI => CallOpenAiChatAsync(persona, model, includeTools, cancellationToken),
				_ => CallOllamaChatAsync(persona, model, includeTools, cancellationToken)
			};
		}

		private string ExtractContent(JsonElement message)
		{
			var raw = message.TryGetProperty("content", out var prop) && prop.ValueKind == JsonValueKind.String
				? prop.GetString() ?? ""
				: "";
			var cleaned = ThinkTagRegex().Replace(raw, "").Trim();
			return CleanText(cleaned);
		}

		#region Ollama Backend

		private async Task<JsonElement> CallOllamaChatAsync(AiPersona persona, string model, bool includeTools, CancellationToken cancellationToken)
		{
			var messages = BuildMessagesArray(persona, LlmBackend.Ollama);

			var requestObj = new Dictionary<string, object>
			{
				["model"] = model,
				["messages"] = messages,
				["stream"] = false,
				["options"] = new { num_predict = 2048 }
			};

			if (includeTools && persona.Tools is { Count: > 0 })
			{
				requestObj["tools"] = BuildToolsArray(persona);
			}

			var requestJson = JsonSerializer.Serialize(requestObj);

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

			// Ollama returns message directly (not nested in choices[])
			return doc.RootElement
				.GetProperty("message")
				.Clone();
		}

		#endregion

		#region OpenAI Backend

		private async Task<JsonElement> CallOpenAiChatAsync(AiPersona persona, string model, bool includeTools, CancellationToken cancellationToken)
		{
			var messages = BuildMessagesArray(persona, LlmBackend.OpenAI);

			var requestObj = new Dictionary<string, object>
			{
				["model"] = model,
				["messages"] = messages,
				["max_tokens"] = 2048
			};

			if (includeTools && persona.Tools is { Count: > 0 })
			{
				requestObj["tools"] = BuildToolsArray(persona);
			}

			var requestJson = JsonSerializer.Serialize(requestObj);

			using var request = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
			{
				Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
			};

			using var response = await _openAiClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

			if (!response.IsSuccessStatusCode)
			{
				var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
				throw new HttpRequestException($"OpenAI {(int)response.StatusCode}: {errorBody}");
			}

			var body = await response.Content.ReadAsStringAsync(cancellationToken);
			using var doc = JsonDocument.Parse(body);

			// OpenAI returns choices[0].message
			return doc.RootElement
				.GetProperty("choices")[0]
				.GetProperty("message")
				.Clone();
		}

		#endregion

		#region Gemini Backend

		/// <summary>
		/// Gemini has a completely different API shape: parts-based messages, functionCall/functionResponse,
		/// and system instructions are separate from contents. This method manages its own conversation flow.
		/// </summary>
		private async Task<(string Text, string? Instruct)> ChatGeminiAsync(AiPersona persona, string userMessage, CancellationToken cancellationToken)
		{
			var model = persona.Model ?? _geminiModel;
			var hasTools = persona.Tools is { Count: > 0 } && persona.ToolHandler is not null;

			// Add user message to shared history (for context preservation across calls)
			if (persona.History.Count == 0)
				persona.History.Add(new AiPersona.ChatMessage("system", persona.SystemPrompt));
			persona.History.Add(new AiPersona.ChatMessage("user", userMessage));

			Console.WriteLine($"[LLM/{persona.Keywords[0]}] Sending to Gemini ({model}): {userMessage}");

			// Build Gemini request
			var responseElement = await CallGeminiAsync(persona, model, includeTools: hasTools, cancellationToken);

			// Check for function calls in the response parts
			if (hasTools && TryExtractGeminiFunctionCalls(responseElement, out var functionCalls))
			{
				// Store the model's function call in history as an assistant message
				persona.History.Add(new AiPersona.ChatMessage("assistant", "", JsonSerializer.Serialize(functionCalls)));

				// Execute each function call and collect results
				var functionResponses = new List<object>();
				foreach (var fc in functionCalls)
				{
					var toolName = fc.Name;
					var argsJson = JsonSerializer.Serialize(fc.Args);

					Console.WriteLine($"[LLM/{persona.Keywords[0]}] Tool call: {toolName}({argsJson})");

					var result = await persona.ToolHandler!(toolName, argsJson);
					Console.WriteLine($"[LLM/{persona.Keywords[0]}] Tool result: {(result.Length > 200 ? result[..200] + "..." : result)}");

					persona.History.Add(new AiPersona.ChatMessage("tool", result, ToolCallId: toolName));

					functionResponses.Add(new
					{
						functionResponse = new
						{
							name = toolName,
							response = new { result }
						}
					});
				}

				// Second call with function results
				responseElement = await CallGeminiAsync(persona, model, includeTools: false, cancellationToken);
			}

			var content = ExtractGeminiContent(responseElement);

			// Retry once if empty
			if (string.IsNullOrWhiteSpace(content))
			{
				Console.WriteLine($"[LLM/{persona.Keywords[0]}] Empty response, retrying...");
				responseElement = await CallGeminiAsync(persona, model, includeTools: false, cancellationToken);
				content = ExtractGeminiContent(responseElement);
			}

			content = CleanText(content);
			Console.WriteLine($"[LLM/{persona.Keywords[0]}] Response: {content}");

			if (!string.IsNullOrWhiteSpace(content))
				persona.History.Add(new AiPersona.ChatMessage("assistant", content));

			return (content, null);
		}

		private async Task<JsonElement> CallGeminiAsync(AiPersona persona, string model, bool includeTools, CancellationToken cancellationToken)
		{
			// Build Gemini contents array from history
			var contents = BuildGeminiContents(persona);

			var requestObj = new Dictionary<string, object>
			{
				["contents"] = contents,
				["systemInstruction"] = new
				{
					parts = new[] { new { text = persona.SystemPrompt } }
				}
			};

			if (includeTools && persona.Tools is { Count: > 0 })
			{
				requestObj["tools"] = new[]
				{
					new
					{
						functionDeclarations = persona.Tools.Select(t => new
						{
							name = t.Name,
							description = t.Description,
							parameters = t.Parameters
						}).ToArray()
					}
				};
			}

			var requestJson = JsonSerializer.Serialize(requestObj);

			var url = $"v1beta/models/{model}:generateContent?key={_geminiApiKey}";
			using var request = new HttpRequestMessage(HttpMethod.Post, url)
			{
				Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
			};

			using var response = await _geminiClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

			if (!response.IsSuccessStatusCode)
			{
				var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
				throw new HttpRequestException($"Gemini {(int)response.StatusCode}: {errorBody}");
			}

			var body = await response.Content.ReadAsStringAsync(cancellationToken);
			using var doc = JsonDocument.Parse(body);

			// Return the full response for parsing
			return doc.RootElement.Clone();
		}

		/// <summary>
		/// Builds Gemini-format contents array from persona history.
		/// Skips system messages (handled via systemInstruction).
		/// Maps: user→user, assistant→model, tool→user(functionResponse)
		/// </summary>
		private static List<object> BuildGeminiContents(AiPersona persona)
		{
			var contents = new List<object>();

			foreach (var msg in persona.History)
			{
				if (msg.Role == "system")
					continue; // Handled by systemInstruction

				if (msg.Role == "user")
				{
					contents.Add(new
					{
						role = "user",
						parts = new object[] { new { text = msg.Content } }
					});
				}
				else if (msg.Role == "assistant" && msg.ToolCalls is not null)
				{
					// Model response with function calls
					var calls = JsonSerializer.Deserialize<List<GeminiFunctionCall>>(msg.ToolCalls);
					if (calls is not null)
					{
						contents.Add(new
						{
							role = "model",
							parts = calls.Select(fc => (object)new
							{
								functionCall = new { name = fc.Name, args = fc.Args }
							}).ToArray()
						});
					}
				}
				else if (msg.Role == "assistant")
				{
					contents.Add(new
					{
						role = "model",
						parts = new object[] { new { text = msg.Content } }
					});
				}
				else if (msg.Role == "tool")
				{
					contents.Add(new
					{
						role = "user",
						parts = new object[]
						{
							new
							{
								functionResponse = new
								{
									name = msg.ToolCallId ?? "unknown",
									response = new { result = msg.Content }
								}
							}
						}
					});
				}
			}

			return contents;
		}

		private static bool TryExtractGeminiFunctionCalls(JsonElement response, out List<GeminiFunctionCall> calls)
		{
			calls = [];

			if (!response.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
				return false;

			var parts = candidates[0].GetProperty("content").GetProperty("parts");
			foreach (var part in parts.EnumerateArray())
			{
				if (part.TryGetProperty("functionCall", out var fc))
				{
					var name = fc.GetProperty("name").GetString() ?? "";
					var args = fc.TryGetProperty("args", out var argsEl)
						? JsonSerializer.Deserialize<Dictionary<string, object>>(argsEl.GetRawText()) ?? []
						: [];
					calls.Add(new GeminiFunctionCall(name, args));
				}
			}

			return calls.Count > 0;
		}

		private static string ExtractGeminiContent(JsonElement response)
		{
			if (!response.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
				return "";

			var parts = candidates[0].GetProperty("content").GetProperty("parts");
			var texts = new List<string>();
			foreach (var part in parts.EnumerateArray())
			{
				if (part.TryGetProperty("text", out var textProp))
					texts.Add(textProp.GetString() ?? "");
			}
			return string.Join(" ", texts).Trim();
		}

		private record GeminiFunctionCall(string Name, Dictionary<string, object> Args);

		#endregion

		#region Shared Helpers

		private static object[] BuildToolsArray(AiPersona persona)
		{
			return persona.Tools!.Select(t => new
			{
				type = "function",
				function = new
				{
					name = t.Name,
					description = t.Description,
					parameters = t.Parameters
				}
			}).ToArray();
		}

		private static object[] BuildMessagesArray(AiPersona persona, LlmBackend backend)
		{
			var messages = new List<object>();

			foreach (var msg in persona.History)
			{
				if (msg.ToolCalls is not null && msg.Role == "assistant")
				{
					// Assistant message with tool calls
					messages.Add(new
					{
						role = msg.Role,
						content = "",
						tool_calls = JsonSerializer.Deserialize<JsonElement>(msg.ToolCalls)
					});
				}
				else if (msg.Role == "tool" && backend == LlmBackend.OpenAI)
				{
					// OpenAI requires tool_call_id on tool responses
					messages.Add(new
					{
						role = "tool",
						content = msg.Content,
						tool_call_id = msg.ToolCallId ?? "call_0"
					});
				}
				else
				{
					messages.Add(new { role = msg.Role, content = msg.Content });
				}
			}

			return [.. messages];
		}

		private static string SerializeToolCalls(JsonElement toolCalls)
		{
			return toolCalls.GetRawText();
		}

		private static string CleanText(string text)
		{
			var hashIdx = text.IndexOf("###");
			if (hashIdx >= 0)
				text = text[..hashIdx];

			// Replace slash-separated numbers (e.g. "9/8/7/6/5") with commas for TTS
			text = SlashNumbersRegex().Replace(text, m =>
				m.Value.Replace("/", ", "));

			text = text
				.Replace("\n", " ").Replace("\r", " ")
				.Replace("\\n", " ").Replace("\\r", " ")
				.Replace("\"", "").Replace("*", "")
				.Replace("{", "").Replace("}", "");

			return text.Trim();
		}

		#endregion

		[GeneratedRegex(@"<think>[\s\S]*?</think>", RegexOptions.Compiled)]
		private static partial Regex ThinkTagRegex();

		[GeneratedRegex(@"\d+(?:\.\d+)?(?:/\d+(?:\.\d+)?){2,}", RegexOptions.Compiled)]
		private static partial Regex SlashNumbersRegex();
	}
}
