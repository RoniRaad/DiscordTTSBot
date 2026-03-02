using System.Net;
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

			var wrMatch = WinrateRegex().Match(html);
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

			// Extract rank (e.g. "1 / 92")
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

			ExtractBuildData(html, lines);

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

			var wrMatch = WinrateRegex().Match(html);
			if (wrMatch.Success)
				lines.Add($"Win Rate: {wrMatch.Groups[1].Value}%");

			// Look for matchup-specific game count
			var gamesMatch = GamesCountRegex().Match(html);
			if (gamesMatch.Success)
				lines.Add($"Games: {gamesMatch.Groups[1].Value}");

			ExtractBuildData(html, lines);

			return lines.Count > 1 ? string.Join("\n", lines) : null;
		}

		/// <summary>
		/// Extracts recommended build data (items, runes, spells, skills) from a build/matchup HTML page.
		/// </summary>
		private static void ExtractBuildData(string html, List<string> lines)
		{
			// Starting Items: section marked by <!--t=XX-->Starting Items<!---->
			var startingItems = ExtractSectionItems(html, StartingItemsSectionRegex(), ItemAltRegex());
			if (startingItems.Length > 0)
				lines.Add($"Starting Items: {string.Join(", ", startingItems)}");

			// Core Build: section marked by <!--t=XX-->Core Build<!---->
			var coreItems = ExtractSectionItems(html, CoreBuildSectionRegex(), ItemAltRegex());
			if (coreItems.Length > 0)
				lines.Add($"Core Build: {string.Join(" → ", coreItems)}");

			// Summoner Spells: section marked by >Summoner Spells<
			var spells = ExtractSectionItems(html, SummonerSpellsSectionRegex(), SpellAltRegex());
			if (spells.Length > 0)
				lines.Add($"Summoner Spells: {string.Join(", ", spells)}");

			// Primary Runes: selected runes (no grayscale) from <!--t=XX-->Primary Runes<!---->
			var primaryRunes = ExtractSelectedRunes(html, PrimaryRunesSectionRegex());
			if (primaryRunes.Length > 0)
				lines.Add($"Primary Runes: {string.Join(", ", primaryRunes)}");

			// Secondary Runes: selected runes (no grayscale) from <!--t=XX-->Secondary<!---->
			var secondaryRunes = ExtractSelectedRunes(html, SecondaryRunesSectionRegex());
			if (secondaryRunes.Length > 0)
				lines.Add($"Secondary Runes: {string.Join(", ", secondaryRunes)}");

			// Skill Priority: Q > E > W from the Skill Priority section
			var skills = ExtractSkillPriority(html);
			if (skills.Length > 0)
				lines.Add($"Skill Priority: {string.Join(" > ", skills)}");
		}

		/// <summary>
		/// Extracts item/spell names from a build section by finding alt="Name" on img tags.
		/// </summary>
		private static string[] ExtractSectionItems(string html, Regex sectionRegex, Regex altRegex)
		{
			var sectionMatch = sectionRegex.Match(html);
			if (!sectionMatch.Success) return [];

			return altRegex.Matches(sectionMatch.Value)
				.Select(m => WebUtility.HtmlDecode(m.Groups[1].Value))
				.Where(n => !string.IsNullOrEmpty(n))
				.ToArray();
		}

		/// <summary>
		/// Extracts selected (non-greyed-out) rune names from a rune section.
		/// Selected runes have alt="Name" on img tags without "grayscale" in their class.
		/// </summary>
		private static string[] ExtractSelectedRunes(string html, Regex sectionRegex)
		{
			var sectionMatch = sectionRegex.Match(html);
			if (!sectionMatch.Success) return [];

			return SelectedRuneRegex().Matches(sectionMatch.Value)
				.Select(m => WebUtility.HtmlDecode(m.Groups[1].Value))
				.Where(n => n != "statmod")
				.ToArray();
		}

		/// <summary>
		/// Extracts skill priority letters (Q/W/E) from the Skill Priority section.
		/// Uses the larger 14px skill letter labels in the compact bar.
		/// </summary>
		private static string[] ExtractSkillPriority(string html)
		{
			var sectionMatch = SkillPrioritySectionRegex().Match(html);
			if (!sectionMatch.Success) return [];

			return SkillLetterRegex().Matches(sectionMatch.Value)
				.Select(m => m.Groups[1].Value)
				.ToArray();
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

		// Champion-specific Win Rate: value-before-label pattern
		// e.g. <!--t=4g-->53.47<!---->%</div>...<div class="...">Win Rate</div>
		[GeneratedRegex(@"font-bold""><!--t=[^>]+-->(\d+\.\d+)<!---->%</div>[\s\S]{0,200}?>Win Rate<", RegexOptions.Compiled)]
		private static partial Regex WinrateRegex();

		// Tier value: <!--t=XX-->S+<!----></div>...<div class="...">Tier</div>
		[GeneratedRegex(@"font-bold""><!--t=[^>]+-->(S\+|S-|[SABCD][+-]?|S|A|B|C|D)<!----></div>[\s\S]{0,200}?>Tier<", RegexOptions.Compiled)]
		private static partial Regex TierRegex();

		// Pick Rate: <!--t=XX-->14.72<!---->%</div>...<div class="...">Pick Rate</div>
		[GeneratedRegex(@"font-bold""><!--t=[^>]+-->(\d+\.\d+)<!---->%</div>[\s\S]{0,200}?>Pick Rate<", RegexOptions.Compiled)]
		private static partial Regex PickrateRegex();

		// Ban Rate: <!--t=XX-->7.66<!---->%</div>...<div class="...">Ban Rate</div>
		[GeneratedRegex(@"font-bold""><!--t=[^>]+-->(\d+\.\d+)<!---->%</div>[\s\S]{0,200}?>Ban Rate<", RegexOptions.Compiled)]
		private static partial Regex BanrateRegex();

		// Rank: <!--t=XX-->1<!----> / <!--t=XX-->92<!----></div>...<div class="...">Rank</div>
		[GeneratedRegex(@"font-bold""><!--t=[^>]+-->(\d+)<!----> / <!--t=[^>]+-->(\d+)<!----></div>[\s\S]{0,200}?>Rank<", RegexOptions.Compiled)]
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

		// Matchup-specific game count: <div class="...font-bold">10,876</div>...<div class="...">Games</div>
		[GeneratedRegex(@"font-bold"">([\d,]+)</div>[\s\S]{0,200}?>Games<", RegexOptions.Compiled)]
		private static partial Regex GamesCountRegex();

		// Strip non-alphanumeric for slug generation
		[GeneratedRegex(@"[^a-zA-Z0-9]", RegexOptions.Compiled)]
		private static partial Regex SlugCleanRegex();

		// ── Build section regexes ──────────────────────────────────────

		// Starting Items section: capture ~2000 chars after the marker
		[GeneratedRegex(@"<!--t=[^>]+-->Starting Items<!---->[\s\S]{0,2000}", RegexOptions.Compiled)]
		private static partial Regex StartingItemsSectionRegex();

		// Core Build section: capture ~5000 chars after the marker (has arrows between items)
		[GeneratedRegex(@"<!--t=[^>]+-->Core Build<!---->[\s\S]{0,5000}", RegexOptions.Compiled)]
		private static partial Regex CoreBuildSectionRegex();

		// Summoner Spells section: capture ~1500 chars after the marker
		[GeneratedRegex(@">Summoner Spells</div>[\s\S]{0,1500}", RegexOptions.Compiled)]
		private static partial Regex SummonerSpellsSectionRegex();

		// Primary Runes section: capture up to the Secondary marker to get all 4 rows
		[GeneratedRegex(@"<!--t=[^>]+-->Primary Runes<!---->[\s\S]*?(?=<!--t=[^>]+-->Secondary<!---->)", RegexOptions.Compiled)]
		private static partial Regex PrimaryRunesSectionRegex();

		// Secondary Runes section: capture ~8000 chars to reach both secondary rune rows
		[GeneratedRegex(@"<!--t=[^>]+-->Secondary<!---->[\s\S]{0,8000}", RegexOptions.Compiled)]
		private static partial Regex SecondaryRunesSectionRegex();

		// Skill Priority section: capture ~6000 chars to reach all 3 skill letters
		[GeneratedRegex(@">Skill Priority[\s\S]{0,6000}", RegexOptions.Compiled)]
		private static partial Regex SkillPrioritySectionRegex();

		// Extract item name from alt="ItemName" on item img tags (item64 URLs)
		[GeneratedRegex(@"item64/\d+\.webp""[^>]*alt=""([^""]+)""", RegexOptions.Compiled)]
		private static partial Regex ItemAltRegex();

		// Extract spell name from alt="SpellName" on spell img tags (spell64 URLs)
		[GeneratedRegex(@"spell64/\d+\.webp""[^>]*alt=""([^""]+)""", RegexOptions.Compiled)]
		private static partial Regex SpellAltRegex();

		// Extract selected rune name: alt="Name" on rune img NOT followed by grayscale class.
		// Selected runes have class="flex flex-none cursor-help" without "grayscale opacity-70".
		[GeneratedRegex(@"alt=""([^""]+)"" data-id="""" class=""[^""]*cursor-help""", RegexOptions.Compiled)]
		private static partial Regex SelectedRuneRegex();

		// Extract skill letter (Q/W/E) from the larger 14px labels in Skill Priority section
		[GeneratedRegex(@"w-\[16px\] text-\[14px\][^>]*>([QWER])</div>", RegexOptions.Compiled)]
		private static partial Regex SkillLetterRegex();
	}
}
