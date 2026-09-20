using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace IptvPlayer.Contracts.Channels;

public static class BroadcastChannelIdentity
{
	private static readonly HashSet<string> QualityTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
	{
		"SD", "HD", "FHD", "UHD", "RAW", "HEVC", "H265", "H264", "4K", "8K",
		"FPS", "AV1", "VP9", "MPEG2", "MPEG4", "X264", "X265", "HDR", "HDR10", "HDR10PLUS",
		"HEVC10", "DOLBY", "VISION", "DV", "HQ", "LQ", "576", "576P", "720", "720P",
		"1080", "1080P", "1440", "1440P", "2160", "2160P", "3840", "3840P", "4320", "4320P"
	};

	private static readonly HashSet<string> ResolutionTokens = new HashSet<string>(StringComparer.Ordinal) { "576", "720", "1080", "1440", "2160", "3840", "4320" };

	private static readonly Dictionary<string, string> NumberWords = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
	{
		["ONE"] = "1",
		["TWO"] = "2",
		["THREE"] = "3",
		["FOUR"] = "4",
		["FIVE"] = "5",
		["SIX"] = "6",
		["SEVEN"] = "7",
		["EIGHT"] = "8",
		["NINE"] = "9",
		["TEN"] = "10"
	};

	private static readonly Dictionary<string, string> LanguageAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
	{
		["ENGLISH"] = "EN",
		["FRENCH"] = "FR",
		["ARABIC"] = "AR"
	};

