using System.Text.RegularExpressions;

namespace PromptFlipper.Pii;

/// <summary>One pattern: its name, regular expression and base score.</summary>
internal sealed record PiiPattern(string Name, string Regex, double Score);

/// <summary>One match a recognizer reports, before context scoring and the threshold.</summary>
internal sealed record PiiResult(PiiRecognizer Recognizer, int Start, int End, double Score)
{
    public int Length => End - Start;
}

/// <summary>
/// A recognizer: the entity it reports, its patterns and context words, and the optional
/// methods that validate a match or replace the pattern loop.
/// </summary>
internal sealed class PiiRecognizer(string entity, PiiPattern[] patterns, string[] context)
{
    private Regex[]? regexes;

    /// <summary>The entity name, for example CREDIT_CARD.</summary>
    public string Entity { get; } = entity;

    public PiiPattern[] Patterns { get; } = patterns;

    public string[] Context { get; } = context;

    /// <summary>Whether the patterns ignore case. Most do.</summary>
    public bool IgnoreCase { get; init; } = true;

    /// <summary>True sets the score to 1.0, false removes the match, null keeps the score.</summary>
    public Func<string, bool?>? Validate { get; init; }

    /// <summary>True removes the match.</summary>
    public Func<string, bool>? Invalidate { get; init; }

    /// <summary>Finds matches itself instead of running the pattern loop.</summary>
    public Func<string, PiiRecognizer, IEnumerable<PiiResult>>? Analyze { get; init; }

    /// <summary>The patterns as interpreted regular expressions, built on first use.</summary>
    public Regex[] Regexes => regexes ??= Patterns
        .Select(pattern => new Regex(pattern.Regex, Options, PiiRegistry.MatchTimeout))
        .ToArray();

    public RegexOptions Options =>
        RegexOptions.Singleline | RegexOptions.Multiline | RegexOptions.CultureInvariant |
        (IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
}

internal static partial class PiiRegistry
{
    /// <summary>
    /// Time allowed for one match. A pattern that times out is skipped and the others still run.
    /// Review runs on the UI thread, so the limit is short.
    /// </summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(500);

    private static PiiRecognizer[]? all;

    public static PiiRecognizer[] All => all ??= Ported();
}
