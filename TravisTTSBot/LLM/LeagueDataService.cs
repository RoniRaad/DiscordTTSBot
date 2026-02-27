using System.Text.Json;
using System.Text.RegularExpressions;

namespace DiscordTTSBot.LLM
{
	public partial class LeagueDataService
	{
		private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
		private const string BaseUrl = "https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default/v1";

		private Dictionary<string, int> _championNameToId = new(StringComparer.OrdinalIgnoreCase);
		private List<string> _championNames = [];
		private Dictionary<int, string> _championIdToName = [];

		private Dictionary<string, ItemInfo> _itemsByName = new(StringComparer.OrdinalIgnoreCase);
		private Dictionary<int, string> _itemIdToName = [];
		private List<string> _itemNames = [];

		private readonly Lock _cacheLock = new();
		private readonly Dictionary<int, (ChampionInfo Info, DateTime CachedAt)> _championCache = [];
		private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);

		public IReadOnlyList<string> ChampionNames => _championNames;
		public IReadOnlyList<string> ItemNames => _itemNames;

		public async Task InitializeAsync()
		{
			try
			{
				// Load champion summary
				var summaryJson = await _http.GetStringAsync($"{BaseUrl}/champion-summary.json");
				using var summaryDoc = JsonDocument.Parse(summaryJson);

				foreach (var champ in summaryDoc.RootElement.EnumerateArray())
				{
					var id = champ.GetProperty("id").GetInt32();
					var name = champ.GetProperty("name").GetString() ?? "";
					if (id < 0 || string.IsNullOrEmpty(name)) continue;

					_championNameToId[name] = id;
					_championIdToName[id] = name;
				}

				_championNames = [.. _championNameToId.Keys.Order()];
				Console.WriteLine($"[League] Loaded {_championNames.Count} champions");

				// Load items
				var itemsJson = await _http.GetStringAsync($"{BaseUrl}/items.json");
				using var itemsDoc = JsonDocument.Parse(itemsJson);

				foreach (var item in itemsDoc.RootElement.EnumerateArray())
				{
					var inStore = item.GetProperty("inStore").GetBoolean();
					if (!inStore) continue;

					var id = item.GetProperty("id").GetInt32();
					var name = item.GetProperty("name").GetString() ?? "";
					if (string.IsNullOrEmpty(name)) continue;

					_itemIdToName[id] = name;
				}

				// Second pass to resolve build paths to names
				foreach (var item in itemsDoc.RootElement.EnumerateArray())
				{
					var inStore = item.GetProperty("inStore").GetBoolean();
					if (!inStore) continue;

					var name = item.GetProperty("name").GetString() ?? "";
					if (string.IsNullOrEmpty(name)) continue;

					var info = ParseItem(item);
					_itemsByName[name] = info;
				}

				_itemNames = [.. _itemsByName.Keys.Order()];
				Console.WriteLine($"[League] Loaded {_itemNames.Count} items");
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine($"[League] Failed to initialize: {ex.Message}");
			}
		}

		public async Task<ChampionInfo?> GetChampionAsync(string name)
		{
			if (!_championNameToId.TryGetValue(name, out var id))
				return null;

			lock (_cacheLock)
			{
				if (_championCache.TryGetValue(id, out var cached) && DateTime.UtcNow - cached.CachedAt < CacheTtl)
					return cached.Info;
			}

			try
			{
				var json = await _http.GetStringAsync($"{BaseUrl}/champions/{id}.json");
				using var doc = JsonDocument.Parse(json);
				var info = ParseChampion(doc.RootElement);

				lock (_cacheLock)
				{
					_championCache[id] = (info, DateTime.UtcNow);
				}

				return info;
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine($"[League] Failed to fetch champion {name} (id {id}): {ex.Message}");
				return null;
			}
		}

		public ItemInfo? GetItem(string name)
		{
			return _itemsByName.GetValueOrDefault(name);
		}

