using System.Text.Json;

namespace DiscordTTSBot.LLM
{
	/// <summary>
	/// Provides tool definitions and a handler for League of Legends data lookups.
	/// </summary>
	public static class LeagueToolHandler
	{
		public static List<AiTool> CreateTools() =>
		[
			new AiTool(
				"get_champion_info",
				"Get abilities, passive, cooldowns, costs, and role info for a League of Legends champion by name",
				new
				{
					type = "object",
					properties = new
					{
						name = new { type = "string", description = "The champion name (e.g. Yasuo, Ahri, Miss Fortune)" }
					},
					required = new[] { "name" }
				}
			),
			new AiTool(
				"get_item_info",
				"Get stats, description, price, and build path for a League of Legends item by name",
				new
				{
					type = "object",
					properties = new
					{
						name = new { type = "string", description = "The item name (e.g. Infinity Edge, Zhonya's Hourglass)" }
					},
					required = new[] { "name" }
				}
			),
			new AiTool(
				"get_champion_counters",
				"Get winrate, tier, pick/ban rates, and which champions counter or are weak against a given champion. Use this for questions about counters, matchups, or how strong a champion is.",
				new
				{
					type = "object",
					properties = new
					{
						name = new { type = "string", description = "The champion name (e.g. Yasuo, Ahri)" },
						lane = new { type = "string", description = "The lane/role (top, jungle, middle, bottom, support). Omit to use the champion's most popular role." }
					},
					required = new[] { "name" }
				}
			),
			new AiTool(
				"get_champion_build",
				"Get the recommended build for a champion including winrate, tier, core items, and runes. Use this when asked what to build on a champion.",
				new
				{
					type = "object",
					properties = new
					{
						name = new { type = "string", description = "The champion name (e.g. Yasuo, Ahri)" },
						lane = new { type = "string", description = "The lane/role (top, jungle, middle, bottom, support). Omit to use the champion's most popular role." }
					},
					required = new[] { "name" }
				}
			),
			new AiTool(
				"get_champion_matchup",
				"Get the head-to-head winrate and stats for a specific champion vs champion matchup.",
				new
				{
					type = "object",
					properties = new
					{
						champion = new { type = "string", description = "The first champion name" },
						opponent = new { type = "string", description = "The second champion name" },
						lane = new { type = "string", description = "The lane/role (top, jungle, middle, bottom, support). Omit to use the champion's most popular role." }
					},
					required = new[] { "champion", "opponent" }
				}
			)
		];

		public static Func<string, string, Task<string>> CreateHandler(
			LeagueDataService dataService,
			LeagueContextExtractor contextExtractor,
			LoLalyticsService lolalytics)
		{
			return async (toolName, argsJson) =>
			{
				using var doc = JsonDocument.Parse(argsJson);

				return toolName switch
				{
					"get_champion_info" => await HandleChampionLookup(
						doc.RootElement.GetProperty("name").GetString() ?? "", dataService, contextExtractor),
					"get_item_info" => HandleItemLookup(
						doc.RootElement.GetProperty("name").GetString() ?? "", dataService, contextExtractor),
					"get_champion_counters" => await HandleCounters(
						doc.RootElement.GetProperty("name").GetString() ?? "",
						GetOptionalString(doc.RootElement, "lane"),
						dataService, contextExtractor, lolalytics),
					"get_champion_build" => await HandleBuild(
						doc.RootElement.GetProperty("name").GetString() ?? "",
						GetOptionalString(doc.RootElement, "lane"),
						dataService, contextExtractor, lolalytics),
					"get_champion_matchup" => await HandleMatchup(
						doc.RootElement.GetProperty("champion").GetString() ?? "",
						doc.RootElement.GetProperty("opponent").GetString() ?? "",
						GetOptionalString(doc.RootElement, "lane"),
						dataService, contextExtractor, lolalytics),
					_ => $"Unknown tool: {toolName}"
				};
			};
		}

