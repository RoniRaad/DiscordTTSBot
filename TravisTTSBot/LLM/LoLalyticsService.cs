using System.Text.RegularExpressions;

namespace DiscordTTSBot.LLM
{
	public partial class LoLalyticsService
	{
		private readonly HttpClient _http;
		private readonly Dictionary<string, (string Data, DateTime CachedAt)> _cache = [];
		private readonly Lock _cacheLock = new();
		private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

		public LoLalyticsService()
		{
			_http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
			_http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
		}

		/// <summary>
		/// Get counter/matchup data for a champion from the counters page.
		/// Returns formatted text with best/worst matchups and overall stats.
		/// </summary>
		public async Task<string?> GetCountersAsync(string championName, string? lane = null)
		{
			var slug = ToSlug(championName);
			var laneQuery = lane is not null ? $"?lane={NormalizeLane(lane)}" : "";
			var html = await FetchCachedAsync($"https://lolalytics.com/lol/{slug}/counters/{laneQuery}");
			if (html is null) return null;

			var laneLabel = lane is not null ? $" ({lane})" : "";
			var lines = new List<string> { $"{championName}{laneLabel} Counter Data:" };

			// Extract overall win rate from "Win Rate: X.XX%"
			var wrMatch = OverallWinrateRegex().Match(html);
			if (wrMatch.Success)
				lines.Add($"Win Rate: {wrMatch.Groups[1].Value}%");

			// Extract tier (S+, S, A, B, etc.)
			var tierMatch = TierRegex().Match(html);
			if (tierMatch.Success)
				lines.Add($"Tier: {tierMatch.Groups[1].Value}");

			// Extract pick rate
			var prMatch = PickrateRegex().Match(html);
			if (prMatch.Success)
				lines.Add($"Pick Rate: {prMatch.Groups[1].Value}%");

			// Extract ban rate
			var brMatch = BanrateRegex().Match(html);
			if (brMatch.Success)
				lines.Add($"Ban Rate: {brMatch.Groups[1].Value}%");

			// Extract "countered most by" text with champion names
			var weakMatch = CounteredByRegex().Match(html);
			if (weakMatch.Success)
			{
				var names = ChampLinkRegex().Matches(weakMatch.Value)
					.Select(m => m.Groups[1].Value)
					.ToArray();
				if (names.Length > 0)
					lines.Add($"Countered by: {string.Join(", ", names)}");
			}

			// Extract "strongest counter to" text with champion names
			var strongMatch = StrongestCounterRegex().Match(html);
			if (strongMatch.Success)
			{
				var names = ChampLinkRegex().Matches(strongMatch.Value)
					.Select(m => m.Groups[1].Value)
					.ToArray();
				if (names.Length > 0)
					lines.Add($"Strong against: {string.Join(", ", names)}");
			}

			return lines.Count > 1 ? string.Join("\n", lines) : null;
		}

		/// <summary>
		/// Get build data for a champion from the build page.
		/// Returns formatted text with winrate, tier, items, runes, etc.
		/// </summary>
		public async Task<string?> GetBuildAsync(string championName, string? lane = null)
		{
			var slug = ToSlug(championName);
			var laneQuery = lane is not null ? $"?lane={NormalizeLane(lane)}" : "";
			var html = await FetchCachedAsync($"https://lolalytics.com/lol/{slug}/build/{laneQuery}");
			if (html is null) return null;

			var laneLabel = lane is not null ? $" ({lane})" : "";
			var lines = new List<string> { $"{championName}{laneLabel} Build Data:" };

			var wrMatch = OverallWinrateRegex().Match(html);
			if (wrMatch.Success)
				lines.Add($"Win Rate: {wrMatch.Groups[1].Value}%");

			var tierMatch = TierRegex().Match(html);
			if (tierMatch.Success)
				lines.Add($"Tier: {tierMatch.Groups[1].Value}");

			var prMatch = PickrateRegex().Match(html);
			if (prMatch.Success)
				lines.Add($"Pick Rate: {prMatch.Groups[1].Value}%");

			var brMatch = BanrateRegex().Match(html);
			if (brMatch.Success)
				lines.Add($"Ban Rate: {brMatch.Groups[1].Value}%");

			// Extract rank (e.g. "1 / 93")
			var rankMatch = RankRegex().Match(html);
			if (rankMatch.Success)
				lines.Add($"Rank: {rankMatch.Groups[1].Value} / {rankMatch.Groups[2].Value}");

			// Extract "countered most by" for top counters if present
			var weakMatch = CounteredByRegex().Match(html);
			if (weakMatch.Success)
			{
				var names = ChampLinkRegex().Matches(weakMatch.Value)
					.Select(m => m.Groups[1].Value)
					.ToArray();
				if (names.Length > 0)
					lines.Add($"Top Counters: {string.Join(", ", names)}");
			}

			var strongMatch = StrongestCounterRegex().Match(html);
			if (strongMatch.Success)
			{
				var names = ChampLinkRegex().Matches(strongMatch.Value)
					.Select(m => m.Groups[1].Value)
					.ToArray();
				if (names.Length > 0)
					lines.Add($"Best Matchups: {string.Join(", ", names)}");
			}

			return lines.Count > 1 ? string.Join("\n", lines) : null;
		}

