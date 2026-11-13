using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PromptFlipper;

public sealed class SanitizerRule
{
    public const string PlaceholderNamespace = "PromptFlipper.Sanitizer";

    public const string HiddenValue = "********";

    public SanitizerRule(string name, string value)
    {
        Name = name;
        Value = value;
    }

    public string Name { get; }

    public string Value { get; }

    public string ShownValue(bool hidden) => hidden ? HiddenValue : Value;

    // The secret value leads; the placeholder name is secondary.
    public string ListText(bool hidden) => ShownValue(hidden) + " (" + Name + ")";

    public string NormalizedName => NormalizeName(Name);

    public string Placeholder => "{{" + PlaceholderNamespace + "." + NormalizedName + "}}";

    public static string NormalizeName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var source = name.Trim().Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var normalized = new StringBuilder(source.Length);
        var separatorPending = false;

        foreach (var character in source)
        {
            if (char.IsLetterOrDigit(character))
            {
                if (separatorPending && normalized.Length > 0)
                {
                    normalized.Append('-');
                }

                normalized.Append(character);
                separatorPending = false;
            }
            else if (normalized.Length > 0)
            {
                separatorPending = true;
            }
        }

        return normalized.ToString();
    }
}

public static class Sanitizer
{
    public static bool TryQuickSanitize(string? input, Func<IEnumerable<SanitizerRule>> loadRules,
        Action<string> writeOutput, out string status)
    {
        return TryQuickSanitize(input, loadRules, writeOutput, out status, out _, out _);
    }

    /// <summary>Sanitizes, writes the output, and returns the result for the editor view. Nothing is returned on failure.</summary>
    public static bool TryQuickSanitize(string? input, Func<IEnumerable<SanitizerRule>> loadRules,
        Action<string> writeOutput, out string status, out string? sanitized,
        out IReadOnlyList<SanitizedSpan> spans)
    {
        ArgumentNullException.ThrowIfNull(writeOutput);
        sanitized = null;
        spans = Array.Empty<SanitizedSpan>();
        status = "Open a non-empty card before using Quick Sanitize. Nothing was copied.";
        if (string.IsNullOrEmpty(input)) return false;
        if (!TryLoadRules(loadRules, out var rules) || rules.Count == 0)
        {
            status = "Saved placeholders are missing or invalid. Open Sanitizer setup. Nothing was copied.";
            return false;
        }
        if (!TrySanitize(input, rules, out var output, out var found) || output is null)
        {
            status = "The placeholders are invalid or ambiguous for this card. Nothing was copied.";
            return false;
        }
        try
        {
            writeOutput(output);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            status = "The clipboard is unavailable. Nothing was copied. Try again.";
            return false;
        }
        sanitized = output;
        spans = found;
        status = "Sanitized and copied. " + QuickSanitizeScope;
        return true;
    }

    /// <summary>The warning shown with Quick Sanitize, which runs no personal details check.</summary>
    public const string QuickSanitizeScope =
        "Saved secrets only. No personal details check.";

    public static IEnumerable<SanitizerRule> FilterRules(
        IEnumerable<SanitizerRule> rules, string search)
    {
        return TextSearch.Filter(rules, search, rule => new[] { rule.Name, rule.Value });
    }