		private static async Task<string> HandleChampionLookup(
			string name, LeagueDataService dataService, LeagueContextExtractor extractor)
		{
			var resolved = extractor.ResolveChampionName(name);
			if (resolved is null)
				return $"Champion '{name}' not found. Check the spelling and try again.";

			var info = await dataService.GetChampionAsync(resolved);
			if (info is null)
				return $"Failed to fetch data for champion '{resolved}'.";

			return LeagueContextExtractor.FormatChampion(info);
		}

		private static string HandleItemLookup(
			string name, LeagueDataService dataService, LeagueContextExtractor extractor)
		{
			var resolved = extractor.ResolveItemName(name);
			if (resolved is null)
				return $"Item '{name}' not found. Check the spelling and try again.";

			var info = dataService.GetItem(resolved);
			if (info is null)
				return $"Failed to fetch data for item '{resolved}'.";

			return LeagueContextExtractor.FormatItem(info);
		}

		private static string? GetOptionalString(JsonElement root, string property)
		{
			return root.TryGetProperty(property, out var prop) && prop.ValueKind == JsonValueKind.String
				? prop.GetString()
				: null;
		}

		private static async Task<string?> GetChampionInfo(
			string resolved, LeagueDataService dataService)
		{
			var info = await dataService.GetChampionAsync(resolved);
			return info is not null ? LeagueContextExtractor.FormatChampion(info) : null;
		}

		private static async Task<string> HandleCounters(
			string name, string? lane, LeagueDataService dataService,
			LeagueContextExtractor extractor, LoLalyticsService lolalytics)
		{
			var resolved = extractor.ResolveChampionName(name);
			if (resolved is null)
				return $"Champion '{name}' not found. Check the spelling and try again.";

			var countersTask = lolalytics.GetCountersAsync(resolved, lane);
			var champTask = GetChampionInfo(resolved, dataService);
			await Task.WhenAll(countersTask, champTask);

			var parts = new List<string>();
			if (champTask.Result is not null) parts.Add(champTask.Result);
			if (countersTask.Result is not null) parts.Add(countersTask.Result);

			return parts.Count > 0 ? string.Join("\n\n", parts) : $"Failed to fetch data for '{resolved}'.";
		}

		private static async Task<string> HandleBuild(
			string name, string? lane, LeagueDataService dataService,
			LeagueContextExtractor extractor, LoLalyticsService lolalytics)
		{
			var resolved = extractor.ResolveChampionName(name);
			if (resolved is null)
				return $"Champion '{name}' not found. Check the spelling and try again.";

			var buildTask = lolalytics.GetBuildAsync(resolved, lane);
			var champTask = GetChampionInfo(resolved, dataService);
			await Task.WhenAll(buildTask, champTask);

			var parts = new List<string>();
			if (champTask.Result is not null) parts.Add(champTask.Result);
			if (buildTask.Result is not null) parts.Add(buildTask.Result);

			return parts.Count > 0 ? string.Join("\n\n", parts) : $"Failed to fetch data for '{resolved}'.";
		}

		private static async Task<string> HandleMatchup(
			string champion, string opponent, string? lane, LeagueDataService dataService,
			LeagueContextExtractor extractor, LoLalyticsService lolalytics)
		{
			var resolvedChamp = extractor.ResolveChampionName(champion);
			if (resolvedChamp is null)
				return $"Champion '{champion}' not found. Check the spelling and try again.";

			var resolvedOpp = extractor.ResolveChampionName(opponent);
			if (resolvedOpp is null)
				return $"Champion '{opponent}' not found. Check the spelling and try again.";

			var matchupTask = lolalytics.GetMatchupAsync(resolvedChamp, resolvedOpp, lane);
			var champ1Task = GetChampionInfo(resolvedChamp, dataService);
			var champ2Task = GetChampionInfo(resolvedOpp, dataService);
			await Task.WhenAll(matchupTask, champ1Task, champ2Task);

			var parts = new List<string>();
			if (champ1Task.Result is not null) parts.Add(champ1Task.Result);
			if (champ2Task.Result is not null) parts.Add(champ2Task.Result);
			if (matchupTask.Result is not null) parts.Add(matchupTask.Result);

			return parts.Count > 0 ? string.Join("\n\n", parts) : $"Failed to fetch matchup data for '{resolvedChamp}' vs '{resolvedOpp}'.";
		}
	}
}
