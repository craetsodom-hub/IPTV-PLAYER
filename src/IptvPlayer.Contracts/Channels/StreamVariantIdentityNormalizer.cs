using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace IptvPlayer.Contracts.Channels;

public static class StreamVariantIdentityNormalizer
{
	private sealed record QualityPart(string Kind, string Label, int DisplayOrder, int SortRank);

	private static readonly HashSet<string> LocaleTokens = new HashSet<string>(StringComparer.Ordinal)
	{
		"AF", "ALB", "AM", "AR", "ARA", "ARABIC", "AZ", "BE", "BG", "BN",
		"BOS", "BS", "CA", "CAT", "CS", "CY", "CZ", "DA", "DAN", "DE",
		"DEU", "DUTCH", "EL", "EN", "ENG", "ENGLISH", "ES", "ESP", "ET", "EU",
		"FA", "FI", "FIN", "FR", "FRA", "FRENCH", "GA", "GE", "GER", "GL",
		"GR", "GU", "HE", "HI", "HR", "HU", "HY", "ID", "IE", "IS",
		"IT", "ITA", "ITALIAN", "JA", "JP", "KA", "KK", "KN", "KO", "KOR",
		"LT", "LV", "MK", "ML", "MN", "MR", "MS", "MT", "NL", "NO",
		"NOR", "PA", "PL", "POL", "PORTUGUESE", "PT", "RO", "ROM", "RU", "RUS",
		"RUSSIAN", "SK", "SL", "SQ", "SR", "SV", "SW", "TA", "TE", "TH",
		"TR", "TUR", "UK", "UKR", "UR", "UZ", "VI", "ZH", "AE", "AT",
		"AU", "BA", "BD", "BH", "BO", "BR", "CA", "CH", "CL", "CM",
		"CN", "CO", "CR", "CY", "DE", "DK", "DO", "DZ", "EC", "EE",
		"EG", "ES", "ET", "EU", "FI", "FR", "GB", "GH", "GR", "HK",
		"HR", "HU", "ID", "IE", "IL", "IN", "IQ", "IR", "IS", "IT",
		"JO", "JP", "KE", "KH", "KR", "KW", "LA", "LB", "LK", "LT",
		"LV", "MA", "MENA", "MK", "MM", "MX", "MY", "NG", "NL", "NO",
		"NP", "NZ", "OM", "PA", "PE", "PH", "PK", "PL", "PR", "PT",
		"PY", "QA", "RO", "RS", "RU", "SA", "SE", "SG", "SI", "SK",
		"SN", "SY", "TH", "TN", "TR", "TW", "TZ", "UA", "UG", "UK",
		"US", "USA", "UY", "VE", "VN", "ZA", "AFRICA", "APAC", "ASIA", "CANADA",
		"EMEA", "EUROPE", "LATAM"
	};

	public static StreamVariantIdentity Normalize(string? channelName)
	{
		List<string> list = Tokenize(channelName);
		string text = ResolveVerifiedLocaleQualifier(channelName, list);
		bool hasExplicitLocaleQualifier = !string.IsNullOrWhiteSpace(text);
		List<string> list2 = new List<string>(list.Count);
		Dictionary<string, QualityPart> dictionary = new Dictionary<string, QualityPart>(StringComparer.Ordinal);
		int num = 0;
		while (num < list.Count)
		{
			if (TryReadQuality(list, num, out int consumed, out QualityPart qualityPart))
			{
				if (!dictionary.ContainsKey(qualityPart.Kind))
				{
					dictionary.Add(qualityPart.Kind, qualityPart);
				}
				num += consumed;
			}
			else
			{
				list2.Add(NormalizeIdentityToken(list[num]));
				num++;
			}
		}
		QualityPart[] array = dictionary.Values.OrderBy((QualityPart part) => part.DisplayOrder).ThenBy<QualityPart, string>((QualityPart part) => part.Label, StringComparer.Ordinal).ToArray();
		string qualityLabel = ((array.Length == 0) ? "SOURCE" : string.Join(" · ", array.Select((QualityPart part) => part.Label)));
		int qualitySortRank = ((array.Length != 0) ? array.Max((QualityPart part) => part.SortRank) : 0);
		return new StreamVariantIdentity(string.Join(' ', list2), qualityLabel, qualitySortRank, hasExplicitLocaleQualifier, text);
	}

