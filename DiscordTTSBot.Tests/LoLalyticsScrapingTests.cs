using DiscordTTSBot.LLM;
using Xunit.Abstractions;

namespace DiscordTTSBot.Tests;

/// <summary>
/// Integration tests that hit LoLalytics live to verify our scraping logic
/// still works against their current HTML structure. If any of these fail,
/// the site layout has likely changed and the regex patterns need updating.
/// </summary>
public class LoLalyticsScrapingTests(ITestOutputHelper output)
{
	private readonly LoLalyticsService _service = new();

	// ──────────────────────────────────────────────────────────────
	//  Counters page — only has countered-by / strong-against lists
	// ──────────────────────────────────────────────────────────────

	[Theory]
	[InlineData("Ahri", "middle")]
	[InlineData("Jinx", "bottom")]
	[InlineData("Thresh", "support")]
	[InlineData("Lee Sin", "jungle")]
	[InlineData("Darius", "top")]
	public async Task GetCounters_ReturnsData_ForChampionAndLane(string champion, string lane)
	{
		var result = await _service.GetCountersAsync(champion, lane);

		output.WriteLine($"--- {champion} ({lane}) Counters ---");
		output.WriteLine(result ?? "(null)");
		output.WriteLine("");

		Assert.NotNull(result);
		Assert.Contains("Counter Data:", result);
		Assert.Contains("Countered by:", result);
		Assert.Contains("Strong against:", result);
	}

	[Fact]
	public async Task GetCounters_ReturnsData_WithoutLane()
	{
		var result = await _service.GetCountersAsync("Yasuo");

		output.WriteLine("--- Yasuo (default lane) Counters ---");
		output.WriteLine(result ?? "(null)");

		Assert.NotNull(result);
		Assert.Contains("Counter Data:", result);
	}

	[Fact]
	public async Task GetCounters_CounteredByHasMultipleChampions()
	{
		var result = await _service.GetCountersAsync("Ahri", "middle");

		output.WriteLine(result ?? "(null)");

		Assert.NotNull(result);
		var line = result.Split('\n').FirstOrDefault(l => l.StartsWith("Countered by:"));
		Assert.NotNull(line);
		// Should have at least 2 champions (comma-separated)
		var names = line.Replace("Countered by: ", "").Split(", ");
		output.WriteLine($"Countered by champions: [{string.Join("], [", names)}]");
		Assert.True(names.Length >= 2, $"Expected at least 2 counter champions, got {names.Length}");
	}

	// ──────────────────────────────────────────────────────────────
	//  Build page — has win rate, tier, pick/ban rate, rank, counters
	// ──────────────────────────────────────────────────────────────

	[Theory]
	[InlineData("Ahri", "middle")]
	[InlineData("Jinx", "bottom")]
	[InlineData("Thresh", "support")]
	[InlineData("Lee Sin", "jungle")]
	[InlineData("Darius", "top")]
	public async Task GetBuild_ReturnsData_ForChampionAndLane(string champion, string lane)
	{
		var result = await _service.GetBuildAsync(champion, lane);

		output.WriteLine($"--- {champion} ({lane}) Build ---");
		output.WriteLine(result ?? "(null)");
		output.WriteLine("");

		Assert.NotNull(result);
		Assert.Contains("Build Data:", result);
		Assert.Contains("Win Rate:", result);
	}

	[Fact]
	public async Task GetBuild_ReturnsChampionSpecificWinRate()
	{
		var result = await _service.GetBuildAsync("Ahri", "middle");

		output.WriteLine(result ?? "(null)");

		Assert.NotNull(result);
		Assert.Contains("Win Rate:", result);

		// Extract the win rate value and verify it's in a reasonable range
		var wr = double.Parse(ExtractField(result, "Win Rate:").Replace("%", ""));
		output.WriteLine($"Parsed win rate: {wr}%");
		Assert.True(wr >= 40 && wr <= 65, $"Win rate {wr}% seems out of range");
	}

	[Fact]
	public async Task GetBuild_ReturnsTier()
	{
		var result = await _service.GetBuildAsync("Ahri", "middle");

		Assert.NotNull(result);
		Assert.Contains("Tier:", result);

		var tier = ExtractField(result, "Tier:");
		output.WriteLine($"Tier: {tier}");
		Assert.Matches(@"^[SABCD][+-]?$|^S\+$", tier);
	}

