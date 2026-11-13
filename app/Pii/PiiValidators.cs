using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PhoneNumbers;

namespace PromptFlipper.Pii;

/// <summary>
/// The validation and custom matching methods the recognizer table in PiiRecognizers.g.cs
/// refers to by name.
/// </summary>
internal static class PiiValidators
{
    // EntityRecognizer.sanitize_value with the pairs a recognizer passes.
    private static string Remove(string value, params char[] characters)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (Array.IndexOf(characters, c) < 0) builder.Append(c);
        }
        return builder.ToString();
    }

    // Any Unicode decimal digit counts; any other character fails.
    private static bool TryDigits(string value, out int[] digits)
    {
        digits = new int[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            var digit = CharUnicodeInfo.GetDecimalDigitValue(value[i]);
            if (digit < 0) return false;
            digits[i] = digit;
        }
        return true;
    }

    // Luhn checksum over the digits, separators removed.
    public static bool? ValidateCreditCard(string text)
    {
        if (!TryDigits(Remove(text, '-', ' '), out var digits)) return false;
        var checksum = 0;
        for (int i = digits.Length - 1, position = 0; i >= 0; i--, position++)
        {
            if (position % 2 == 0)
            {
                checksum += digits[i];
            }
            else
            {
                var doubled = digits[i] * 2;
                checksum += doubled / 10 + doubled % 10;
            }
        }
        return checksum % 10 == 0;
    }

    // Bitcoin addresses: Base58Check for 1 and 3 addresses, Bech32 and Bech32m for bc1.
    private const string Base58Digits = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
    private const string Bech32Charset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";
    private const int Bech32mConst = 0x2BC830A3;

    public static bool? ValidateCrypto(string text)
    {
        if (text.StartsWith('1') || text.StartsWith('3'))
        {
            var bytes = DecodeBase58(text);
            if (bytes is null || bytes.Length < 4) return false;
            var checksum = SHA256.HashData(SHA256.HashData(bytes.AsSpan(0, bytes.Length - 4)));
            return bytes.AsSpan(bytes.Length - 4).SequenceEqual(checksum.AsSpan(0, 4));
        }
        if (text.StartsWith("bc1", StringComparison.Ordinal))
        {
            return IsBech32Address(text);
        }
        return false;
    }

    private static byte[]? DecodeBase58(string text)
    {
        var stripped = text.TrimStart('1');
        var n = BigInteger.Zero;
        foreach (var c in stripped)
        {
            var index = Base58Digits.IndexOf(c);
            if (index < 0) return null;
            n = n * 58 + index;
        }
        var magnitude = n.IsZero ? [] : n.ToByteArray(isUnsigned: true, isBigEndian: true);
        var leadingZeros = text.Length - stripped.Length;
        var result = new byte[leadingZeros + magnitude.Length];
        magnitude.CopyTo(result, leadingZeros);
        return result;
    }

    private static int Bech32Polymod(IEnumerable<int> values)
    {
        int[] generator = [0x3B6A57B2, 0x26508E6D, 0x1EA119FA, 0x3D4233DD, 0x2A1462B3];
        var chk = 1;
        foreach (var value in values)
        {
            var top = chk >> 25;
            chk = ((chk & 0x1FFFFFF) << 5) ^ value;
            for (var i = 0; i < 5; i++)
            {
                chk ^= ((top >> i) & 1) != 0 ? generator[i] : 0;
            }
        }
        return chk;
    }

    private static bool IsBech32Address(string bech)
    {
        if (bech.Any(c => c < 33 || c > 126)) return false;
        if (bech.ToLowerInvariant() != bech && bech.ToUpperInvariant() != bech) return false;
        bech = bech.ToLowerInvariant();
        var pos = bech.LastIndexOf('1');
        if (pos < 1 || pos + 7 > bech.Length || bech.Length > 90) return false;
        var data = new List<int>();
        foreach (var c in bech[(pos + 1)..])
        {
            var index = Bech32Charset.IndexOf(c);
            if (index < 0) return false;
            data.Add(index);
        }
        var hrp = bech[..pos];
        var expanded = hrp.Select(c => c >> 5).Append(0).Concat(hrp.Select(c => c & 31)).Concat(data);
        var constant = Bech32Polymod(expanded);
        return constant == 1 || constant == Bech32mConst;
    }

    // The domain must end in a public suffix and have a label before it.
    public static bool? ValidateEmail(string text) => PublicSuffixList.HasRegistrableDomain(text);

    // Mod-97 check digits, then the country format. A format that only fits in upper case keeps the base score.
    public static bool? ValidateIban(string text)
    {
        var iban = Remove(text, '-', ' ');
        if (iban.Length < 4) return false;
        var transformed = (iban[..2] + "00" + iban[4..]).ToUpperInvariant();
        var rearranged = transformed[4..] + transformed[..4];
        var number = new StringBuilder(rearranged.Length * 2);
        foreach (var c in rearranged)
        {
            if (c is >= '0' and <= '9') number.Append(c);
            else if (c is >= 'A' and <= 'Z') number.Append(c - 'A' + 10);
            else return false;
        }
        var remainder = 0;
        foreach (var c in number.ToString())
        {
            remainder = (remainder * 10 + (c - '0')) % 97;
        }
        var checkDigits = (98 - remainder).ToString("00", CultureInfo.InvariantCulture);
        if (checkDigits != iban[2..4]) return false;
        if (IsIbanFormat(iban)) return true;
        if (IsIbanFormat(iban.ToUpperInvariant())) return null;
        return false;
    }

    private static readonly Dictionary<string, Regex> IbanFormatRegexes = new(StringComparer.Ordinal);

    // The country format must match from the start, case-sensitive.
    private static bool IsIbanFormat(string iban)
    {
        if (!PiiRegistry.IbanCountryFormats.TryGetValue(iban[..2], out var pattern)) return false;
        Regex? regex;
        lock (IbanFormatRegexes)
        {
            if (!IbanFormatRegexes.TryGetValue(pattern, out regex))
            {
                regex = new Regex(@"\G(?:" + pattern + ")",
                    RegexOptions.Singleline | RegexOptions.Multiline | RegexOptions.CultureInvariant,
                    PiiRegistry.MatchTimeout);
                IbanFormatRegexes[pattern] = regex;
            }
        }
        try
        {
            return regex.IsMatch(iban);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    // Groups 3, 2 and 1 are tried in turn, and the first
    // that does not validate as false is kept.
    public static IEnumerable<PiiResult> AnalyzeIban(string text, PiiRecognizer recognizer)
    {
        var results = new List<PiiResult>();
        for (var i = 0; i < recognizer.Patterns.Length; i++)
        {
            try
            {
                foreach (Match match in recognizer.Regexes[i].Matches(text))
                {
                    for (var group = match.Groups.Count - 1; group >= 1; group--)
                    {
                        var span = match.Groups[group];
                        var end = span.Success && span.Index + span.Length > 0 ? span.Index + span.Length : match.Index + match.Length;
                        if (end <= match.Index) continue;
                        var score = recognizer.Patterns[i].Score;
                        if (ValidateIban(text[match.Index..end]) is { } valid)
                        {
                            score = valid ? 1.0 : 0;
                        }
                        if (score > 0)
                        {
                            results.Add(new PiiResult(recognizer, match.Index, end, score));
                            break;
                        }
                    }
                }
            }
            catch (RegexMatchTimeoutException)
            {
            }
        }
        return results;
    }

    // Removes a match that is not a valid address with an optional prefix length.
    public static bool InvalidateIp(string text)
    {
        var slash = text.IndexOf('/');
        var address = slash < 0 ? text : text[..slash];
        var prefix = slash < 0 ? null : text[(slash + 1)..];
        int maxPrefix;
        if (address.Contains(':'))
        {
            if (!System.Net.IPAddress.TryParse(address, out var parsed) ||
                parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6 ||
                address.Contains('%'))
            {
                return true;
            }
            maxPrefix = 128;
        }
        else
        {
            var octets = address.Split('.');
            if (octets.Length != 4) return true;
            foreach (var octet in octets)
            {
                // An octet with a leading zero is ambiguous and is rejected.
                if (octet.Length is 0 or > 3 || !octet.All(char.IsAsciiDigit) ||
                    (octet.Length > 1 && octet[0] == '0') || int.Parse(octet, CultureInfo.InvariantCulture) > 255)
                {
                    return true;
                }
            }
            maxPrefix = 32;
        }
        if (prefix is null) return false;
        return prefix.Length == 0 || !prefix.All(char.IsAsciiDigit) || prefix.Length > 3 ||
            int.Parse(prefix, CultureInfo.InvariantCulture) > maxPrefix;
    }

    // Twelve hex digits, not all zeros and not broadcast.
    private static readonly Regex HexTwelve = new("^[0-9A-Fa-f]{12}$", RegexOptions.CultureInvariant);

    public static bool InvalidateMacAddress(string text)
    {
        var cleaned = Remove(text, ':', '-', '.');
        if (!HexTwelve.IsMatch(cleaned) || cleaned.EndsWith('\n')) return true;
        var upper = cleaned.ToUpperInvariant();
        return upper is "FFFFFFFFFFFF" or "000000000000";
    }

    // Numbers valid for each region's numbering plan, at score 0.4.
    private static readonly string[] PhoneRegions = ["US", "GB", "DE", "FR", "IL", "IN", "CA", "BR", "NL"];
    private const double PhoneScore = 0.4;

    public static IEnumerable<PiiResult> AnalyzePhone(string text, PiiRecognizer recognizer)
    {
        var util = PhoneNumberUtil.GetInstance();
        var results = new List<PiiResult>();
        foreach (var region in PhoneRegions)
        {
            foreach (var match in util.FindNumbers(text, region, PhoneNumberUtil.Leniency.VALID, long.MaxValue))
            {
                results.Add(new PiiResult(recognizer, match.Start, match.Start + match.Length, PhoneScore));
            }
        }
        return PiiDetector.RemoveDuplicates(results);
    }

    // Removes the nil UUID and an unknown version or variant.
    public static bool InvalidateUuid(string text)
    {
        if (text.ToLowerInvariant() == "00000000-0000-0000-0000-000000000000") return true;
        var groups = text.Split('-');
        if (groups.Length != 5 || groups.Any(g => g.Length == 0)) return true;
        if (!"12345678".Contains(char.ToLowerInvariant(groups[2][0]))) return true;
        return !"89ab".Contains(char.ToLowerInvariant(groups[3][0]));
    }

    // ABA routing checksum, weights 3, 7, 1.
    public static bool? ValidateAbaRouting(string text)
    {
        if (!TryDigits(Remove(text, '-'), out var digits) || digits.Length < 9) return false;
        int[] weights = [3, 7, 1, 3, 7, 1, 3, 7, 1];
        var sum = 0;
        for (var i = 0; i < weights.Length; i++) sum += digits[i] * weights[i];
        return sum % 10 == 0;
    }

    // DEA number check digit.
    public static bool? ValidateMedicalLicense(string text)
    {
        var sanitized = Remove(text, '-', ' ');
        if (sanitized.Length < 3 || !TryDigits(sanitized[2..], out var digits)) return false;
        var checksum = -digits[^1];
        var body = digits[..^1];
        for (int i = body.Length - 1, position = 0; i >= 0; i--, position++)
        {
            checksum += position % 2 == 0 ? 2 * body[i] : body[i];
        }
        return checksum % 10 == 0;
    }

    // NPI Luhn check with the 80840 prefix; a body of one repeated digit is removed.
    public static bool? ValidateUsNpi(string text)
    {
        if (!TryDigits("80840" + Remove(text, '-', ' '), out var digits)) return false;
        var checksum = 0;
        for (int i = digits.Length - 1, position = 0; i >= 0; i--, position++)
        {
            if (position % 2 == 1)
            {
                var doubled = digits[i] * 2;
                checksum += doubled > 9 ? doubled - 9 : doubled;
            }
            else
            {
                checksum += digits[i];
            }
        }
        return checksum % 10 == 0;
    }

    public static bool InvalidateUsNpi(string text)
    {
        var sanitized = Remove(text, '-', ' ');
        if (sanitized.Length == 0) return false;
        var body = sanitized.Length > 1 ? sanitized[..^1] : sanitized;
        return body.Length > 0 && body.Distinct().Count() == 1;
    }

    // Removes mixed separators, repeated digits, zero groups, unissued areas and published samples.
    public static bool InvalidateUsSsn(string text)
    {
        if (text.Where(c => c is '.' or '-' or ' ').Distinct().Count() > 1) return true;
        var digits = new string(text.Where(char.IsDigit).ToArray());
        if (digits.Length == 0 || digits.All(c => c == digits[0])) return true;
        if (Slice(digits, 3, 5) == "00" || Slice(digits, 5, digits.Length) == "0000") return true;
        if (Slice(digits, 0, 3) is "000" or "666") return true;
        return digits is "123456789" or "987654320" or "078051120";
    }

    // Out-of-range bounds are clamped.
    private static string Slice(string value, int start, int end)
    {
        start = Math.Min(start, value.Length);
        end = Math.Min(end, value.Length);
        return end > start ? value[start..end] : "";
    }

    // The surname part needs letters, padded with 9 only at the end.
    private static readonly Regex SurnamePortion = new("^[A-Z]+9*$", RegexOptions.CultureInvariant);

    public static bool? ValidateUkDrivingLicence(string text)
    {
        var upper = text.ToUpperInvariant();
        var surname = Slice(upper, 0, 5);
        if (surname == "99999") return false;
        if (!SurnamePortion.IsMatch(surname)) return false;
        return null;
    }

    // NHS mod-11 check, weights 10 down to 1.
    public static bool? ValidateNhs(string text)
    {
        if (!TryDigits(Remove(text, '-', ' '), out var digits)) return false;
        var total = 0;
        for (var i = 0; i < Math.Min(digits.Length, 11); i++) total += digits[i] * (10 - i);
        return total % 11 == 0;
    }

    // A current-format plate needs a valid age identifier.
    public static bool? ValidateUkVehicleRegistration(string text)
    {
        var sanitized = Remove(text, '-', ' ');
        if (sanitized.Length == 7 && sanitized[..2].All(char.IsLetter))
        {
            var age = sanitized[2..4];
            if (age.All(char.IsDigit) && TryDigits(age, out var digits))
            {
                var value = digits[0] * 10 + digits[1];
                return value is >= 2 and <= 29 or >= 51 and <= 79;
            }
        }
        return null;
    }
}

/// <summary>
/// The ICANN section of the Public Suffix List, matched right to left with wildcard and
/// exception rules. An email domain counts when it ends in a public suffix and has a label
/// before it. Private-domain suffixes are not loaded.
/// </summary>
internal static class PublicSuffixList
{
    private sealed class Node
    {
        public Dictionary<string, Node> Matches { get; } = new(StringComparer.Ordinal);

        public bool End { get; set; }
    }

    private static Node? root;

    private static Node Root => root ??= Load();

    private static readonly Regex Ipv4 = new(
        @"^(?:(?:[0-9]|[1-9][0-9]|1[0-9]{2}|2[0-4][0-9]|25[0-5])\.){3}(?:[0-9]|[1-9][0-9]|1[0-9]{2}|2[0-4][0-9]|25[0-5])$",
        RegexOptions.CultureInvariant);

    private static Node Load()
    {
        var node = new Node();
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Pii.public-suffix-icann.txt")
            ?? throw new InvalidOperationException("The Public Suffix List resource is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            var rule = line.Trim();
            if (rule.Length == 0 || rule.StartsWith("//", StringComparison.Ordinal)) continue;
            var current = node;
            foreach (var label in rule.Split('.').Reverse())
            {
                if (!current.Matches.TryGetValue(label, out var next))
                {
                    next = new Node();
                    current.Matches[label] = next;
                }
                current = next;
            }
            current.End = true;
        }
        return node;
    }

    public static bool HasRegistrableDomain(string text)
    {
        var host = Host(text)
            .Replace('。', '.').Replace('．', '.').Replace('｡', '.');
        if (host.Length >= 4 && host[0] == '[' && host[^1] == ']') return false;
        var labels = host.Split('.');
        var suffixIndex = SuffixIndex(labels);
        if (suffixIndex is null) return false;
        // fqdn is not empty when there is a suffix and a domain label before it.
        return suffixIndex.Value > 0 && suffixIndex.Value < labels.Length;
    }

    // The host part of a URL-like string: after any scheme and the last @, before any port.
    private static string Host(string url)
    {
        var doubleSlash = url.IndexOf("//", StringComparison.Ordinal);
        if (doubleSlash == 0)
        {
            url = url[2..];
        }
        else if (doubleSlash >= 2 && url[doubleSlash - 1] == ':' &&
            url[..(doubleSlash - 1)].All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.'))
        {
            url = url[(doubleSlash + 2)..];
        }
        var authority = url.Split('/')[0].Split('?')[0].Split('#')[0];
        var at = authority.LastIndexOf('@');
        var afterUserInfo = at < 0 ? authority : authority[(at + 1)..];
        if (afterUserInfo.Length > 0 && afterUserInfo[0] == '[')
        {
            var close = afterUserInfo.IndexOf(']');
            if (close >= 0) return afterUserInfo[..(close + 1)];
        }
        var hostname = afterUserInfo.Split(':')[0].Trim();
        return hostname.TrimEnd('.', '。', '．', '｡');
    }

    private static int? SuffixIndex(string[] labels)
    {
        var node = Root;
        var suffixIndex = labels.Length;
        var labelIndex = labels.Length;
        for (var i = labels.Length - 1; i >= 0; i--)
        {
            var decoded = DecodePunycode(labels[i]);
            if (node.Matches.TryGetValue(decoded, out var next))
            {
                labelIndex--;
                node = next;
                if (node.End) suffixIndex = labelIndex;
                continue;
            }
            if (node.Matches.ContainsKey("*"))
            {
                return node.Matches.ContainsKey("!" + decoded) ? labelIndex : labelIndex - 1;
            }
            break;
        }
        return suffixIndex == labels.Length ? null : suffixIndex;
    }

    private static string DecodePunycode(string label)
    {
        var lowered = label.ToLowerInvariant();
        if (lowered.StartsWith("xn--", StringComparison.Ordinal))
        {
            try
            {
                return new IdnMapping().GetUnicode(lowered);
            }
            catch (ArgumentException)
            {
            }
        }
        return lowered;
    }
}
