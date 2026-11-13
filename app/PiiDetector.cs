using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace PromptFlipper;

/// <summary>One piece of text that matched a PII category, with the matched text for review.</summary>
public sealed record PiiFinding(string Category, string Value);

/// <summary>
/// Pattern-based detection of structured personal details. Each recognizer has a pattern and,
/// where the format has one, a checksum that rules out look-alike numbers. Recognizers whose
/// format is too common on its own report a match only when a context word is near it. Names
/// and street addresses are not detected: recognizing them needs a language model.
/// </summary>
public static partial class PiiDetector
{
    public const string Email = "Email address";
    public const string Phone = "Phone number";
    public const string Iban = "IBAN";
    public const string Card = "Card number";
    public const string IpAddress = "IP address";
    public const string Bsn = "NL BSN";
    public const string Ssn = "US Social Security number";
    public const string Nino = "UK National Insurance number";
    public const string Nhs = "UK NHS number";
    public const string UkPostcode = "UK postcode";
    public const string NlPostcode = "NL postcode";

    public const string CoverageStatement =
        "Finds: email, phone, IBAN, card, IP, postcode, BSN, SSN, NI, NHS. Not names or addresses.";

    private const int MatchTimeoutMs = 500;

    // How far back from a match the context words are looked for, in words and in characters.
    private const int ContextWords = 5;
    private const int ContextScanLimit = 512;

    private static readonly string[] PostcodeContext =
        ["postcode", "zip", "address", "adres", "road", "street", "straat", "lane", "avenue", "laan", "weg", "plein"];

    // Measure returns how much of the match is accepted, or 0 to reject it. Without one the whole
    // match is accepted.
    private sealed record Recognizer(
        string Category, Regex Pattern, Func<string, int>? Measure = null, string[]? Context = null);

    private static Func<string, int> Whole(Func<string, bool> isValid) => value => isValid(value) ? value.Length : 0;

    // Listed in priority order: when two matches overlap, the one listed first is kept.
    private static readonly Recognizer[] Recognizers =
    [
        new(Iban, IbanPattern(), MeasureIban),
        new(Card, CardPattern(), Whole(PassesLuhn)),
        new(Email, EmailPattern()),
        new(Nhs, NhsPattern(), Whole(IsValidNhs), ["nhs"]),
        new(Bsn, BsnPattern(), Whole(IsValidBsn)),
        new(Ssn, SsnPattern()),
        new(Nino, NinoPattern()),
        new(IpAddress, Ipv4Pattern()),
        new(IpAddress, Ipv6Pattern(), Whole(IsIpv6)),
        new(Phone, NanpPhonePattern(), Whole(HasPhoneDigitCount)),
        new(Phone, InternationalPhonePattern(), Whole(HasPhoneDigitCount)),
        new(Phone, NationalPhonePattern(), Whole(HasPhoneDigitCount)),
        new(UkPostcode, UkPostcodePattern(), Context: PostcodeContext),
        new(NlPostcode, NlPostcodePattern(), Context: PostcodeContext),
    ];

    public static IReadOnlyList<string> CoveredCategories { get; } =
        Recognizers.Select(recognizer => recognizer.Category).Distinct().ToArray();

    public static IReadOnlyList<PiiFinding> Detect(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<PiiFinding>();
        }

        var accepted = new List<(int Start, int End, string Category)>();
        foreach (var recognizer in Recognizers)
        {
            try
            {
                foreach (Match match in recognizer.Pattern.Matches(text))
                {
                    var start = match.Index;
                    var length = recognizer.Measure is { } measure ? measure(match.Value) : match.Length;
                    if (length == 0) continue;
                    var end = start + length;
                    if (recognizer.Context is { } context && !HasContextWord(text, start, context)) continue;
                    if (accepted.Any(span => start < span.End && span.Start < end)) continue;
                    accepted.Add((start, end, recognizer.Category));
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // A pattern that times out is skipped; the results of the others stand.
            }
        }

        return accepted
            .OrderBy(span => span.Start)
            .Select(span => new PiiFinding(span.Category, text[span.Start..span.End]))
            .ToList();
    }