    // Value order for the list: A-Z by secret value, ties broken by placeholder name.
    public static IEnumerable<SanitizerRule> SortByValue(IEnumerable<SanitizerRule> rules)
    {
        return rules
            .OrderBy(rule => rule.Value, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(rule => rule.Name, StringComparer.Ordinal);
    }

    public static string NextRuleName(IEnumerable<SanitizerRule> rules)
    {
        var names = rules.Select(rule => rule.NormalizedName).ToHashSet(StringComparer.Ordinal);
        var number = 1;
        while (names.Contains("sanitize-" + number.ToString("D3", System.Globalization.CultureInfo.InvariantCulture)))
        {
            number++;
        }
        return "sanitize-" + number.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static bool TryImportRulesAndWrite(
        Func<IEnumerable<string>> readLines,
        IEnumerable<SanitizerRule> existingRules,
        Action<IReadOnlyList<SanitizerRule>> writeRules,
        out IReadOnlyList<SanitizerRule> rules)
    {
        ArgumentNullException.ThrowIfNull(readLines);
        ArgumentNullException.ThrowIfNull(existingRules);
        ArgumentNullException.ThrowIfNull(writeRules);

        rules = Array.Empty<SanitizerRule>();
        try
        {
            var currentRules = existingRules.ToArray();
            var lines = readLines()?.ToArray();
            if (lines is null)
            {
                return false;
            }

            var candidate = currentRules.ToList();
            foreach (var value in lines.Where(static line => !string.IsNullOrWhiteSpace(line)))
            {
                candidate.Add(new SanitizerRule(NextRuleName(candidate), value));
            }
            if (candidate.Count == currentRules.Length)
            {
                return false;
            }

            if (!TrySanitize(string.Empty, candidate, out _))
            {
                return false;
            }

            writeRules(candidate);
            rules = candidate;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool TryLoadRules(
        Func<IEnumerable<SanitizerRule>> loadRules,
        out IReadOnlyList<SanitizerRule> rules)
    {
        ArgumentNullException.ThrowIfNull(loadRules);

        rules = Array.Empty<SanitizerRule>();
        try
        {
            var loadedRules = loadRules()?.ToArray();
            if (loadedRules is null || !TrySanitize(string.Empty, loadedRules, out _))
            {
                return false;
            }

            rules = loadedRules;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool TrySanitizeAndWrite(
        string input,
        IEnumerable<SanitizerRule> rules,
        Action<string> writeOutput,
        out string? sanitized)
    {
        ArgumentNullException.ThrowIfNull(writeOutput);

        if (!TrySanitize(input, rules, out sanitized) || sanitized is null)
        {
            return false;
        }

        writeOutput(sanitized);
        return true;
    }

    public static bool TrySanitize(
        string input,
        IEnumerable<SanitizerRule> rules,
        out string? sanitized)
    {
        return TrySanitize(input, rules, out sanitized, out _);
    }

    public static bool TrySanitize(
        string input,
        IEnumerable<SanitizerRule> rules,
        out string? sanitized,
        out IReadOnlyList<SanitizedSpan> spans)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(rules);

        sanitized = null;
        spans = Array.Empty<SanitizedSpan>();
        var orderedRules = rules.ToArray();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var normalizedNames = new HashSet<string>(StringComparer.Ordinal);
        var values = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rule in orderedRules)
        {
            if (rule is null ||
                string.IsNullOrEmpty(rule.Name) ||
                string.IsNullOrEmpty(rule.NormalizedName) ||
                string.IsNullOrEmpty(rule.Value) ||
                !names.Add(rule.Name) ||
                !normalizedNames.Add(rule.NormalizedName) ||
                !values.Add(rule.Value) ||
                input.Contains(rule.Placeholder, StringComparison.Ordinal))
            {
                return false;
            }
        }

        Array.Sort(
            orderedRules,
            static (left, right) =>
            {
                var lengthComparison = right.Value.Length.CompareTo(left.Value.Length);
                if (lengthComparison != 0)
                {
                    return lengthComparison;
                }

                var valueComparison = StringComparer.Ordinal.Compare(left.Value, right.Value);
                return valueComparison != 0
                    ? valueComparison
                    : StringComparer.Ordinal.Compare(left.Name, right.Name);
            });

        var result = new StringBuilder(input.Length);
        var found = new List<SanitizedSpan>();

        for (var index = 0; index < input.Length;)
        {
            SanitizerRule? match = null;

            foreach (var rule in orderedRules)
            {
                if (input.AsSpan(index).StartsWith(rule.Value.AsSpan(), StringComparison.Ordinal))
                {
                    match = rule;
                    break;
                }
            }

            if (match is null)
            {
                result.Append(input[index]);
                index++;
                continue;
            }

            found.Add(new SanitizedSpan(result.Length, match.Placeholder, match.Value));
            result.Append(match.Placeholder);
            index += match.Value.Length;
        }

        sanitized = result.ToString();
        spans = found;
        return true;
    }
}

/// <summary>One substituted placeholder: its start in the sanitized output and the value it replaced.</summary>
public sealed record SanitizedSpan(int Start, string Placeholder, string Original)
{
    public int Length => Placeholder.Length;

    public int End => Start + Placeholder.Length;
}

/// <summary>One piece of text that matched a PII category, with the matched text for review.</summary>
public sealed record PiiFinding(string Category, string Value);

/// <summary>
/// Pattern-based detection of structured personal details. Names and street addresses are not
/// detected: no local detector that ships inside the single-file build recognizes them.
/// </summary>
public static class PiiDetector
{
    public const string Email = "Email address";
    public const string Phone = "Phone number";
    public const string Iban = "IBAN";
    public const string Card = "Card number";
    public const string Ssn = "US Social Security number";
    public const string Postcode = "UK postcode";

    public static IReadOnlyList<string> CoveredCategories { get; } = [Email, Phone, Iban, Card, Ssn, Postcode];

    public const string CoverageStatement =
        "Finds: email, phone, IBAN, card, US SSN, UK postcode. Not names or addresses.";

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

    private static readonly Regex IbanPattern = new(
        @"(?<![A-Za-z0-9])([A-Z]{2})\d{2}(?: ?[A-Z0-9]){11,30}", RegexOptions.Compiled);
    private static readonly Regex CardPattern = new(
        @"(?<!\d)\d(?:[ -]?\d){12,18}(?!\d)", RegexOptions.Compiled);
    private static readonly Regex SsnPattern = new(
        @"(?<!\d)(?!000|666|9\d\d)\d{3}-(?!00)\d{2}-(?!0000)\d{4}(?!\d)", RegexOptions.Compiled);
    private static readonly Regex PostcodePattern = new(
        @"(?<![A-Za-z0-9])[A-Z]{1,2}\d[A-Z\d]? ?\d[A-Z]{2}(?![A-Za-z0-9])", RegexOptions.Compiled);
    private static readonly Regex EmailPattern = new(
        @"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);
    private static readonly Regex NanpPhonePattern = new(
        @"(?<![\d+])(?:\+1[ .-]?)?(?:\(\d{3}\)|\d{3})[ .-]?\d{3}[ .-]?\d{4}(?!\d)", RegexOptions.Compiled);
    private static readonly Regex InternationalPhonePattern = new(
        @"(?<![\w+])\+\d{1,3}(?:[ .-]?\(0\))?(?:[ .-]?\d){6,13}(?!\d)", RegexOptions.Compiled);
    // National numbers with a trunk 0: 11 digits in the UK, 10 in the Netherlands.
    private static readonly Regex NationalPhonePattern = new(
        @"(?<![\w+])0\d(?:[ .-]?\d){8,9}(?!\d)", RegexOptions.Compiled);

    public static IReadOnlyList<PiiFinding> Detect(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<PiiFinding>();
        }

        // Earlier categories win: an IBAN or card number is never also listed as a phone number.
        var accepted = new List<(int Start, int End, PiiFinding Finding)>();
        void Accept(int start, int length, string category)
        {
            var end = start + length;
            if (accepted.Any(span => start < span.End && span.Start < end)) return;
            accepted.Add((start, end, new PiiFinding(category, text.Substring(start, length))));
        }

        foreach (Match match in IbanPattern.Matches(text))
        {
            if (TryMeasureIban(text, match, out var length)) Accept(match.Index, length, Iban);
        }
        foreach (Match match in CardPattern.Matches(text))
        {
            if (PassesLuhn(match.Value)) Accept(match.Index, match.Length, Card);
        }
        foreach (Match match in SsnPattern.Matches(text)) Accept(match.Index, match.Length, Ssn);
        foreach (Match match in EmailPattern.Matches(text)) Accept(match.Index, match.Length, Email);
        foreach (Match match in PostcodePattern.Matches(text)) Accept(match.Index, match.Length, Postcode);
        foreach (var pattern in new[] { NanpPhonePattern, InternationalPhonePattern, NationalPhonePattern })
        {
            foreach (Match match in pattern.Matches(text))
            {
                var digits = match.Value.Count(char.IsAsciiDigit);
                if (digits is >= 8 and <= 15) Accept(match.Index, match.Length, Phone);
            }
        }
        return accepted.OrderBy(span => span.Start).Select(span => span.Finding).ToList();
    }

    // Structure only: country code and that country's length. Check digits are not verified, so a
    // mistyped or example IBAN is still reported.
    private static bool TryMeasureIban(string text, Match match, out int length)
    {
        length = 0;
        if (!IbanLengths.TryGetValue(match.Groups[1].Value, out var expected)) return false;
        var count = 0;
        var end = match.Index;
        while (end < match.Index + match.Length && count < expected)
        {
            if (char.IsAsciiLetterOrDigit(text[end])) count++;
            end++;
        }
        if (count < expected || (end < text.Length && char.IsAsciiLetterOrDigit(text[end]))) return false;
        length = end - match.Index;
        return true;
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
}