	public static bool CanGroup(StreamVariantIdentity selected, StreamVariantIdentity candidate)
	{
		if (selected.HasExplicitLocaleQualifier && candidate.HasExplicitLocaleQualifier && selected.Key.Length > 0)
		{
			return string.Equals(selected.Key, candidate.Key, StringComparison.Ordinal);
		}
		return false;
	}

	private static List<string> Tokenize(string? value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return new List<string>();
		}
		StringBuilder stringBuilder = new StringBuilder(value.Length + 8);
		AppendFlagCountryCodes(value, stringBuilder);
		string text = value.Normalize(NormalizationForm.FormD);
		foreach (char c in text)
		{
			if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
			{
				continue;
			}
			if (char.IsLetter(c))
			{
				stringBuilder.Append(char.ToUpperInvariant(c));
			}
			else if (char.IsDigit(c))
			{
				if (stringBuilder.Length > 0)
				{
					if (char.IsLetter(stringBuilder[stringBuilder.Length - 1]))
					{
						stringBuilder.Append(' ');
					}
				}
				stringBuilder.Append(c);
			}
			else if (c == '+')
			{
				stringBuilder.Append(" PLUS ");
			}
			else
			{
				stringBuilder.Append(' ');
			}
			if (char.IsLetter(c) && stringBuilder.Length > 1)
			{
				if (char.IsDigit(stringBuilder[stringBuilder.Length - 2]))
				{
					stringBuilder.Insert(stringBuilder.Length - 1, ' ');
				}
			}
		}
		return stringBuilder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
	}

	private static void AppendFlagCountryCodes(string value, StringBuilder builder)
	{
		char[] array = (from rune in value.EnumerateRunes().Where(delegate(Rune rune)
			{
				int value2 = rune.Value;
				return value2 >= 127462 && value2 <= 127487;
			})
			select (char)(65 + rune.Value - 127462)).ToArray();
		for (int num = 0; num + 1 < array.Length; num += 2)
		{
			builder.Append(array[num]);
			builder.Append(array[num + 1]);
			builder.Append(' ');
		}
	}

	private static string? ResolveVerifiedLocaleQualifier(string? channelName, IReadOnlyList<string> tokens)
	{
		if (string.IsNullOrWhiteSpace(channelName))
		{
			return null;
		}
		HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
		int num = channelName.IndexOf('|');
		if (num > 0)
		{
			List<string> list = Tokenize(channelName.Substring(0, num));
			if (list.Count == 1 && LocaleTokens.Contains(list[0]))
			{
				hashSet.Add(list[0]);
			}
		}
		string text = channelName.TrimStart();
		bool flag = text.Length > 2;
		if (flag)
		{
			char c = text[0];
			bool flag2 = ((c == '(' || c == '[') ? true : false);
			flag = flag2;
		}
		if (flag)
		{
			int num2 = text.IndexOf((text[0] == '[') ? ']' : ')');
			if (num2 > 1)
			{
				List<string> list2 = Tokenize(text.Substring(1, num2 - 1));
				if (list2.Count == 1 && LocaleTokens.Contains(list2[0]))
				{
					hashSet.Add(list2[0]);
				}
			}
		}
		foreach (string item in ExtractFlagCountryCodes(channelName))
		{
			if (LocaleTokens.Contains(item))
			{
				hashSet.Add(item);
			}
		}
		foreach (string token in tokens)
		{
			string text2 = token switch
			{
				"ARABIC" => "AR", 
				"ENGLISH" => "EN", 
				"FRENCH" => "FR", 
				"ITALIAN" => "IT", 
				"PORTUGUESE" => "PT", 
				"RUSSIAN" => "RU", 
				_ => null, 
			};
			if (text2 != null)
			{
				hashSet.Add(text2);
			}
		}
		for (int i = 1; i < tokens.Count; i++)
		{
			if (TryReadQuality(tokens, i, out int _, out QualityPart _))
			{
				int num3 = i - 1;
				while (num3 >= 0 && LocaleTokens.Contains(tokens[num3]))
				{
					hashSet.Add(tokens[num3]);
					num3--;
				}
			}
		}
		if (hashSet.Count != 1)
		{
			return null;
		}
		return hashSet.Single();
	}

	private static IReadOnlyList<string> ExtractFlagCountryCodes(string value)
	{
		char[] array = (from rune in value.EnumerateRunes().Where(delegate(Rune rune)
			{
				int value2 = rune.Value;
				return value2 >= 127462 && value2 <= 127487;
			})
			select (char)(65 + rune.Value - 127462)).ToArray();
		List<string> list = new List<string>();
		for (int num = 0; num + 1 < array.Length; num += 2)
		{
			list.Add(string.Concat(array[num], array[num + 1]));
		}
		return list;
	}

	private static string NormalizeIdentityToken(string token)
	{
		if (!token.All(char.IsDigit))
		{
			return token;
		}
		string text = token.TrimStart('0');
		if (text.Length != 0)
		{
			return text;
		}
		return "0";
	}

	private static bool TryReadQuality(IReadOnlyList<string> tokens, int index, out int consumed, out QualityPart qualityPart)
	{
		consumed = 1;
		string text = tokens[index];
		bool flag;
		switch (text)
		{
		case "FULL":
		case "ULTRA":
		case "STANDARD":
		case "HIGH":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		bool flag2 = flag;
		if (flag2)
		{
			int index2 = index + 1;
			bool flag3 = ((text == "STANDARD" || text == "HIGH") ? true : false);
			flag2 = HasToken(tokens, index2, flag3 ? "DEFINITION" : "HD");
		}
		if (flag2)
		{
			consumed = 2;
			qualityPart = text switch
			{
				"FULL" => Resolution("FHD", 300), 
				"ULTRA" => Resolution("UHD", 400), 
				"HIGH" => Resolution("HD", 200), 
				_ => Resolution("SD", 100), 
			};
			return true;
		}
		flag2 = ((text == "4" || text == "8") ? true : false);
		if (flag2 && HasToken(tokens, index + 1, "K"))
		{
			consumed = 2;
			qualityPart = Resolution(text + "K", (text == "8") ? 500 : 400);
			return true;
		}
		if (text != null)
		{
			int index2 = text.Length;
			if (index2 != 3)
			{
				if (index2 == 4)
				{
					switch (text[1])
					{
					case '0':
						break;
					case '4':
						goto IL_0217;
					case '1':
						goto IL_0226;
					case '3':
						goto IL_0235;
					default:
						goto IL_0246;
					}
					if (text == "1080")
					{
						goto IL_0242;
					}
				}
			}
			else
			{
				switch (text[0])
				{
				case '4':
					break;
				case '5':
					goto IL_01ea;
				case '7':
					goto IL_01f9;
				default:
					goto IL_0246;
				}
				if (text == "480")
				{
					goto IL_0242;
				}
			}
		}
		goto IL_0246;
		IL_04e2:
		if (flag2 && HasToken(tokens, index + 1, "FPS"))
		{
			consumed = 2;
			qualityPart = new QualityPart("FPS", text + " FPS", 40, 20);
			return true;
		}
		if (text == "HDR" && HasToken(tokens, index + 1, "10"))
		{
			consumed = 2;
			qualityPart = new QualityPart("HDR", "HDR10", 30, 15);
			return true;
		}
		switch (text)
		{
		case "AC":
		case "EAC":
		case "VP":
		case "AV":
		case "MPEG":
			flag2 = true;
			break;
		default:
			flag2 = false;
			break;
		}
		flag = flag2 && index + 1 < tokens.Count;
		if (flag)
		{
			bool flag3;
			switch (tokens[index + 1])
			{
			case "1":
			case "2":
			case "3":
			case "4":
			case "9":
				flag3 = true;
				break;
			default:
				flag3 = false;
				break;
			}
			flag = flag3;
		}
		if (flag)
		{
			consumed = 2;
			QualityPart qualityPart2;
			switch (text)
			{
			case "AC":
				if (tokens[index + 1] == "3")
				{
					qualityPart2 = AudioCodec("AC-3");
					break;
				}
				goto default;
			case "EAC":
				if (tokens[index + 1] == "3")
				{
					qualityPart2 = AudioCodec("E-AC-3");
					break;
				}
				goto default;
			case "VP":
				if (tokens[index + 1] == "9")
				{
					qualityPart2 = Codec("VP9");
					break;
				}
				goto default;
			case "AV":
				if (tokens[index + 1] == "1")
				{
					qualityPart2 = Codec("AV1");
					break;
				}
				goto default;
			case "MPEG":
			{
				string text2 = tokens[index + 1];
				if ((text2 == "2" || text2 == "4") ? true : false)
				{
					qualityPart2 = Codec("MPEG-" + tokens[index + 1]);
					break;
				}
				goto default;
			}
			default:
				qualityPart2 = null;
				break;
			}
			qualityPart = qualityPart2;
			if ((object)qualityPart != null)
			{
				return true;
			}
			consumed = 1;
		}
		if (text == "HEVC" && HasToken(tokens, index + 1, "10"))
		{
			consumed = 2;
			qualityPart = Codec("HEVC 10-BIT");
			return true;
		}
		switch (text)
		{
		case "8":
		case "10":
		case "12":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		if (flag && HasToken(tokens, index + 1, "BIT"))
		{
			consumed = 2;
			qualityPart = new QualityPart("BIT-DEPTH", text + "-BIT", 35, 12);
			return true;
		}
		QualityPart qualityPart3 = text switch
		{
			"SD" => Resolution("SD", 100), 
			"HD" => Resolution(HasToken(tokens, index + 1, "PLUS") ? "HD+" : "HD", 200), 
			"FHD" => Resolution(HasToken(tokens, index + 1, "PLUS") ? "FHD+" : "FHD", 300), 
			"UHD" => Resolution(HasToken(tokens, index + 1, "PLUS") ? "UHD+" : "UHD", 400), 
			"HQ" => Resolution("HQ", 180), 
			"LQ" => Resolution("LQ", 80), 
			"HEVC" => Codec("HEVC"), 
			"AVC" => Codec("AVC"), 
			"VVC" => Codec("VVC"), 
			"MPEG" => Codec("MPEG"), 
			"MPEG2" => Codec("MPEG-2"), 
			"MPEG4" => Codec("MPEG-4"), 
			"VP9" => Codec("VP9"), 
			"AV1" => Codec("AV1"), 
			"AAC" => AudioCodec("AAC"), 
			"AC3" => AudioCodec("AC-3"), 
			"EAC3" => AudioCodec("E-AC-3"), 
			"HDR" => new QualityPart("HDR", "HDR", 30, 15), 
			"HDR10" => new QualityPart("HDR", "HDR10", 30, 15), 
			"HLG" => new QualityPart("HDR", "HLG", 30, 15), 
			"DV" => new QualityPart("HDR", "DOLBY VISION", 30, 15), 
			"RAW" => new QualityPart("RAW", "RAW", 5, 5), 
			_ => null, 
		};
		if ((object)qualityPart3 == null)
		{
			qualityPart = null;
			return false;
		}
		qualityPart = qualityPart3;
		switch (text)
		{
		case "HD":
		case "FHD":
		case "UHD":
			flag = true;
			break;
		default:
			flag = false;
			break;
		}
		if (flag && HasToken(tokens, index + 1, "PLUS"))
		{
			consumed = 2;
		}
		return true;
		IL_04a2:
		if (text == "50")
		{
			goto IL_04dc;
		}
		goto IL_04e0;
		IL_0242:
		flag2 = true;
		goto IL_0248;
		IL_0217:
		if (text == "1440")
		{
			goto IL_0242;
		}
		goto IL_0246;
		IL_0493:
		if (text == "30")
		{
			goto IL_04dc;
		}
		goto IL_04e0;
		IL_04b1:
		if (text == "60")
		{
			goto IL_04dc;
		}
		goto IL_04e0;
		IL_0246:
		flag2 = false;
		goto IL_0248;
		IL_0248:
		flag = flag2 && index + 1 < tokens.Count;
		if (flag)
		{
			string text2 = tokens[index + 1];
			bool flag3 = ((text2 == "P" || text2 == "I") ? true : false);
			flag = flag3;
		}
		if (flag)
		{
			consumed = 2;
			string text3 = tokens[index + 1];
			int num = int.Parse(text, CultureInfo.InvariantCulture);
			int index2 = ((num >= 1080) ? ((num >= 4320) ? 500 : ((num < 2160) ? 300 : 400)) : ((num < 720) ? 100 : 200));
			int rank = index2;
			qualityPart = Resolution(text + text3, rank);
			return true;
		}
		flag = ((text == "H" || text == "X") ? true : false);
		flag2 = flag && index + 1 < tokens.Count;
		if (flag2)
		{
			bool flag3;
			switch (tokens[index + 1])
			{
			case "264":
			case "265":
			case "266":
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
			string text4 = tokens[index + 1];
			string text2 = ((text4 == "265") ? "HEVC" : ((!(text4 == "266")) ? "H.264" : "VVC"));
			string text5 = text2;
			if (index + 2 < tokens.Count && tokens[index + 2] == "10")
			{
				consumed = 3;
				text5 += " 10-BIT";
			}
			qualityPart = Codec(text5);
			return true;
		}
		if (text != null)
		{
			int index2 = text.Length;
			if (index2 != 2)
			{
				if (index2 == 3)
				{
					char c = text[1];
					if (c != '0')
					{
						if (c == '2' && text == "120")
						{
							goto IL_04dc;
						}
					}
					else if (text == "100")
					{
						goto IL_04dc;
					}
				}
			}
			else
			{
				switch (text[0])
				{
				case '2':
					break;
				case '3':
					goto IL_0493;
				case '5':
					goto IL_04a2;
				case '6':
					goto IL_04b1;
				default:
					goto IL_04e0;
				}
				if (text == "24" || text == "25")
				{
					goto IL_04dc;
				}
			}
		}
		goto IL_04e0;
		IL_01f9:
		if (text == "720")
		{
			goto IL_0242;
		}
		goto IL_0246;
		IL_01ea:
		if (text == "576")
		{
			goto IL_0242;
		}
		goto IL_0246;
		IL_04e0:
		flag2 = false;
		goto IL_04e2;
		IL_0226:
		if (text == "2160")
		{
			goto IL_0242;
		}
		goto IL_0246;
		IL_04dc:
		flag2 = true;
		goto IL_04e2;
		IL_0235:
		if (text == "4320")
		{
			goto IL_0242;
		}
		goto IL_0246;
	}

	private static bool HasToken(IReadOnlyList<string> tokens, int index, string token)
	{
		if (index < tokens.Count)
		{
			return string.Equals(tokens[index], token, StringComparison.Ordinal);
		}
		return false;
	}

	private static QualityPart Resolution(string label, int rank)
	{
		return new QualityPart("RESOLUTION", label, 10, rank);
	}

	private static QualityPart Codec(string label)
	{
		return new QualityPart("VIDEO-CODEC", label, 20, 25);
	}

	private static QualityPart AudioCodec(string label)
	{
		return new QualityPart("AUDIO-CODEC", label, 25, 10);
	}
}