	[Fact]
	public async Task GetBuild_ReturnsPickAndBanRate()
	{
		var result = await _service.GetBuildAsync("Ahri", "middle");

		output.WriteLine(result ?? "(null)");

		Assert.NotNull(result);
		Assert.Contains("Pick Rate:", result);
		Assert.Contains("Ban Rate:", result);
	}

	[Fact]
	public async Task GetBuild_ReturnsRank()
	{
		var result = await _service.GetBuildAsync("Ahri", "middle");

		output.WriteLine(result ?? "(null)");

		Assert.NotNull(result);
		Assert.Matches(@"Rank: \d+ / \d+", result);
	}

	[Fact]
	public async Task GetBuild_WinRatesDifferByChampion()
	{
		// Verify we're getting champion-specific data, not a global stat
		var ahri = await _service.GetBuildAsync("Ahri", "middle");
		var darius = await _service.GetBuildAsync("Darius", "top");

		Assert.NotNull(ahri);
		Assert.NotNull(darius);

		var ahriWr = ExtractField(ahri, "Win Rate:");
		var dariusWr = ExtractField(darius, "Win Rate:");

		output.WriteLine($"Ahri win rate: {ahriWr}");
		output.WriteLine($"Darius win rate: {dariusWr}");

		// These should be different values (catches the bug where all returned 51.75%)
		Assert.NotEqual(ahriWr, dariusWr);
	}

	// ──────────────────────────────────────────────────────────────
	//  Matchup page — win rate and game count
	// ──────────────────────────────────────────────────────────────

	[Theory]
	[InlineData("Ahri", "Zed", "middle")]
	[InlineData("Jinx", "Draven", "bottom")]
	[InlineData("Darius", "Garen", "top")]
	public async Task GetMatchup_ReturnsData_ForMatchupAndLane(string champion, string opponent, string lane)
	{
		var result = await _service.GetMatchupAsync(champion, opponent, lane);

		output.WriteLine($"--- {champion} vs {opponent} ({lane}) ---");
		output.WriteLine(result ?? "(null)");
		output.WriteLine("");

		Assert.NotNull(result);
		Assert.Contains($"{champion} vs {opponent}", result);
		Assert.Contains("Win Rate:", result);
	}

	[Fact]
	public async Task GetMatchup_ReturnsMatchupSpecificGameCount()
	{
		var result = await _service.GetMatchupAsync("Ahri", "Zed", "middle");

		output.WriteLine(result ?? "(null)");

		Assert.NotNull(result);
		Assert.Contains("Games:", result);

		// Game count should be matchup-specific (thousands, not millions)
		var gamesStr = ExtractField(result, "Games:").Replace(",", "");
		var games = int.Parse(gamesStr);
		output.WriteLine($"Matchup game count: {games}");
		Assert.True(games >= 100 && games < 500_000,
			$"Game count {games} seems wrong — should be matchup-specific, not global");
	}

	[Fact]
	public async Task GetMatchup_WinRateIsMatchupSpecific()
	{
		// Verify the win rate reflects the specific matchup, not a global stat
		var ahriVsZed = await _service.GetMatchupAsync("Ahri", "Zed", "middle");
		var ahriVsLux = await _service.GetMatchupAsync("Ahri", "Lux", "middle");

		Assert.NotNull(ahriVsZed);
		Assert.NotNull(ahriVsLux);

		var wrVsZed = ExtractField(ahriVsZed, "Win Rate:");
		var wrVsLux = ExtractField(ahriVsLux, "Win Rate:");

		output.WriteLine($"Ahri vs Zed: {wrVsZed}");
		output.WriteLine($"Ahri vs Lux: {wrVsLux}");

		// Matchup win rates should differ (if they're both 51.75%, scraping is broken)
		Assert.NotEqual(wrVsZed, wrVsLux);
	}

	// ──────────────────────────────────────────────────────────────
	//  Build recommendations — items, runes, spells, skills
	// ──────────────────────────────────────────────────────────────

	[Theory]
	[InlineData("Ahri", "Zed", "middle")]
	[InlineData("Jinx", "Draven", "bottom")]
	[InlineData("Darius", "Garen", "top")]
	public async Task GetMatchup_ReturnsBuildData(string champion, string opponent, string lane)
	{
		var result = await _service.GetMatchupAsync(champion, opponent, lane);

		output.WriteLine($"--- {champion} vs {opponent} ({lane}) Build ---");
		output.WriteLine(result ?? "(null)");

		Assert.NotNull(result);
		Assert.Contains("Starting Items:", result);
		Assert.Contains("Core Build:", result);
		Assert.Contains("Summoner Spells:", result);
		Assert.Contains("Skill Priority:", result);
	}

