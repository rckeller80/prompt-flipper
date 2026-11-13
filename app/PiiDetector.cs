using System.Text.RegularExpressions;
using PromptFlipper.Pii;

namespace PromptFlipper;

/// <summary>One piece of text that matched a PII category, with the matched text for review.</summary>
public sealed record PiiFinding(string Category, string Value);

/// <summary>
/// Pattern-based detection of structured personal details: each recognizer has patterns with a
/// base score, context words and optional checksum validation. Names and street addresses are
/// not detected, because recognizing them needs a language model and the app ships none.
/// </summary>
public static class PiiDetector
{
    /// <summary>
    /// A result is shown when its final score is at least this value. A weak match is shown only
    /// when a context word lifts it, because a context word sets the score to at least 0.4.
    /// </summary>
    public const double ScoreThreshold = 0.4;

    private const double ContextSimilarityFactor = 0.35;
    private const double MinScoreWithContext = 0.4;
    private const int ContextPrefixCount = 5;

    // The farthest Detect looks back from a match for context words.
    private const int ContextScanLimit = 512;

    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["CREDIT_CARD"] = "Card number",
        ["CRYPTO"] = "Crypto wallet",
        ["DATE_TIME"] = "Date",
        ["EMAIL_ADDRESS"] = "Email address",
        ["IBAN_CODE"] = "IBAN",
        ["IP_ADDRESS"] = "IP address",
        ["MAC_ADDRESS"] = "MAC address",
        ["PHONE_NUMBER"] = "Phone number",
        ["URL"] = "URL",
        ["UUID"] = "UUID",
        ["ABA_ROUTING_NUMBER"] = "US bank routing number",
        ["MEDICAL_LICENSE"] = "US DEA number",
        ["US_BANK_NUMBER"] = "US bank account number",
        ["US_DRIVER_LICENSE"] = "US driver's license",
        ["US_HEALTH_INSURANCE_MEMBER_ID"] = "US health insurance member ID",
        ["US_PRIOR_AUTHORIZATION_NUMBER"] = "US prior authorization number",
        ["US_CLAIM_NUMBER"] = "US claim number",
        ["US_PRESCRIPTION_NUMBER"] = "US prescription number",
        ["US_REFERRAL_NUMBER"] = "US referral number",
        ["US_PROVIDER_TAX_ID"] = "US provider tax ID",
        ["US_ITIN"] = "US ITIN",
        ["US_MBI"] = "US Medicare number",
        ["US_NPI"] = "US provider number (NPI)",
        ["US_PASSPORT"] = "US passport number",
        ["US_SSN"] = "US Social Security number",
        ["UK_DRIVING_LICENCE"] = "UK driving licence",
        ["UK_NHS"] = "UK NHS number",
        ["UK_NINO"] = "UK National Insurance number",
        ["UK_PASSPORT"] = "UK passport number",
        ["UK_POSTCODE"] = "UK postcode",
        ["UK_VEHICLE_REGISTRATION"] = "UK vehicle registration",
    };

    /// <summary>The display category of every recognizer, in registry order.</summary>
    public static IReadOnlyList<string> CoveredCategories { get; } =
        PiiRegistry.All.Select(recognizer => Labels[recognizer.Entity]).Distinct().ToArray();

    public const string CoverageStatement =
        "Finds: email, phone, IBAN, card, IDs and more. Not names or addresses.";

    public static IReadOnlyList<PiiFinding> Detect(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<PiiFinding>();
        }

        var results = new List<PiiResult>();
        foreach (var recognizer in PiiRegistry.All)
        {
            results.AddRange(recognizer.Analyze is { } analyze ? analyze(text, recognizer) : AnalyzePatterns(text, recognizer));
        }

        var order = PiiRegistry.All.Select((recognizer, index) => (recognizer, index))
            .ToDictionary(pair => pair.recognizer, pair => pair.index);
        var scored = results
            .Select(result => result with { Score = WithContext(text, result) })
            .Where(result => result.Score >= ScoreThreshold)
            // Review lists one category per span: the highest score wins, then the longer
            // match, then the recognizer listed first.
            .OrderByDescending(result => result.Score)
            .ThenByDescending(result => result.Length)
            .ThenBy(result => order[result.Recognizer])
            .ThenBy(result => result.Start);

        var accepted = new List<PiiResult>();
        foreach (var result in scored)
        {
            if (!accepted.Any(kept => result.Start < kept.End && kept.Start < result.End))
            {
                accepted.Add(result);
            }
        }

        return accepted
            .OrderBy(result => result.Start)
            .Select(result => new PiiFinding(Labels[result.Recognizer.Entity], text[result.Start..result.End]))
            .ToList();
    }

    /// <summary>Runs each pattern, applies validation, and drops matches scored 0.</summary>
    internal static IEnumerable<PiiResult> AnalyzePatterns(string text, PiiRecognizer recognizer)
    {
        var results = new List<PiiResult>();
        for (var i = 0; i < recognizer.Patterns.Length; i++)
        {
            try
            {
                foreach (Match match in recognizer.Regexes[i].Matches(text))
                {
                    if (match.Length == 0)
                    {
                        continue;
                    }

                    var score = recognizer.Patterns[i].Score;
                    if (recognizer.Validate?.Invoke(match.Value) is { } valid)
                    {
                        score = valid ? 1.0 : 0;
                    }
                    if (recognizer.Invalidate?.Invoke(match.Value) == true)
                    {
                        score = 0;
                    }
                    if (score > 0)
                    {
                        results.Add(new PiiResult(recognizer, match.Index, match.Index + match.Length, score));
                    }
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // A pattern that times out is skipped; the results of the others stand.
            }
        }
        return RemoveDuplicates(results);
    }

    /// <summary>
    /// Drops a result when a result of the same entity contains it with an equal or higher score.
    /// </summary>
    internal static List<PiiResult> RemoveDuplicates(IEnumerable<PiiResult> results)
    {
        var kept = new List<PiiResult>();
        foreach (var group in results.Distinct().GroupBy(result => result.Recognizer.Entity))
        {
            var active = new List<PiiResult>();
            foreach (var result in group.OrderBy(r => r.Start).ThenByDescending(r => r.Length).ThenByDescending(r => r.Score))
            {
                if (result.Score == 0)
                {
                    continue;
                }
                active.RemoveAll(other => other.End < result.Start);
                if (active.Any(other => other.Start <= result.Start && result.End <= other.End && result.Score <= other.Score))
                {
                    continue;
                }
                kept.Add(result);
                active.Add(result);
            }
        }
        return kept;
    }

    /// <summary>
    /// The word that holds the match start and the 5 words before it are compared with the
    /// recognizer's context words, ignoring case. A context word may match inside a longer word,
    /// so "card" matches "cards" and "creditcard". A match adds 0.35, with a floor of 0.4.
    /// </summary>
    private static double WithContext(string text, PiiResult result)
    {
        var context = result.Recognizer.Context;
        if (context.Length == 0 || !HasContextWord(text, result.Start, context))
        {
            return result.Score;
        }
        return Math.Min(1.0, Math.Max(result.Score + ContextSimilarityFactor, MinScoreWithContext));
    }

    private static bool HasContextWord(string text, int start, string[] context)
    {
        // The first word that ends after the match start: the word holding it, or the next one.
        var position = start;
        while (position > 0 && char.IsLetterOrDigit(text[position - 1]))
        {
            position--;
        }
        var limit = Math.Min(text.Length, start + ContextScanLimit);
        while (position < limit && !char.IsLetterOrDigit(text[position]))
        {
            position++;
        }

        var words = new List<string>(ContextPrefixCount + 1);
        if (position < limit)
        {
            var end = position;
            while (end < text.Length && char.IsLetterOrDigit(text[end]))
            {
                end++;
            }
            words.Add(text[position..end].ToLowerInvariant());
        }

        var floor = Math.Max(0, start - ContextScanLimit);
        var cursor = Math.Min(position, start);
        while (words.Count <= ContextPrefixCount && cursor > floor)
        {
            while (cursor > floor && !char.IsLetterOrDigit(text[cursor - 1]))
            {
                cursor--;
            }
            var wordEnd = cursor;
            while (cursor > floor && char.IsLetterOrDigit(text[cursor - 1]))
            {
                cursor--;
            }
            if (wordEnd > cursor)
            {
                words.Add(text[cursor..wordEnd].ToLowerInvariant());
            }
        }

        foreach (var word in context)
        {
            var lowered = word.ToLowerInvariant();
            if (words.Any(candidate => candidate.Contains(lowered, StringComparison.Ordinal)))
            {
                return true;
            }
        }
        return false;
    }
}