    // Greedy: the country's length is cut from the front of the match in MeasureIban.
    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Z]{2}\d{2}(?: ?[A-Z0-9]){11,30}", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex IbanPattern();

    [GeneratedRegex(@"(?<!\d)\d(?:[ -]?\d){12,18}(?!\d)", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex CardPattern();

    [GeneratedRegex(@"(?<![\w.%+-])[\w.%+-]+@[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?)*\.[A-Za-z]{2,}(?![\w-])", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"(?<!\d)\d{3}[ -]?\d{3}[ -]?\d{4}(?!\d)", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex NhsPattern();

    [GeneratedRegex(@"(?<![\d.])(?:\d{9}|\d{4}\.\d{2}\.\d{3})(?![\d.]\d|\d)", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex BsnPattern();

    [GeneratedRegex(@"(?<!\d)(?!000|666|9\d\d)\d{3}-(?!00)\d{2}-(?!0000)\d{4}(?!\d)", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex SsnPattern();

    // HMRC: no D, F, I, Q, U or V as a prefix letter, no O as the second, and the prefixes
    // BG, GB, KN, NK, NT, TN and ZZ are not allocated.
    [GeneratedRegex(@"(?<![A-Za-z0-9])(?!BG|GB|KN|NK|NT|TN|ZZ)[A-CEGHJ-PR-TW-Z][A-CEGHJ-NPR-TW-Z] ?\d{2} ?\d{2} ?\d{2} ?[A-D](?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeoutMs)]
    private static partial Regex NinoPattern();

    [GeneratedRegex(@"(?<![\d.])(?:(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(?!\d|\.\d)", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex Ipv4Pattern();

    [GeneratedRegex(@"(?<![\w:.])[0-9A-Fa-f]{0,4}(?::[0-9A-Fa-f]{0,4}){2,7}(?![\w:])", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex Ipv6Pattern();

    [GeneratedRegex(@"(?<![\d+])(?:\+1[ .-]?)?(?:\(\d{3}\)|\d{3})[ .-]?\d{3}[ .-]?\d{4}(?!\d)", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex NanpPhonePattern();

    [GeneratedRegex(@"(?<![\w+])\+\d{1,3}(?:[ .-]?\(0\))?(?:[ .-]?\d){6,13}(?!\d)", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex InternationalPhonePattern();

    // National numbers with a trunk 0: 10 digits in the Netherlands, 11 in the UK.
    [GeneratedRegex(@"(?<![\w+])0\d(?:[ .-]?\d){8,9}(?!\d)", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex NationalPhonePattern();

    [GeneratedRegex(@"(?<![A-Za-z0-9])[A-Z]{1,2}\d[A-Z\d]? ?\d[A-Z]{2}(?![A-Za-z0-9])", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex UkPostcodePattern();

    // PostNL: four digits without a leading zero, then two letters other than SA, SD or SS.
    [GeneratedRegex(@"(?<![A-Za-z0-9])[1-9]\d{3} ?(?!SA|SD|SS)[A-Z]{2}(?![A-Za-z0-9])", RegexOptions.None, MatchTimeoutMs)]
    private static partial Regex NlPostcodePattern();

    // Total IBAN length per country, from the SWIFT IBAN registry.
    private static readonly Dictionary<string, int> IbanLengths = new(StringComparer.Ordinal)
    {
        ["AD"] = 24, ["AE"] = 23, ["AL"] = 28, ["AT"] = 20, ["AZ"] = 28, ["BA"] = 20, ["BE"] = 16,
        ["BG"] = 22, ["BH"] = 22, ["BR"] = 29, ["BY"] = 28, ["CH"] = 21, ["CR"] = 22, ["CY"] = 28,
        ["CZ"] = 24, ["DE"] = 22, ["DK"] = 18, ["DO"] = 28, ["EE"] = 20, ["EG"] = 29, ["ES"] = 24,
        ["FI"] = 18, ["FO"] = 18, ["FR"] = 27, ["GB"] = 22, ["GE"] = 22, ["GI"] = 23, ["GL"] = 18,
        ["GR"] = 27, ["GT"] = 28, ["HR"] = 21, ["HU"] = 28, ["IE"] = 22, ["IL"] = 23, ["IQ"] = 23,
        ["IS"] = 26, ["IT"] = 27, ["JO"] = 30, ["KW"] = 30, ["KZ"] = 20, ["LB"] = 28, ["LC"] = 32,
        ["LI"] = 21, ["LT"] = 20, ["LU"] = 20, ["LV"] = 21, ["MC"] = 27, ["MD"] = 24, ["ME"] = 22,
        ["MK"] = 19, ["MR"] = 27, ["MT"] = 31, ["MU"] = 30, ["NL"] = 18, ["NO"] = 15, ["PK"] = 24,
        ["PL"] = 28, ["PS"] = 29, ["PT"] = 25, ["QA"] = 29, ["RO"] = 24, ["RS"] = 22, ["SA"] = 24,
        ["SC"] = 31, ["SE"] = 24, ["SI"] = 19, ["SK"] = 24, ["SM"] = 27, ["ST"] = 25, ["SV"] = 28,
        ["TL"] = 23, ["TN"] = 24, ["TR"] = 26, ["UA"] = 29, ["VA"] = 22, ["VG"] = 24, ["XK"] = 20
    };

    // Takes the country's length of characters from the front of the match, which must not run
    // on into a further letter or digit, then applies the ISO 13616 mod-97 check.
    private static int MeasureIban(string value)
    {
        if (!IbanLengths.TryGetValue(value[..2], out var expected)) return 0;
        var count = 0;
        var end = 0;
        while (end < value.Length && count < expected)
        {
            if (value[end] != ' ') count++;
            end++;
        }
        if (count < expected || (end < value.Length && value[end] != ' ')) return 0;
        return IsValidIban(value[..end].Replace(" ", "", StringComparison.Ordinal)) ? end : 0;
    }

    private static bool IsValidIban(string iban)
    {
        var remainder = 0;
        foreach (var c in iban[4..] + iban[..4])
        {
            var digits = char.IsAsciiDigit(c) ? c - '0' : c - 'A' + 10;
            remainder = digits < 10 ? (remainder * 10 + digits) % 97 : (remainder * 100 + digits) % 97;
        }
        return remainder == 1;
    }

    private static bool PassesLuhn(string value)
    {
        var sum = 0;
        var doubleIt = false;
        for (var i = value.Length - 1; i >= 0; i--)
        {
            if (!char.IsAsciiDigit(value[i])) continue;
            var digit = value[i] - '0';
            if (doubleIt && (digit *= 2) > 9) digit -= 9;
            sum += digit;
            doubleIt = !doubleIt;
        }
        return sum % 10 == 0;
    }

    // NHS England: weights 10 down to 2 over the first nine digits; 11 minus the remainder
    // modulo 11 is the check digit, 11 counts as 0 and 10 is never issued.
    private static bool IsValidNhs(string value)
    {
        var digits = value.Where(char.IsAsciiDigit).Select(c => c - '0').ToArray();
        var sum = 0;
        for (var i = 0; i < 9; i++) sum += digits[i] * (10 - i);
        var check = 11 - sum % 11;
        if (check == 11) check = 0;
        return check != 10 && check == digits[9];
    }

    // Dutch elfproef for the BSN: weights 9 down to 2, the last digit weighted -1, and the sum
    // divisible by 11.
    private static bool IsValidBsn(string value)
    {
        var digits = value.Where(char.IsAsciiDigit).Select(c => c - '0').ToArray();
        if (digits.All(d => d == 0)) return false;
        var sum = -digits[8];
        for (var i = 0; i < 8; i++) sum += digits[i] * (9 - i);
        return sum % 11 == 0;
    }

    private static bool IsIpv6(string value) =>
        value.Any(char.IsAsciiHexDigit) &&
        IPAddress.TryParse(value, out var address) &&
        address.AddressFamily == AddressFamily.InterNetworkV6;

    private static bool HasPhoneDigitCount(string value) => value.Count(char.IsAsciiDigit) is >= 8 and <= 15;

    // The word that holds the match start and the words before it are compared with the context
    // words, ignoring case. A context word may match inside a longer word, so "road" matches
    // "roads". The scan stays within a fixed distance of the match.
    private static bool HasContextWord(string text, int start, string[] context)
    {
        var words = new List<string>(ContextWords + 1);
        var cursor = start;
        while (cursor < text.Length && char.IsLetterOrDigit(text[cursor])) cursor++;
        var floor = Math.Max(0, start - ContextScanLimit);
        while (words.Count <= ContextWords && cursor > floor)
        {
            while (cursor > floor && !char.IsLetterOrDigit(text[cursor - 1])) cursor--;
            var wordEnd = cursor;
            while (cursor > floor && char.IsLetterOrDigit(text[cursor - 1])) cursor--;
            if (wordEnd > cursor) words.Add(text[cursor..wordEnd].ToLowerInvariant());
        }
        return context.Any(word => words.Any(candidate => candidate.Contains(word, StringComparison.Ordinal)));
    }
}