	[Fact]
	public async Task GetMatchup_ReturnsStartingItems()
	{
		var result = await _service.GetMatchupAsync("Ahri", "Zed", "middle");

		output.WriteLine(result ?? "(null)");

		Assert.NotNull(result);
		var items = ExtractField(result, "Starting Items:");
		output.WriteLine($"Starting Items: {items}");

		// Should have at least 1 item
		var itemList = items.Split(", ");
		Assert.True(itemList.Length >= 1, $"Expected at least 1 starting item, got {itemList.Length}");
	}

	[Fact]
	public async Task GetMatchup_ReturnsCoreBuilt()
	{
		var result = await _service.GetMatchupAsync("Ahri", "Zed", "middle");

		output.WriteLine(result ?? "(null)");

		Assert.NotNull(result);
		var items = ExtractField(result, "Core Build:");
		output.WriteLine($"Core Build: {items}");

		// Core build uses arrows and should have at least 2 items
		var itemList = items.Split(" → ");
		Assert.True(itemList.Length >= 2, $"Expected at least 2 core items, got {itemList.Length}");
	}

	[Fact]
	public async Task GetMatchup_ReturnsSummonerSpells()
	{
		var result = await _service.GetMatchupAsync("Ahri", "Zed", "middle");

		output.WriteLine(result ?? "(null)");

		Assert.NotNull(result);
		var spells = ExtractField(result, "Summoner Spells:");
		output.WriteLine($"Summoner Spells: {spells}");

		// Should have exactly 2 summoner spells
		var spellList = spells.Split(", ");
		Assert.Equal(2, spellList.Length);
		// Flash is nearly universal
		Assert.Contains("Flash", spells);
	}

	[Fact]
	public async Task GetMatchup_ReturnsPrimaryRunes()
	{
		var result = await _service.GetMatchupAsync("Ahri", "Zed", "middle");

		output.WriteLine(result ?? "(null)");

		Assert.NotNull(result);
		var runes = ExtractField(result, "Primary Runes:");
		output.WriteLine($"Primary Runes: {runes}");

		// Should have keystone + 3 minor runes = 4 total
		var runeList = runes.Split(", ");
		Assert.True(runeList.Length >= 3, $"Expected at least 3 primary runes, got {runeList.Length}");
	}

	[Fact]
	public async Task GetMatchup_ReturnsSecondaryRunes()
	{
		var result = await _service.GetMatchupAsync("Ahri", "Zed", "middle");

		output.WriteLine(result ?? "(null)");

		Assert.NotNull(result);
		var runes = ExtractField(result, "Secondary Runes:");
		output.WriteLine($"Secondary Runes: {runes}");

		// Should have exactly 2 secondary runes
		var runeList = runes.Split(", ");
		Assert.Equal(2, runeList.Length);
	}

	[Fact]
	public async Task GetMatchup_ReturnsSkillPriority()
	{
		var result = await _service.GetMatchupAsync("Ahri", "Zed", "middle");

		output.WriteLine(result ?? "(null)");

		Assert.NotNull(result);
		var skills = ExtractField(result, "Skill Priority:");
		output.WriteLine($"Skill Priority: {skills}");

		// Should have exactly 3 skills (Q/W/E, not R)
		var skillList = skills.Split(" > ");
		Assert.Equal(3, skillList.Length);
		// Each should be a single letter Q, W, or E
		foreach (var s in skillList)
			Assert.Matches(@"^[QWE]$", s);
	}

	[Theory]
	[InlineData("Ahri", "middle")]
	[InlineData("Jinx", "bottom")]
	[InlineData("Darius", "top")]
	public async Task GetBuild_ReturnsBuildRecommendations(string champion, string lane)
	{
		var result = await _service.GetBuildAsync(champion, lane);

		output.WriteLine($"--- {champion} ({lane}) Build ---");
		output.WriteLine(result ?? "(null)");

		Assert.NotNull(result);
		Assert.Contains("Starting Items:", result);
		Assert.Contains("Core Build:", result);
		Assert.Contains("Summoner Spells:", result);
		Assert.Contains("Skill Priority:", result);
	}

	// ──────────────────────────────────────────────────────────────
	//  Lane normalization (pure unit tests)
	// ──────────────────────────────────────────────────────────────