		/// <summary>
		/// Get head-to-head matchup data for champion vs opponent.
		/// </summary>
		public async Task<string?> GetMatchupAsync(string championName, string opponentName, string? lane = null)
		{
			var champSlug = ToSlug(championName);
			var oppSlug = ToSlug(opponentName);
			var laneQuery = lane is not null ? $"?lane={NormalizeLane(lane)}" : "";
			var html = await FetchCachedAsync($"https://lolalytics.com/lol/{champSlug}/vs/{oppSlug}/build/{laneQuery}");
			if (html is null) return null;

			var laneLabel = lane is not null ? $" ({lane})" : "";
			var lines = new List<string> { $"{championName} vs {opponentName}{laneLabel}:" };

			var wrMatch = OverallWinrateRegex().Match(html);
			if (wrMatch.Success)
				lines.Add($"Win Rate: {wrMatch.Groups[1].Value}%");

			// Look for game count
			var gamesMatch = GamesCountRegex().Match(html);
			if (gamesMatch.Success)
				lines.Add($"Games Analysed: {gamesMatch.Groups[1].Value}");

			var tierMatch = TierRegex().Match(html);
			if (tierMatch.Success)
				lines.Add($"Tier: {tierMatch.Groups[1].Value}");

			return lines.Count > 1 ? string.Join("\n", lines) : null;
		}

		private async Task<string?> FetchCachedAsync(string url)
		{
			lock (_cacheLock)
			{
				if (_cache.TryGetValue(url, out var cached) && DateTime.UtcNow - cached.CachedAt < CacheTtl)
					return cached.Data;
			}

			try
			{
				var html = await _http.GetStringAsync(url);

				lock (_cacheLock)
				{
					_cache[url] = (html, DateTime.UtcNow);
				}

				return html;
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine($"[LoLalytics] Failed to fetch {url}: {ex.Message}");
				return null;
			}
		}

		/// <summary>
		/// Normalizes lane aliases to the values LoLalytics expects.
		/// e.g. "mid" → "middle", "bot"/"adc" → "bottom", "jg"/"jung" → "jungle", "sup" → "support"
		/// </summary>
		public static string NormalizeLane(string lane)
		{
			return lane.ToLowerInvariant() switch
			{
				"mid" => "middle",
				"bot" or "adc" => "bottom",
				"jg" or "jung" => "jungle",
				"sup" or "supp" => "support",
				var l => l
			};
		}

		/// <summary>
		/// Converts a champion name to a LoLalytics URL slug.
		/// e.g. "Miss Fortune" → "missfortune", "Cho'Gath" → "chogath"
		/// </summary>
		public static string ToSlug(string name)
		{
			return SlugCleanRegex().Replace(name, "").ToLowerInvariant();
		}

		// "Win Rate: <!--...-->XX.XX<!---->%"
		[GeneratedRegex(@"Win Rate:[\s\S]*?<!--t=[^>]+-->(\d+\.\d+)<!---->%", RegexOptions.Compiled)]
		private static partial Regex OverallWinrateRegex();

		// Tier value like "S+", "S", "A", "B" etc.
		[GeneratedRegex(@"<!--t=[^>]+-->(S\+|S-|[SABCD][+-]?|S|A|B|C|D)<!---->[\s\S]{0,200}?Tier", RegexOptions.Compiled)]
		private static partial Regex TierRegex();

		// Pick Rate: XX.XX%
		[GeneratedRegex(@"<!--t=[^>]+-->(\d+\.\d+)<!---->%[\s\S]{0,200}?Pick Rate", RegexOptions.Compiled)]
		private static partial Regex PickrateRegex();

		// Ban Rate: XX.XX%
		[GeneratedRegex(@"<!--t=[^>]+-->(\d+\.\d+)<!---->%[\s\S]{0,200}?Ban Rate", RegexOptions.Compiled)]
		private static partial Regex BanrateRegex();

		// Rank: X / Y
		[GeneratedRegex(@"<!--t=[^>]+-->(\d+)<!---->[\s\S]*?/[\s\S]*?<!--t=[^>]+-->(\d+)<!---->[\s\S]{0,200}?Rank", RegexOptions.Compiled)]
		private static partial Regex RankRegex();

		// "countered most by ... champions of <a>Name</a>, <a>Name</a> & <a>Name</a>."
		[GeneratedRegex(@"countered most by[^.]+\.", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
		private static partial Regex CounteredByRegex();

		// "strongest counter to ... champions of <a>Name</a>, <a>Name</a> & <a>Name</a>."
		[GeneratedRegex(@"strongest counter to[^.]+\.", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
		private static partial Regex StrongestCounterRegex();

		// Extract champion name from <a href="...">ChampName</a>
		[GeneratedRegex(@"<a[^>]+>([^<]+)</a>", RegexOptions.Compiled)]
		private static partial Regex ChampLinkRegex();

		// "Analysed: XX,XXX"
		[GeneratedRegex(@"Champions Analysed:[\s\S]*?<!--t=[^>]+-->([\d,]+)<!---->", RegexOptions.Compiled)]
		private static partial Regex GamesCountRegex();

		// Strip non-alphanumeric for slug generation
		[GeneratedRegex(@"[^a-zA-Z0-9]", RegexOptions.Compiled)]
		private static partial Regex SlugCleanRegex();
	}
}