	private static readonly Regex LeadingCountryDecorator = new Regex("^\\s*(?:\\[[A-Za-z]{2,3}\\]|\\([A-Za-z]{2,3}\\)|[A-Z]{2,3})\\s*[|:\\-•]\\s*", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly Regex LeadingCategoryDecorator = new Regex("^\\s*(?:(?:VIP|SPORTS?|LIVE)\\s*[|:\\-•]\\s*)+", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly Regex LeadingCountryCode = new Regex("^\\s*(?:\\[(?<code>[A-Za-z]{2,3})\\]|\\((?<code>[A-Za-z]{2,3})\\)|(?<code>[A-Z]{2,3}))\\s*[|:\\-•]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly Regex TrailingEventDecoration = new Regex("\\s*[\\[(]\\s*(?:LIVE\\s*[- ]?\\s*)?EVENT(?:\\s+ONLY)?\\s*[\\])]\\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

	private static readonly Lazy<IReadOnlyDictionary<string, string>> TerritoryAliasCodes = new Lazy<IReadOnlyDictionary<string, string>>(BuildTerritoryAliasCodes);

	public static IReadOnlyList<string> Tokens(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return Array.Empty<string>();
		}
		string text = TrailingEventDecoration.Replace(StripLeadingDecorators(value), string.Empty);
		StringBuilder stringBuilder = new StringBuilder(text.Length + 8);
		string text2 = text.Normalize(NormalizationForm.FormKC).Normalize(NormalizationForm.FormD);
		for (int i = 0; i < text2.Length; i++)
		{
			char c = FoldModifierLetter(text2[i]);
			if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
			{
				continue;
			}
			if (char.IsLetterOrDigit(c))
			{
				if (stringBuilder.Length > 0)
				{
					if (char.IsLetter(c) != char.IsLetter(stringBuilder[stringBuilder.Length - 1]))
					{
						stringBuilder.Append(' ');
					}
				}
				stringBuilder.Append(char.ToUpperInvariant(c));
			}
			else
			{
				stringBuilder.Append((c == '+') ? " PLUS " : " ");
			}
		}
		string[] array = stringBuilder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		List<string> list = new List<string>(array.Length);
		bool isSpelledLanguageEdition = false;
		for (int j = 0; j < array.Length; j++)
		{
			string text3 = array[j];
			string value2;
			string value3;
			if (IsQuality(array, j, out var consumed))
			{
				j += consumed - 1;
			}
			else if (text3 == "BE" && array.ElementAtOrDefault(j + 1) == "IN")
			{
				list.Add("BEIN");
				isSpelledLanguageEdition = false;
				j++;
			}
			else if (NumberWords.TryGetValue(text3, out value2))
			{
				list.Add(value2);
				isSpelledLanguageEdition = false;
			}
			else if (text3.All(char.IsDigit))
			{
				string text4 = text3.TrimStart('0');
				list.Add((text4.Length == 0) ? "0" : text4);
				isSpelledLanguageEdition = false;
			}
			else if (LanguageAliases.TryGetValue(text3, out value3))
			{
				list.Add(value3);
				isSpelledLanguageEdition = true;
			}
			else
			{
				list.Add((text3 == "SPORT") ? "SPORTS" : text3);
				isSpelledLanguageEdition = false;
			}
		}
		CanonicalizeTrailingLanguageEdition(list, isSpelledLanguageEdition);
		return list;
	}

	public static string Key(string? value)
	{
		return string.Join(' ', Tokens(value));
	}

	public static string KeyWithoutTrailingQualifier(string? value, params string?[] qualifiers)
	{
		List<string> list = Tokens(value).ToList();
		foreach (string item in qualifiers.Where((string value2) => !string.IsNullOrWhiteSpace(value2)))
		{
			string[] array = Tokens(item).ToArray();
			if (array.Length != 0 && list.Count >= array.Length && list.TakeLast(array.Length).SequenceEqual<string>(array, StringComparer.OrdinalIgnoreCase))
			{
				list.RemoveRange(list.Count - array.Length, array.Length);
			}
		}
		return string.Join(' ', list);
	}

	public static string KeyWithoutTrailingTerritory(string? value, params string?[] territories)
	{
		List<string> list = Tokens(value).ToList();
		HashSet<string> territoryCodes = territories.Where((string territory) => TryNormalizeTerritory(territory, out string _)).Select(delegate(string territory)
		{
			TryNormalizeTerritory(territory, out string code);
			return code;
		}).ToHashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string[] item in from suffix in (from pair in TerritoryAliasCodes.Value
				where territoryCodes.Contains(pair.Value)
				select Tokens(pair.Key).ToArray() into suffix
				where suffix.Length != 0
				select suffix).DistinctBy<string[], string>((string[] suffix) => string.Join(' ', suffix), StringComparer.OrdinalIgnoreCase)
			orderby suffix.Length descending
			select suffix)
		{
			if (list.Count >= item.Length && list.TakeLast(item.Length).SequenceEqual<string>(item, StringComparer.OrdinalIgnoreCase))
			{
				list.RemoveRange(list.Count - item.Length, item.Length);
				break;
			}
		}
		return string.Join(' ', list);
	}

	public static bool TryGetLeadingTerritory(string? value, out string code)
	{
		code = string.Empty;
		if (string.IsNullOrWhiteSpace(value))
		{
			return false;
		}
		Match match = LeadingCountryCode.Match(value);
		if (match.Success)
		{
			return TryNormalizeTerritory(match.Groups["code"].Value, out code);
		}
		return false;
	}

	public static bool TryNormalizeTerritory(string? value, out string code)
	{
		string text = value?.Trim().ToUpperInvariant();
		string text2;
		if (text != null)
		{
			int length = text.Length;
			if (length != 2)
			{
				if (length == 3)
				{
					switch (text[0])
					{
					case 'E':
						break;
					case 'S':
						goto IL_00d6;
					case 'W':
						goto IL_00eb;
					case 'N':
						goto IL_0100;
					case 'U':
						goto IL_0125;
					case 'K':
						goto IL_014a;
					case 'D':
						if (text == "DEU")
						{
							goto IL_025f;
						}
						goto IL_02c7;
					case 'G':
						if (text == "GER")
						{
							goto IL_025f;
						}
						goto IL_02c7;
					case 'F':
						if (!(text == "FRA"))
						{
							goto IL_02c7;
						}
						text2 = "FR";
						goto IL_02e2;
					case 'I':
						if (!(text == "ITA"))
						{
							goto IL_02c7;
						}
						text2 = "IT";
						goto IL_02e2;
					case 'P':
						if (!(text == "POR"))
						{
							goto IL_02c7;
						}
						text2 = "PT";
						goto IL_02e2;
					case 'T':
						if (!(text == "TUR"))
						{
							goto IL_02c7;
						}
						text2 = "TR";
						goto IL_02e2;
					case 'B':
						if (!(text == "BRA"))
						{
							goto IL_02c7;
						}
						text2 = "BR";
						goto IL_02e2;
					case 'A':
						goto IL_01f2;
					case 'M':
						if (!(text == "MEX"))
						{
							goto IL_02c7;
						}
						text2 = "MX";
						goto IL_02e2;
					case 'C':
						if (!(text == "CAN"))
						{
							goto IL_02c7;
						}
						text2 = "CA";
						goto IL_02e2;
					default:
						goto IL_02c7;
						IL_025f:
						text2 = "DE";
						goto IL_02e2;
					}
					if (text == "ENG")
					{
						goto IL_023e;
					}
					if (text == "ESP")
					{
						text2 = "ES";
						goto IL_02e2;
					}
				}
			}
			else if (text == "UK")
			{
				goto IL_023e;
			}
		}
		goto IL_02c7;
		IL_02e2:
		code = text2;
		if (code.Length != 2)
		{
			if (!TerritoryAliasCodes.Value.TryGetValue(code, out string value2))
			{
				code = string.Empty;
				return false;
			}
			code = value2;
		}
		try
		{
			new RegionInfo(code);
			return true;
		}
		catch (ArgumentException)
		{
			code = string.Empty;
			return false;
		}
		IL_02c7:
		text2 = value?.Trim().ToUpperInvariant() ?? string.Empty;
		goto IL_02e2;
		IL_00eb:
		if (text == "WAL")
		{
			goto IL_023e;
		}
		goto IL_02c7;
		IL_00d6:
		if (text == "SCO")
		{
			goto IL_023e;
		}
		goto IL_02c7;
		IL_0125:
		if (!(text == "UAE"))
		{
			if (!(text == "USA"))
			{
				goto IL_02c7;
			}
			text2 = "US";
		}
		else
		{
			text2 = "AE";
		}
		goto IL_02e2;
		IL_0100:
		if (text == "NIR")
		{
			goto IL_023e;
		}
		if (!(text == "NED"))
		{
			goto IL_02c7;
		}
		text2 = "NL";
		goto IL_02e2;
		IL_023e:
		text2 = "GB";
		goto IL_02e2;
		IL_01f2:
		if (!(text == "ARG"))
		{
			if (!(text == "AUS"))
			{
				goto IL_02c7;
			}
			text2 = "AU";
		}
		else
		{
			text2 = "AR";
		}
		goto IL_02e2;
		IL_014a:
		if (!(text == "KSA"))
		{
			goto IL_02c7;
		}
		text2 = "SA";
		goto IL_02e2;
	}

	private static IReadOnlyDictionary<string, string> BuildTerritoryAliasCodes()
	{
		Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		CultureInfo[] cultures = CultureInfo.GetCultures(CultureTypes.SpecificCultures);
		foreach (CultureInfo cultureInfo in cultures)
		{
			try
			{
				RegionInfo regionInfo = new RegionInfo(cultureInfo.Name);
				AddTerritoryAlias(dictionary, regionInfo.TwoLetterISORegionName, regionInfo.TwoLetterISORegionName);
				AddTerritoryAlias(dictionary, regionInfo.ThreeLetterISORegionName, regionInfo.TwoLetterISORegionName);
				AddTerritoryAlias(dictionary, regionInfo.EnglishName, regionInfo.TwoLetterISORegionName);
				AddTerritoryAlias(dictionary, regionInfo.NativeName, regionInfo.TwoLetterISORegionName);
				AddTerritoryAlias(dictionary, regionInfo.DisplayName, regionInfo.TwoLetterISORegionName);
			}
			catch (ArgumentException)
			{
			}
		}
		AddTerritoryAlias(dictionary, "UK", "GB");
		AddTerritoryAlias(dictionary, "ENG", "GB");
		AddTerritoryAlias(dictionary, "SCO", "GB");
		AddTerritoryAlias(dictionary, "WAL", "GB");
		AddTerritoryAlias(dictionary, "NIR", "GB");
		AddTerritoryAlias(dictionary, "UAE", "AE");
		AddTerritoryAlias(dictionary, "KSA", "SA");
		return dictionary;
	}

	private static void AddTerritoryAlias(IDictionary<string, string> aliases, string? alias, string code)
	{
		if (!string.IsNullOrWhiteSpace(alias))
		{
			aliases.TryAdd(alias.Trim(), code.ToUpperInvariant());
		}
	}

	private static string StripLeadingDecorators(string value)
	{
		string text = value;
		while (true)
		{
			string input = LeadingCountryDecorator.Replace(text, string.Empty, 1);
			input = LeadingCategoryDecorator.Replace(input, string.Empty, 1);
			if (string.Equals(input, text, StringComparison.Ordinal))
			{
				break;
			}
			text = input;
		}
		return text;
	}

	private static void CanonicalizeTrailingLanguageEdition(List<string> tokens, bool isSpelledLanguageEdition)
	{
		if (!isSpelledLanguageEdition || tokens.Count < 2)
		{
			return;
		}
		if (LanguageAliases.Values.Contains<string>(tokens[tokens.Count - 1], StringComparer.OrdinalIgnoreCase))
		{
			int num = tokens.Count - 1;
			int num2 = tokens.FindLastIndex(num - 1, (string token) => token.All(char.IsDigit));
			if (num2 >= 0)
			{
				string item = tokens[num];
				tokens.RemoveAt(num);
				tokens.Insert(num2, item);
			}
		}
	}

	private static bool IsQuality(IReadOnlyList<string> tokens, int index, out int consumed)
	{
		consumed = 1;
		string text = tokens[index];
		if (ResolutionTokens.Contains(text) && index + 1 < tokens.Count && tokens[index + 1] == "P")
		{
			consumed = 2;
			return true;
		}
		bool flag = ((text == "HEVC" || text == "HDR") ? true : false);
		if (flag && index + 1 < tokens.Count && tokens[index + 1].All(char.IsDigit))
		{
			consumed = 2;
			return true;
		}
		if (QualityTokens.Contains(text))
		{
			return true;
		}
		if (text == "FULL" && index + 1 < tokens.Count && tokens[index + 1] == "HD")
		{
			consumed = 2;
			return true;
		}
		flag = text == "H" && index + 1 < tokens.Count;
		bool flag2;
		if (flag)
		{
			string text2 = tokens[index + 1];
			flag2 = ((text2 == "264" || text2 == "265") ? true : false);
			flag = flag2;
		}
		if (flag)
		{
			consumed = 2;
			return true;
		}
		flag = ((text == "X" || text == "MPEG") ? true : false);
		flag2 = flag && index + 1 < tokens.Count;
		if (flag2)
		{
			bool flag3;
			switch (tokens[index + 1])
			{
			case "264":
			case "265":
			case "2":
			case "4":
				flag3 = true;
				break;
			default:
				flag3 = false;
				break;
			}
			flag2 = flag3;
		}
		if (flag2)
		{
			consumed = 2;
			return true;
		}
		flag2 = ((text == "AV" || text == "VP") ? true : false);
		flag = flag2 && index + 1 < tokens.Count;
		if (flag)
		{
			string text2 = tokens[index + 1];
			bool flag3 = ((text2 == "1" || text2 == "9") ? true : false);
			flag = flag3;
		}
		if (flag)
		{
			consumed = 2;
			return true;
		}
		flag = ((text == "4" || text == "8") ? true : false);
		if (flag && index + 1 < tokens.Count && tokens[index + 1] == "K")
		{
			consumed = 2;
			return true;
		}
		if (text.All(char.IsDigit) && index + 1 < tokens.Count && tokens[index + 1] == "FPS")
		{
			consumed = 2;
			return true;
		}
		return false;
	}

	private static char FoldModifierLetter(char character)
	{
		switch (character)
		{
		case 'ᴬ':
		case 'ᵃ':
			return 'A';
		case 'ᴮ':
		case 'ᵇ':
			return 'B';
		case 'ᴰ':
		case 'ᵈ':
			return 'D';
		case 'ᴱ':
		case 'ᵉ':
			return 'E';
		case 'ᶠ':
			return 'F';
		case 'ᴳ':
		case 'ᵍ':
			return 'G';
		case 'ʰ':
		case 'ᴴ':
			return 'H';
		case 'ᴵ':
		case 'ᶦ':
			return 'I';
		case 'ʲ':
		case 'ᴶ':
			return 'J';
		case 'ᴷ':
		case 'ᵏ':
			return 'K';
		case 'ˡ':
		case 'ᴸ':
			return 'L';
		case 'ᴹ':
		case 'ᵐ':
			return 'M';
		case 'ᴺ':
		case 'ⁿ':
			return 'N';
		case 'ᴼ':
		case 'ᵒ':
			return 'O';
		case 'ᴾ':
		case 'ᵖ':
			return 'P';
		case 'ʳ':
		case 'ᴿ':
			return 'R';
		case 'ˢ':
			return 'S';
		case 'ᵀ':
		case 'ᵗ':
			return 'T';
		case 'ᵁ':
		case 'ᵘ':
			return 'U';
		case 'ⱽ':
			return 'V';
		case 'ʷ':
		case 'ᵂ':
			return 'W';
		case 'ˣ':
			return 'X';
		case 'ʸ':
			return 'Y';
		case 'ᶻ':
			return 'Z';
		default:
			return character;
		}
	}
}
