using System.Text;

namespace DiscordTTSBot.LLM
{
	public class LeagueContextExtractor
	{
		private readonly LeagueDataService _data;

		// Pre-built lookup: normalized form → champion name
		private Dictionary<string, string> _champLookup = new(StringComparer.OrdinalIgnoreCase);

		// Pre-built lookup: normalized form → item name
		private Dictionary<string, string> _itemLookup = new(StringComparer.OrdinalIgnoreCase);

		// Max word count in any item name (for n-gram matching)
		private int _maxItemWords = 1;

		public LeagueContextExtractor(LeagueDataService data)
		{
			_data = data;
			BuildChampionLookup();
			BuildItemLookup();
		}

		private void BuildChampionLookup()
		{
			foreach (var name in _data.ChampionNames)
			{
				var lower = name.ToLower();
				_champLookup.TryAdd(lower, name);
				_champLookup.TryAdd(Normalize(lower), name);
			}

			// Common STT phonetic mistakes: transcribed form → actual champion name
			var phoneticAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				["ari"] = "Ahri",
				["auri"] = "Ahri",
				["scion"] = "Sion",
				["cyan"] = "Sion",
				["zion"] = "Sion",
				["nami"] = "Nami",
				["annie"] = "Annie",
				["ash"] = "Ashe",
				["jinx"] = "Jinx",
				["lux"] = "Lux",
				["zed"] = "Zed",
				["lee sin"] = "Lee Sin",
				["leeson"] = "Lee Sin",
				["leesin"] = "Lee Sin",
				["jason"] = "Jhin",
				["gene"] = "Jhin",
				["gin"] = "Jhin",
				["yasuo"] = "Yasuo",
				["yasuko"] = "Yasuo",
				["yasu"] = "Yasuo",
				["yonay"] = "Yone",
				["yoni"] = "Yone",
				["vane"] = "Vayne",
				["wayne"] = "Vayne",
				["vain"] = "Vayne",
				["blitz"] = "Blitzcrank",
				["blitzcrank"] = "Blitzcrank",
				["mundo"] = "Dr. Mundo",
				["dr mundo"] = "Dr. Mundo",
				["doctor mundo"] = "Dr. Mundo",
				["thresh"] = "Thresh",
				["fresh"] = "Thresh",
				["caitlin"] = "Caitlyn",
				["kaitlyn"] = "Caitlyn",
				["nautilus"] = "Nautilus",
				["naughtless"] = "Nautilus",
				["malzahar"] = "Malzahar",
				["malzar"] = "Malzahar",
				["tryndamere"] = "Tryndamere",
				["tryndameer"] = "Tryndamere",
				["trynda"] = "Tryndamere",
				["warwick"] = "Warwick",
				["warrick"] = "Warwick",
				["viego"] = "Viego",
				["vago"] = "Viego",
				["k'sante"] = "K'Sante",
				["kasante"] = "K'Sante",
				["cosante"] = "K'Sante",
				["kaisa"] = "Kai'Sa",
				["kaiser"] = "Kai'Sa",
				["kysa"] = "Kai'Sa",
				["chogath"] = "Cho'Gath",
				["cho gath"] = "Cho'Gath",
				["kogmaw"] = "Kog'Maw",
				["kog maw"] = "Kog'Maw",
				["reksai"] = "Rek'Sai",
				["rek sai"] = "Rek'Sai",
				["khazix"] = "Kha'Zix",
				["kha zix"] = "Kha'Zix",
				["kazix"] = "Kha'Zix",
				["velkoz"] = "Vel'Koz",
				["vel koz"] = "Vel'Koz",
				["veigar"] = "Veigar",
				["vigor"] = "Veigar",
				["nasus"] = "Nasus",
				["nassus"] = "Nasus",
				["zyra"] = "Zyra",
				["zara"] = "Zyra",
				["sera"] = "Zyra",
				["teemo"] = "Teemo",
				["timo"] = "Teemo",
				["garen"] = "Garen",
				["garren"] = "Garen",
				["darius"] = "Darius",
				["darious"] = "Darius",

				// Common community nicknames / abbreviations
				["mf"] = "Miss Fortune",
				["tf"] = "Twisted Fate",
				["gp"] = "Gangplank",
				["ww"] = "Warwick",
				["yi"] = "Master Yi",
				["j4"] = "Jarvan IV",
				["jarvan"] = "Jarvan IV",
				["noc"] = "Nocturne",
				["noct"] = "Nocturne",
				["morde"] = "Mordekaiser",
				["mord"] = "Mordekaiser",
				["heimer"] = "Heimerdinger",
				["donger"] = "Heimerdinger",
				["trynd"] = "Tryndamere",
				["ez"] = "Ezreal",
				["cass"] = "Cassiopeia",
				["kat"] = "Katarina",
				["nid"] = "Nidalee",
				["ori"] = "Orianna",
				["renek"] = "Renekton",
				["sej"] = "Sejuani",
				["shyv"] = "Shyvana",
				["vlad"] = "Vladimir",
				["xin"] = "Xin Zhao",
				["xz"] = "Xin Zhao",
				["malph"] = "Malphite",
				["ali"] = "Alistar",
				["eve"] = "Evelynn",
				["fiddle"] = "Fiddlesticks",
				["wu"] = "Wukong",
				["raka"] = "Soraka",
				["morg"] = "Morgana",
				["lb"] = "LeBlanc",
				["leblanc"] = "LeBlanc",
				["le blanc"] = "LeBlanc",
				["nunu"] = "Nunu & Willump",
				["leo"] = "Leona",
				["panth"] = "Pantheon",
				["kass"] = "Kassadin",
				["kha"] = "Kha'Zix",
				["kog"] = "Kog'Maw",
				["cho"] = "Cho'Gath",
				["vel"] = "Vel'Koz",
				["rek"] = "Rek'Sai",
				["kai"] = "Kai'Sa",
				["hec"] = "Hecarim",
				["heca"] = "Hecarim",
				["voli"] = "Volibear",
				["tahm"] = "Tahm Kench",
				["tk"] = "Tahm Kench",
				["sol"] = "Aurelion Sol",
				["asol"] = "Aurelion Sol",
				["a sol"] = "Aurelion Sol",
			};

