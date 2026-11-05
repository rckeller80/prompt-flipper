using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PromptFlipper;

/// <summary>Tracks recorded substitutions and surrounding edits without a native control.</summary>
public sealed class SanitizedPromptSession
{
    private readonly List<SanitizedSpan> spans = new();
    private string loadedText = string.Empty;
    private string loadedDisplay = string.Empty;

    public string Display { get; private set; } = string.Empty;
    public bool IsSanitized => spans.Count > 0;
    public IReadOnlyList<SanitizedSpan> Spans => spans.ToArray();

    internal SanitizedPromptSession Copy()
    {
        var copy = new SanitizedPromptSession
        {
            loadedText = loadedText,
            loadedDisplay = loadedDisplay,
            Display = Display
        };
        copy.spans.AddRange(spans);
        return copy;
    }

    public void Load(string? text, string? display = null)
    {
        spans.Clear();
        loadedText = text ?? string.Empty;
        loadedDisplay = display ?? ToDisplay(loadedText, out _);
        Display = loadedDisplay;
    }

    public string PromptText
    {
        get
        {
            var display = RestoredDisplay();
            if (string.Equals(display, loadedDisplay, StringComparison.Ordinal)) return loadedText;
            return UsesCrlf(loadedText) ? display.Replace("\n", "\r\n", StringComparison.Ordinal) : display;
        }
    }

    public bool ApplySanitized(string sanitized, IReadOnlyList<SanitizedSpan> sanitizedSpans)
    {
        ArgumentNullException.ThrowIfNull(sanitized);
        ArgumentNullException.ThrowIfNull(sanitizedSpans);
        var display = ToDisplay(sanitized, out var map);
        var mapped = new List<SanitizedSpan>(sanitizedSpans.Count);
        foreach (var span in sanitizedSpans.OrderBy(static span => span.Start))
        {
            if (span.Start < 0 || span.Start > sanitized.Length || span.Length > sanitized.Length - span.Start ||
                !string.Equals(sanitized.Substring(span.Start, span.Length), span.Placeholder, StringComparison.Ordinal) ||
                (mapped.Count > 0 && map[span.Start] < mapped[^1].End)) return false;
            mapped.Add(new SanitizedSpan(map[span.Start], span.Placeholder, ToDisplay(span.Original, out _)));
        }
        Display = display;
        spans.Clear();
        spans.AddRange(mapped);
        return true;
    }

    public bool Desanitize()
    {
        if (!IsSanitized) return false;
        Display = RestoredDisplay();
        spans.Clear();
        return true;
    }

    public SanitizedView? CaptureView() => IsSanitized ? new(PromptText, Display, Array.AsReadOnly(spans.ToArray())) : null;

    public bool RestoreView(SanitizedView? view)
    {
        if (view is null || !string.Equals(PromptText, view.PromptText, StringComparison.Ordinal)) return false;
        Display = view.Display;
        spans.Clear();
        spans.AddRange(view.Spans);
        return true;
    }

    public bool CanReplace(int start, int length) => spans.All(span => length == 0
        ? start <= span.Start || start >= span.End
        : start + length <= span.Start || start >= span.End);

    /// <summary>Accepts a display edit only when every recorded placeholder remains intact.</summary>
    public bool TryEdit(string current, out EditRange edit)
    {
        edit = LocateEdit(Display, current);
        if (string.Equals(Display, current, StringComparison.Ordinal)) return true;
        if (IsSanitized)
        {
            if (!TryLocateEdit(Display, current, edit, out var start)) return false;
            var removed = edit.Removed;
            var inserted = edit.Inserted;
            edit = edit with { Start = start };
            for (var i = 0; i < spans.Count; i++)
                if (spans[i].Start >= start + removed)
                    spans[i] = spans[i] with { Start = spans[i].Start + inserted - removed };
        }
        Display = current;
        return true;
    }

    /// <summary>Finds the smallest single replacement between two display strings.</summary>
    public static EditRange LocateEdit(string before, string after)
    {
        var prefix = 0;
        var max = Math.Min(before.Length, after.Length);
        while (prefix < max && before[prefix] == after[prefix]) prefix++;
        var suffix = 0;
        while (suffix < max - prefix && before[before.Length - 1 - suffix] == after[after.Length - 1 - suffix]) suffix++;
        return new(prefix, before.Length - prefix - suffix, after.Length - prefix - suffix);
    }

    public readonly record struct EditRange(int Start, int Removed, int Inserted);

    /// <summary>
    /// Finds an alignment of the single edit between the two texts that leaves every span intact.
    /// Equal neighbouring characters make the edit position ambiguous, so every valid alignment is tried.
    /// </summary>
    private bool TryLocateEdit(string before, string after, EditRange range, out int start)
    {
        var prefix = range.Start;
        var removed = range.Removed;
        var inserted = range.Inserted;
        for (start = prefix; start >= 0; start--)
        {
            if (start < prefix)
            {
                var beforeIndex = start + removed;
                var afterIndex = start + inserted;
                if (before[beforeIndex] != after[afterIndex]) break;
            }

            if (CanReplace(start, removed)) return true;
        }

        start = prefix;
        return false;
    }

    private string RestoredDisplay()
    {
        var text = Display;
        if (spans.Count == 0) return text;

        var builder = new StringBuilder(text.Length);
        var index = 0;
        foreach (var span in spans.OrderBy(static span => span.Start))
        {
            builder.Append(text, index, span.Start - index);
            builder.Append(span.Original);
            index = span.End;
        }

        builder.Append(text, index, text.Length - index);
        return builder.ToString();
    }

    private static bool UsesCrlf(string text)
    {
        return text.Contains("\r\n", StringComparison.Ordinal) || !text.Contains('\n');
    }

    /// <summary>Converts text to the editor's line-break form and maps each source index to its display index.</summary>
    private static string ToDisplay(string text, out int[] map)
    {
        map = new int[text.Length + 1];
        var builder = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            map[index] = builder.Length;
            var character = text[index];
            if (character == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n') continue;
                builder.Append('\n');
                continue;
            }

            builder.Append(character);
        }

        map[text.Length] = builder.Length;
        return builder.ToString();
    }

    public sealed record SanitizedView(string PromptText, string Display, IReadOnlyList<SanitizedSpan> Spans);

}
