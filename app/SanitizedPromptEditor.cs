using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PromptFlipper;

/// <summary>
/// Plain-text prompt editor that can show a sanitized view with protected placeholder spans.
/// The original values live only in memory for the current card and are never written by this control.
/// </summary>
public sealed class SanitizedPromptEditor : RichTextBox
{
    private const int WmCut = 0x0300;
    private const int WmCopy = 0x0301;
    private const int WmPaste = 0x0302;
    private const int WmUndo = 0x0304;
    private const int EmUndo = 0x00C7;
    private const int EmRedo = 0x0454;

    private SanitizedPromptSession session = new();
    private int suppress;

    public SanitizedPromptEditor()
    {
        DetectUrls = false;
        EnableAutoDragDrop = false;
        AllowDrop = false;
        ShortcutsEnabled = true;
    }

    /// <summary>Raised when the editor enters or leaves the sanitized view.</summary>
    public event EventHandler? SanitizedChanged;

    /// <summary>Raised when an edit is refused because it would change a protected placeholder.</summary>
    public event EventHandler? ProtectedEditRejected;

    public bool IsSanitized => session.IsSanitized;

    /// <summary>Current protected spans in display coordinates.</summary>
    public IReadOnlyList<SanitizedSpan> Spans => session.Spans;

    /// <summary>Setting the text loads a card: it ends any sanitized view and clears undo.</summary>
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string Text
    {
        get => base.Text;
        set
        {
            var hadSpans = IsSanitized;
            session.Load(value);
            suppress++;
            try
            {
                base.Text = value ?? string.Empty;
                ResetFormatting();
            }
            finally
            {
                suppress--;
            }

            session.Load(value, base.Text);
            ClearUndo();
            OnTextChanged(EventArgs.Empty);
            if (hadSpans) SanitizedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The prompt with every placeholder restored and the card's line endings.</summary>
    public string PromptText => session.PromptText;

    /// <summary>
    /// Shows <paramref name="sanitized"/> (produced from <see cref="PromptText"/>) with its spans protected.
    /// Returns false and leaves the editor unchanged when the view cannot be shown exactly.
    /// </summary>
    public bool ApplySanitized(string sanitized, IReadOnlyList<SanitizedSpan> sanitizedSpans)
    {
        var candidate = session.Copy();
        var previousDisplay = base.Text;
        var wasSanitized = IsSanitized;
        if (!candidate.ApplySanitized(sanitized, sanitizedSpans)) return false;
        var display = candidate.Display;
        suppress++;
        try
        {
            base.Text = display;
            if (!string.Equals(base.Text, display, StringComparison.Ordinal))
            {
                base.Text = previousDisplay;
                ResetFormatting();
                return false;
            }

            session = candidate;
            ResetFormatting();
            Select(0, 0);
        }
        finally
        {
            suppress--;
        }

        ClearUndo();
        OnTextChanged(EventArgs.Empty);
        if (wasSanitized != IsSanitized || IsSanitized) SanitizedChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Replaces every tracked placeholder with its original value and keeps all other edits.</summary>
    public bool Desanitize()
    {
        if (!session.Desanitize()) return false;

        var restored = session.Display;
        suppress++;
        try
        {
            base.Text = restored;
            ResetFormatting();
            Select(0, 0);
        }
        finally
        {
            suppress--;
        }

        ClearUndo();
        OnTextChanged(EventArgs.Empty);
        SanitizedChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Captures the sanitized view so it can be shown again after the same card is reloaded.</summary>
    public SanitizedPromptSession.SanitizedView? CaptureView() => session.CaptureView();

    /// <summary>Shows a captured view when the loaded prompt still matches it.</summary>
    public bool RestoreView(SanitizedPromptSession.SanitizedView? view)
    {
        if (!session.RestoreView(view)) return false;
        suppress++;
        try
        {
            base.Text = session.Display;
            ResetFormatting();
            Select(0, 0);
        }
        finally { suppress--; }
        ClearUndo();
        SanitizedChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public bool CanReplace(int start, int length) => session.CanReplace(start, length);

    protected override void OnTextChanged(EventArgs e)
    {
        if (suppress > 0) return;
        var current = base.Text;
        var changed = !string.Equals(current, session.Display, StringComparison.Ordinal);
        if (!session.TryEdit(current, out var edit))
        {
            RevertEdit(current);
            return;
        }
        if (changed && IsSanitized && edit.Inserted > 0)
        {
            suppress++;
            try
            {
                var selectionStart = SelectionStart;
                var selectionLength = SelectionLength;
                Select(edit.Start, edit.Inserted);
                SelectionColor = ForeColor;
                SelectionBackColor = BackColor;
                Select(selectionStart, selectionLength);
            }
            finally { suppress--; }
        }
        base.OnTextChanged(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Control | Keys.V:
            case Keys.Shift | Keys.Insert:
                PastePlainText();
                return true;
            case Keys.Control | Keys.C:
            case Keys.Control | Keys.Insert:
                CopyPlainText();
                return true;
            case Keys.Control | Keys.X:
            case Keys.Shift | Keys.Delete:
                CutPlainText();
                return true;
            case Keys.Control | Keys.Z:
            case Keys.Control | Keys.Y:
            case Keys.Alt | Keys.Back:
            case Keys.Control | Keys.Shift | Keys.Z:
                if (IsSanitized) return true;
                break;
            case Keys.Control | Keys.L:
            case Keys.Control | Keys.E:
            case Keys.Control | Keys.R:
            case Keys.Control | Keys.J:
            case Keys.Control | Keys.D1:
            case Keys.Control | Keys.D2:
            case Keys.Control | Keys.D5:
            case Keys.Control | Keys.Oemplus:
            case Keys.Control | Keys.Shift | Keys.Oemplus:
                return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WmPaste:
                PastePlainText();
                return;
            case WmCopy:
                CopyPlainText();
                return;
            case WmCut:
                CutPlainText();
                return;
            case WmUndo:
            case EmUndo:
            case EmRedo:
                if (IsSanitized)
                {
                    m.Result = IntPtr.Zero;
                    return;
                }

                break;
        }

        base.WndProc(ref m);
    }

    private void PastePlainText()
    {
        if (ReadOnly || !Clipboard.ContainsText()) return;
        var text = Clipboard.GetText(TextDataFormat.UnicodeText);
        if (!CanReplace(SelectionStart, SelectionLength))
        {
            ProtectedEditRejected?.Invoke(this, EventArgs.Empty);
            return;
        }

        SelectedText = text;
    }

    private void CopyPlainText()
    {
        if (SelectionLength > 0) Clipboard.SetText(ClipboardText(SelectedText), TextDataFormat.UnicodeText);
    }

    /// <summary>RichEdit reports LF; the clipboard carries Windows CRLF, as the former TextBox editor did.</summary>
    private static string ClipboardText(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);

    private void CutPlainText()
    {
        if (ReadOnly || SelectionLength == 0) return;
        if (!CanReplace(SelectionStart, SelectionLength))
        {
            ProtectedEditRejected?.Invoke(this, EventArgs.Empty);
            return;
        }

        Clipboard.SetText(ClipboardText(SelectedText), TextDataFormat.UnicodeText);
        SelectedText = string.Empty;
    }

    private void RevertEdit(string current)
    {
        var edit = SanitizedPromptSession.LocateEdit(session.Display, current);
        suppress++;
        try
        {
            Select(edit.Start, edit.Inserted);
            SelectedText = session.Display.Substring(edit.Start, edit.Removed);
            if (!string.Equals(base.Text, session.Display, StringComparison.Ordinal)) base.Text = session.Display;
            ResetFormatting();
            Select(Math.Min(edit.Start, base.Text.Length), 0);
        }
        finally { suppress--; }
        ClearUndo();
        System.Media.SystemSounds.Beep.Play();
        ProtectedEditRejected?.Invoke(this, EventArgs.Empty);
    }

    private void ResetFormatting()
    {
        var selectionStart = SelectionStart;
        var selectionLength = SelectionLength;
        suppress++;
        try
        {
            SelectAll();
            SelectionColor = ForeColor;
            SelectionBackColor = BackColor;
            foreach (var span in session.Spans)
            {
                Select(span.Start, span.Length);
                SelectionColor = PlaceholderColor;
                SelectionBackColor = PlaceholderColor;
            }

            Select(Math.Min(selectionStart, TextLength), Math.Min(selectionLength, Math.Max(0, TextLength - selectionStart)));
        }
        finally
        {
            suppress--;
        }
    }

    /// <summary>
    /// Color used for both the text and the background of a protected placeholder. Drawn from
    /// <see cref="SystemColors.ControlDarkDark"/> rather than a fixed hex value, so the redacted
    /// block keeps contrast with the surrounding <see cref="SystemColors.Window"/> text under
    /// dark mode and high contrast instead of only matching the one theme it was picked under.
    /// </summary>
    public Color PlaceholderColor { get; set; } = SystemColors.ControlDarkDark;

}