			foreach (var (alias, champName) in phoneticAliases)
				_champLookup.TryAdd(alias, champName);
		}

		private void BuildItemLookup()
		{
			foreach (var name in _data.ItemNames)
			{
				var lower = name.ToLower();
				_itemLookup.TryAdd(lower, name);
				_itemLookup.TryAdd(Normalize(lower), name);

				// Also add the "key word" for multi-word items (e.g. "deathcap" for "Rabadon's Deathcap")
				var words = lower.Split(' ', StringSplitOptions.RemoveEmptyEntries);
				_maxItemWords = Math.Max(_maxItemWords, words.Length);

				// Add each significant word (4+ chars) as a lookup pointing to the full item
				// This helps match "deathcap" → "Rabadon's Deathcap"
				foreach (var word in words)
				{
					var cleanWord = Normalize(word);
					if (cleanWord.Length >= 5) // longer threshold to avoid false positives
						_itemLookup.TryAdd(cleanWord, name);
				}
			}

			// Common STT phonetic mistakes for items
			var phoneticAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				// Possessives often get mangled by STT
				["rabadons"] = "Rabadon's Deathcap",
				["rabadon"] = "Rabadon's Deathcap",
				["death cap"] = "Rabadon's Deathcap",
				["deathcap"] = "Rabadon's Deathcap",
				["robodons"] = "Rabadon's Deathcap",

				["zhonyas"] = "Zhonya's Hourglass",
				["zhonya"] = "Zhonya's Hourglass",
				["zonya"] = "Zhonya's Hourglass",
				["zonyas"] = "Zhonya's Hourglass",
				["hourglass"] = "Zhonya's Hourglass",

				["infinity edge"] = "Infinity Edge",
				["ie"] = "Infinity Edge",

				["morellonomicon"] = "Morellonomicon",
				["morello"] = "Morellonomicon",

				["nashors"] = "Nashor's Tooth",
				["nashor"] = "Nashor's Tooth",
				["nashors tooth"] = "Nashor's Tooth",

				["guardian angel"] = "Guardian Angel",
				["ga"] = "Guardian Angel",

				["rapid fire"] = "Rapid Firecannon",
				["rapidfire"] = "Rapid Firecannon",
				["rfc"] = "Rapid Firecannon",

				["phantom dancer"] = "Phantom Dancer",
				["pd"] = "Phantom Dancer",

				["blade of the ruined king"] = "Blade of The Ruined King",
				["bork"] = "Blade of The Ruined King",
				["botrk"] = "Blade of The Ruined King",
				["ruined king"] = "Blade of The Ruined King",

				["deadmans"] = "Dead Man's Plate",
				["dead mans"] = "Dead Man's Plate",
				["dead mans plate"] = "Dead Man's Plate",
				["deadman"] = "Dead Man's Plate",

				["spirit visage"] = "Spirit Visage",
				["visage"] = "Spirit Visage",

				["randuins"] = "Randuin's Omen",
				["randuin"] = "Randuin's Omen",
				["randuins omen"] = "Randuin's Omen",

				["thornmail"] = "Thornmail",
				["thorn mail"] = "Thornmail",

				["warmogs"] = "Warmog's Armor",
				["warmog"] = "Warmog's Armor",
				["warmogs armor"] = "Warmog's Armor",

				["steraks"] = "Sterak's Gage",
				["steraks gage"] = "Sterak's Gage",
				["sterak"] = "Sterak's Gage",

				["sunfire"] = "Sunfire Aegis",
				["sunfire aegis"] = "Sunfire Aegis",

				["liandrys"] = "Liandry's Torment",
				["liandry"] = "Liandry's Torment",
				["liandrys torment"] = "Liandry's Torment",

				["ludens"] = "Luden's Companion",
				["luden"] = "Luden's Companion",
				["ludens companion"] = "Luden's Companion",

				["void staff"] = "Void Staff",
				["voidstaff"] = "Void Staff",

				["mejais"] = "Mejai's Soulstealer",
				["mejai"] = "Mejai's Soulstealer",

				["banshees"] = "Banshee's Veil",
				["banshee"] = "Banshee's Veil",
				["banshees veil"] = "Banshee's Veil",

				["deaths dance"] = "Death's Dance",
				["death dance"] = "Death's Dance",
				["dd"] = "Death's Dance",

				["maw of malmortius"] = "Maw of Malmortius",
				["maw"] = "Maw of Malmortius",
				["malmortius"] = "Maw of Malmortius",

				["mercurial scimitar"] = "Mercurial Scimitar",
				["qss"] = "Mercurial Scimitar",

				["black cleaver"] = "Black Cleaver",
				["cleaver"] = "Black Cleaver",

				["trinity force"] = "Trinity Force",
				["triforce"] = "Trinity Force",
				["tri force"] = "Trinity Force",

				["divine sunderer"] = "Divine Sunderer",
				["sunderer"] = "Divine Sunderer",

				["cosmic drive"] = "Cosmic Drive",

				["rod of ages"] = "Rod of Ages",
				["roa"] = "Rod of Ages",

				["frozen heart"] = "Frozen Heart",

				["force of nature"] = "Force of Nature",
				["fon"] = "Force of Nature",

				["hullbreaker"] = "Hullbreaker",
				["hull breaker"] = "Hullbreaker",

				["youmuus"] = "Youmuu's Ghostblade",
				["youmuu"] = "Youmuu's Ghostblade",
				["ghostblade"] = "Youmuu's Ghostblade",
				["umoos"] = "Youmuu's Ghostblade",

				["edge of night"] = "Edge of Night",

				["collector"] = "The Collector",
				["the collector"] = "The Collector",

				["kraken slayer"] = "Kraken Slayer",
				["kraken"] = "Kraken Slayer",

				["shieldbow"] = "Immortal Shieldbow",
				["immortal shieldbow"] = "Immortal Shieldbow",

				["galeforce"] = "Galeforce",

				["shurelyas"] = "Shurelya's Battlesong",
				["shurelya"] = "Shurelya's Battlesong",

				["redemption"] = "Redemption",

				["mikaels"] = "Mikael's Blessing",
				["mikael"] = "Mikael's Blessing",

				["ardent censer"] = "Ardent Censer",
				["ardent"] = "Ardent Censer",

				["staff of flowing water"] = "Staff of Flowing Water",

				["abyssal mask"] = "Abyssal Mask",
				["abyssal"] = "Abyssal Mask",

				["iceborn gauntlet"] = "Iceborn Gauntlet",
				["iceborn"] = "Iceborn Gauntlet",

				["gargoyle stoneplate"] = "Gargoyle Stoneplate",
				["gargoyle"] = "Gargoyle Stoneplate",
				["stoneplate"] = "Gargoyle Stoneplate",
			};

