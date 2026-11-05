using System;
using System.Collections.Generic;
using System.Linq;

namespace PromptFlipper;

// The one matcher behind every search in the application. A query is split into words at
// spaces; an item matches when every word occurs, case-insensitive, in at least one of the
// fields the caller passes. An empty query matches everything.
public static class TextSearch
{
    public static string[] Terms(string? query) => (query ?? string.Empty)
        .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static bool Matches(string? query, IEnumerable<string?> fields)
    {
        var terms = Terms(query);
        if (terms.Length == 0) return true;
        var values = fields.Where(field => !string.IsNullOrEmpty(field)).ToArray();
        return terms.All(term => values.Any(value => value!.Contains(term, StringComparison.OrdinalIgnoreCase)));
    }

    // Every occurrence of every query word in text, as (start, length), for highlighting.
    public static IEnumerable<(int Start, int Length)> Ranges(string? text, string? query)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        foreach (var term in Terms(query))
        {
            for (var at = text.IndexOf(term, StringComparison.OrdinalIgnoreCase); at >= 0;
                 at = text.IndexOf(term, at + term.Length, StringComparison.OrdinalIgnoreCase))
                yield return (at, term.Length);
        }
    }

    public static IEnumerable<T> Filter<T>(IEnumerable<T> items, string? query, Func<T, IEnumerable<string?>> fields) =>
        Terms(query).Length == 0 ? items : items.Where(item => Matches(query, fields(item)));
}