		private static ChampionInfo ParseChampion(JsonElement root)
		{
			var name = root.GetProperty("name").GetString() ?? "";
			var title = root.GetProperty("title").GetString() ?? "";

			// Roles
			var roles = root.GetProperty("roles").EnumerateArray()
				.Select(r => r.GetString() ?? "")
				.Where(r => r.Length > 0)
				.ToArray();

			// Tactical info
			var tactical = root.GetProperty("tacticalInfo");
			var damageType = tactical.GetProperty("damageType").GetString() ?? "";
			var attackType = tactical.GetProperty("attackType").GetString() ?? "";

			// Clean up damage type (e.g. "kMagic" → "Magic")
			if (damageType.StartsWith('k'))
				damageType = damageType[1..];

			// Playstyle
			var playstyle = root.GetProperty("playstyleInfo");

			// Passive
			var passiveEl = root.GetProperty("passive");
			var passive = new PassiveInfo(
				passiveEl.GetProperty("name").GetString() ?? "",
				CleanDescription(passiveEl.GetProperty("description").GetString() ?? "")
			);

			// Spells (Q, W, E, R)
			var spellKeys = new[] { "Q", "W", "E", "R" };
			var spells = root.GetProperty("spells").EnumerateArray()
				.Select((spell, i) =>
				{
					var key = i < spellKeys.Length ? spellKeys[i] : $"Spell{i}";

					var cooldownCoeffs = spell.GetProperty("cooldownCoefficients").EnumerateArray()
						.Select(c => c.GetSingle())
						.Where(c => c > 0)
						.ToArray();

					var costCoeffs = spell.GetProperty("costCoefficients").EnumerateArray()
						.Select(c => c.GetSingle())
						.Where(c => c > 0)
						.ToArray();

					return new AbilityInfo(
						key,
						spell.GetProperty("name").GetString() ?? "",
						CleanDescription(spell.GetProperty("dynamicDescription").GetString()
							?? spell.GetProperty("description").GetString() ?? ""),
						FormatCoefficients(cooldownCoeffs, "s"),
						FormatCoefficients(costCoeffs)
					);
				})
				.ToArray();

			return new ChampionInfo(name, title, roles, passive, spells, damageType, attackType);
		}

		private ItemInfo ParseItem(JsonElement item)
		{
			var name = item.GetProperty("name").GetString() ?? "";
			var description = CleanDescription(item.GetProperty("description").GetString() ?? "");
			var priceTotal = item.GetProperty("priceTotal").GetInt32();

			var categories = item.GetProperty("categories").EnumerateArray()
				.Select(c => c.GetString() ?? "")
				.Where(c => c.Length > 0)
				.ToArray();

			var buildsFrom = item.GetProperty("from").EnumerateArray()
				.Select(id => id.GetInt32())
				.Where(id => _itemIdToName.ContainsKey(id))
				.Select(id => _itemIdToName[id])
				.ToArray();

			var buildsInto = item.GetProperty("to").EnumerateArray()
				.Select(id => id.GetInt32())
				.Where(id => _itemIdToName.ContainsKey(id))
				.Select(id => _itemIdToName[id])
				.ToArray();

			return new ItemInfo(name, description, priceTotal, categories, buildsFrom, buildsInto);
		}

		private static string CleanDescription(string raw)
		{
			if (string.IsNullOrEmpty(raw)) return "";

			// Remove HTML/XML tags
			var text = HtmlTagRegex().Replace(raw, " ");

			// Replace @Variable@ template tokens with X
			text = TemplateVarRegex().Replace(text, "X");

			// Replace {{ variable }} tokens with X
			text = MustacheVarRegex().Replace(text, "X");

			// Remove %i:...% format tokens
			text = FormatTokenRegex().Replace(text, "");

			// Collapse whitespace
			text = WhitespaceRegex().Replace(text, " ").Trim();

			return text;
		}

		private static string FormatCoefficients(float[] values, string suffix = "")
		{
			if (values.Length == 0) return "";
			var distinct = values.Distinct().ToArray();
			if (distinct.Length == 1) return $"{distinct[0]:G}{suffix}";
			return string.Join("/", values.Select(v => $"{v:G}")) + suffix;
		}

		[GeneratedRegex(@"<[^>]+>")]
		private static partial Regex HtmlTagRegex();

		[GeneratedRegex(@"@\w+@")]
		private static partial Regex TemplateVarRegex();

		[GeneratedRegex(@"\{\{[^}]+\}\}")]
		private static partial Regex MustacheVarRegex();

		[GeneratedRegex(@"%\w+:\w+%")]
		private static partial Regex FormatTokenRegex();

		[GeneratedRegex(@"\s+")]
		private static partial Regex WhitespaceRegex();
	}

	public record ChampionInfo(
		string Name,
		string Title,
		string[] Roles,
		PassiveInfo Passive,
		AbilityInfo[] Abilities,
		string DamageType,
		string AttackType
	);

	public record PassiveInfo(string Name, string Description);

	public record AbilityInfo(
		string Key,
		string Name,
		string Description,
		string Cooldown,
		string Cost
	);

	public record ItemInfo(
		string Name,
		string Description,
		int Price,
		string[] Categories,
		string[] BuildsFrom,
		string[] BuildsInto
	);
}