			foreach (var (alias, itemName) in phoneticAliases)
				_itemLookup.TryAdd(alias, itemName);
		}

		/// <summary>
		/// Detects champion/item names in the user message, fetches their data,
		/// and returns the message with a [DATA] context block prepended.
		/// Returns the original message if no entities are found.
		/// </summary>
		public async Task<string> ExtractContextAsync(string userMessage)
		{
			var parts = new List<string>();

			var champions = FindChampions(userMessage);
			foreach (var name in champions.Take(2))
			{
				var info = await _data.GetChampionAsync(name);
				if (info is not null)
					parts.Add(FormatChampion(info));
			}

			var items = FindItems(userMessage);
			foreach (var name in items.Take(3))
			{
				var info = _data.GetItem(name);
				if (info is not null)
					parts.Add(FormatItem(info));
			}

			if (parts.Count == 0)
				return userMessage;

			return $"[DATA]\n{string.Join("\n", parts)}[/DATA]\n{userMessage}";
		}

		private List<string> FindChampions(string text)
		{
			var found = new HashSet<string>();
			var textLower = text.ToLower();
			var words = textLower.Split([' ', ',', '.', '!', '?', ';', ':'], StringSplitOptions.RemoveEmptyEntries);

			// 1. Check exact/normalized substring matches from the lookup table
			foreach (var (alias, champName) in _champLookup)
			{
				if (alias.Length < 2) continue;
				// Short aliases (2-char like "mf", "tf") must be exact word matches to avoid false positives
				if (alias.Length < 3)
				{
					if (words.Contains(alias))
						found.Add(champName);
				}
				else if (textLower.Contains(alias))
					found.Add(champName);
			}

			// 2. Fuzzy match: for each word (and bigram), find close champion names
			if (found.Count == 0)
			{
				foreach (var word in words)
				{
					if (word.Length < 3) continue;
					var match = FuzzyMatch(word, _champLookup);
					if (match is not null)
						found.Add(match);
				}

				for (int i = 0; i < words.Length - 1; i++)
				{
					var bigram = words[i] + " " + words[i + 1];
					var match = FuzzyMatch(bigram, _champLookup);
					if (match is not null)
						found.Add(match);

					var collapsed = words[i] + words[i + 1];
					match = FuzzyMatch(collapsed, _champLookup);
					if (match is not null)
						found.Add(match);
				}
			}

			return [.. found];
		}

		private List<string> FindItems(string text)
		{
			var found = new HashSet<string>();
			var textLower = text.ToLower();
			var words = textLower.Split([' ', ',', '.', '!', '?', ';', ':'], StringSplitOptions.RemoveEmptyEntries);

			// 1. Check exact/normalized substring matches from the lookup table
			foreach (var (alias, itemName) in _itemLookup)
			{
				if (alias.Length < 2) continue;
				// Short aliases (2-char like "ie", "ga", "dd") must be exact word matches
				if (alias.Length < 3)
				{
					if (words.Contains(alias))
						found.Add(itemName);
				}
				else if (textLower.Contains(alias))
					found.Add(itemName);
			}

			// 2. Fuzzy match single words and n-grams
			if (found.Count == 0)
			{
				// Single words
				foreach (var word in words)
				{
					if (word.Length < 4) continue;
					var match = FuzzyMatch(word, _itemLookup);
					if (match is not null)
						found.Add(match);
				}

				// N-grams (bigrams and trigrams for multi-word items)
				for (int n = 2; n <= Math.Min(_maxItemWords, 4); n++)
				{
					for (int i = 0; i <= words.Length - n; i++)
					{
						var ngram = string.Join(" ", words.Skip(i).Take(n));
						var match = FuzzyMatch(ngram, _itemLookup);
						if (match is not null)
							found.Add(match);
					}
				}
			}

			return [.. found];
		}

		/// <summary>
		/// Finds the best match for a word in the given lookup using edit distance.
		/// Returns the matched value or null if nothing is close enough.
		/// </summary>
		private static string? FuzzyMatch(string word, Dictionary<string, string> lookup)
		{
			string? bestMatch = null;
			var bestDistance = int.MaxValue;

			foreach (var (alias, value) in lookup)
			{
				if (alias.Length < 3) continue;

				var lenDiff = Math.Abs(word.Length - alias.Length);
				if (lenDiff > 2) continue;

				var distance = LevenshteinDistance(word, alias);

				// Threshold: allow 1 edit for short names (3-5 chars), 2 for longer ones
				var maxDistance = alias.Length <= 5 ? 1 : 2;

				if (distance < bestDistance && distance <= maxDistance)
				{
					bestDistance = distance;
					bestMatch = value;
				}
			}

			return bestMatch;
		}

		private static string Normalize(string s) =>
			s.Replace("'", "").Replace(".", "").Replace(" ", "").Replace("-", "");

		private static int LevenshteinDistance(string a, string b)
		{
			if (a.Length == 0) return b.Length;
			if (b.Length == 0) return a.Length;

			var costs = new int[b.Length + 1];
			for (var i = 0; i <= b.Length; i++)
				costs[i] = i;

			for (var i = 1; i <= a.Length; i++)
			{
				var prev = costs[0];
				costs[0] = i;

				for (var j = 1; j <= b.Length; j++)
				{
					var cost = a[i - 1] == b[j - 1] ? 0 : 1;
					var current = costs[j];
					costs[j] = Math.Min(Math.Min(costs[j] + 1, costs[j - 1] + 1), prev + cost);
					prev = current;
				}
			}

			return costs[b.Length];
		}

		private static string FormatChampion(ChampionInfo c)
		{
			var sb = new StringBuilder();
			sb.AppendLine($"{c.Name} ({string.Join("/", c.Roles)}) - {c.DamageType} {c.AttackType}");
			sb.AppendLine($"  P: {c.Passive.Name} - {c.Passive.Description}");

			foreach (var a in c.Abilities)
			{
				sb.Append($"  {a.Key}: {a.Name}");
				if (a.Cooldown.Length > 0) sb.Append($" (CD:{a.Cooldown})");
				if (a.Cost.Length > 0) sb.Append($" (Cost:{a.Cost})");
				sb.AppendLine($" - {a.Description}");
			}

			return sb.ToString();
		}

		private static string FormatItem(ItemInfo item)
		{
			var sb = new StringBuilder();
			sb.Append($"{item.Name} ({item.Price}g)");
			if (item.BuildsFrom.Length > 0)
				sb.Append($" [from: {string.Join(" + ", item.BuildsFrom)}]");
			sb.AppendLine($" - {item.Description}");
			return sb.ToString();
		}
	}
}
