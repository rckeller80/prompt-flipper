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