	[Theory]
	[InlineData("mid", "middle")]
	[InlineData("Mid", "middle")]
	[InlineData("MID", "middle")]
	[InlineData("bot", "bottom")]
	[InlineData("adc", "bottom")]
	[InlineData("jg", "jungle")]
	[InlineData("jung", "jungle")]
	[InlineData("sup", "support")]
	[InlineData("supp", "support")]
	[InlineData("top", "top")]
	[InlineData("middle", "middle")]
	[InlineData("jungle", "jungle")]
	[InlineData("support", "support")]
	[InlineData("bottom", "bottom")]
	public void NormalizeLane_MapsAliasesCorrectly(string input, string expected)
	{
		Assert.Equal(expected, LoLalyticsService.NormalizeLane(input));
	}

	// ──────────────────────────────────────────────────────────────
	//  Slug generation (pure unit tests)
	// ──────────────────────────────────────────────────────────────

	[Theory]
	[InlineData("Ahri", "ahri")]
	[InlineData("Miss Fortune", "missfortune")]
	[InlineData("Cho'Gath", "chogath")]
	[InlineData("Lee Sin", "leesin")]
	[InlineData("Dr. Mundo", "drmundo")]
	[InlineData("Kog'Maw", "kogmaw")]
	[InlineData("Kai'Sa", "kaisa")]
	[InlineData("K'Sante", "ksante")]
	[InlineData("Bel'Veth", "belveth")]
	[InlineData("Rek'Sai", "reksai")]
	public void ToSlug_ConvertsCorrectly(string input, string expected)
	{
		Assert.Equal(expected, LoLalyticsService.ToSlug(input));
	}

	// ──────────────────────────────────────────────────────────────
	//  Lane aliases work end-to-end with live data
	// ──────────────────────────────────────────────────────────────

	[Theory]
	[InlineData("mid")]
	[InlineData("bot")]
	[InlineData("jg")]
	[InlineData("sup")]
	public async Task GetCounters_WorksWithLaneAliases(string alias)
	{
		var champion = alias switch
		{
			"mid" => "Ahri",
			"bot" => "Jinx",
			"jg" => "Lee Sin",
			"sup" => "Thresh",
			_ => "Ahri"
		};

		var result = await _service.GetCountersAsync(champion, alias);

		output.WriteLine($"--- {champion} (alias: {alias}) Counters ---");
		output.WriteLine(result ?? "(null)");

		Assert.NotNull(result);
		Assert.Contains("Counter Data:", result);
	}

	// ──────────────────────────────────────────────────────────────
	//  Comprehensive data dump — shows ALL extracted fields
	// ──────────────────────────────────────────────────────────────

	[Fact]
	public async Task DumpAllData_Ahri_Middle()
	{
		output.WriteLine("=== FULL DATA DUMP: Ahri (middle) ===\n");

		var counters = await _service.GetCountersAsync("Ahri", "middle");
		output.WriteLine("--- Counters ---");
		output.WriteLine(counters ?? "(null - scraping may be broken)");
		output.WriteLine("");

		var build = await _service.GetBuildAsync("Ahri", "middle");
		output.WriteLine("--- Build ---");
		output.WriteLine(build ?? "(null - scraping may be broken)");
		output.WriteLine("");

		var matchup = await _service.GetMatchupAsync("Ahri", "Zed", "middle");
		output.WriteLine("--- Matchup (vs Zed) ---");
		output.WriteLine(matchup ?? "(null - scraping may be broken)");
		output.WriteLine("");

		Assert.NotNull(counters);
		Assert.NotNull(build);
		Assert.NotNull(matchup);
	}

	// ──────────────────────────────────────────────────────────────
	//  Invalid champion — should return null, not crash
	// ──────────────────────────────────────────────────────────────

	[Fact]
	public async Task GetCounters_ReturnsNull_ForInvalidChampion()
	{
		var result = await _service.GetCountersAsync("NotARealChampion123");

		output.WriteLine(result ?? "(null — expected)");

		Assert.Null(result);
	}

	// ──────────────────────────────────────────────────────────────
	//  Helper
	// ──────────────────────────────────────────────────────────────

	private static string ExtractField(string result, string prefix)
	{
		var line = result.Split('\n').First(l => l.StartsWith(prefix));
		return line[(prefix.Length + 1)..]; // +1 for the space after the colon
	}
}
