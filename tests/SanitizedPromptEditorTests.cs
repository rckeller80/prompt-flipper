using System.Drawing;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using Xunit;

namespace PromptFlipper.Tests;

[Collection("Quick sanitize Windows state")]
public sealed class SanitizedPromptEditorTests
{
    private static readonly SanitizerRule Token = new("token", "secret-1");
    private static readonly SanitizerRule Key = new("key", "k");

    private static void RunSta(Action<SanitizedPromptEditor> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new Form();
                using var editor = new SanitizedPromptEditor { Multiline = true, Dock = DockStyle.Fill };
                form.Controls.Add(editor);
                form.Show();
                Application.DoEvents();
                body(editor);
                form.Close();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Editor run timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void Sanitize(SanitizedPromptEditor editor, params SanitizerRule[] rules)
    {
        Assert.True(Sanitizer.TrySanitize(editor.PromptText, rules, out var output, out var spans));
        Assert.True(editor.ApplySanitized(output!, spans));
    }

    private static void Type(SanitizedPromptEditor editor, int start, int length, string text)
    {
        editor.Select(start, length);
        editor.SelectedText = text;
    }

    [Fact]
    public void SanitizeShowsPlaceholdersWithMatchingColors()
    {
        RunSta(editor =>
        {
            editor.Text = "Use secret-1 now";
            Sanitize(editor, Token);
            Assert.True(editor.IsSanitized);
            Assert.Equal("Use " + Token.Placeholder + " now", editor.Text);
            var span = Assert.Single(editor.Spans);
            editor.Select(span.Start + 3, 1);
            Assert.Equal(editor.SelectionColor.ToArgb(), editor.SelectionBackColor.ToArgb());
            Assert.Equal(editor.PlaceholderColor.ToArgb(), editor.SelectionColor.ToArgb());
            editor.Select(1, 1);
            Assert.NotEqual(editor.SelectionColor.ToArgb(), editor.SelectionBackColor.ToArgb());
            Assert.Equal("Use secret-1 now", editor.PromptText);
        });
    }

    [Fact]
    public void ProtectedSpansRefuseEveryEditPath()
    {
        RunSta(editor =>
        {
            editor.Text = "a secret-1 b";
            Sanitize(editor, Token);
            var sanitized = editor.Text;
            var span = editor.Spans[0];
            var rejected = 0;
            editor.ProtectedEditRejected += (_, _) => rejected++;

            Type(editor, span.Start + 2, 0, "x");
            Assert.Equal(sanitized, editor.Text);
            Type(editor, span.Start + 1, 1, string.Empty);
            Assert.Equal(sanitized, editor.Text);
            Type(editor, 0, span.Start + 1, "z");
            Assert.Equal(sanitized, editor.Text);
            Type(editor, span.End - 1, 3, string.Empty);
            Assert.Equal(sanitized, editor.Text);

            Clipboard.SetText("pasted");
            editor.Select(span.Start + 1, 0);
            editor.Paste();
            Assert.Equal(sanitized, editor.Text);
            editor.Select(span.Start, span.Length);
            editor.Cut();
            Assert.Equal(sanitized, editor.Text);
            Assert.Equal("pasted", Clipboard.GetText());

            editor.Undo();
            Assert.Equal(sanitized, editor.Text);
            Assert.False(editor.CanUndo);
            Assert.True(rejected >= 6);
            Assert.Single(editor.Spans);
            Assert.Equal("a secret-1 b", editor.PromptText);
        });
    }

    [Fact]
    public void SurroundingTextStaysEditableAndIsKeptOnRestore()
    {
        RunSta(editor =>
        {
            editor.Text = "k-secret-1-k";
            Sanitize(editor, Token, Key);
            Assert.Equal(3, editor.Spans.Count);
            Type(editor, 0, 0, "start ");
            Type(editor, editor.TextLength, 0, " end");
            var second = editor.Spans[1];
            Type(editor, second.Start, 0, "<");
            second = editor.Spans[1];
            Type(editor, second.End, 0, ">");
            Assert.Equal(3, editor.Spans.Count);
            Assert.Equal("start k-<secret-1>-k end", editor.PromptText);

            editor.Select(1, 1);
            Assert.Equal(editor.ForeColor.ToArgb(), editor.SelectionColor.ToArgb());
            var afterSpan = editor.Spans[1].End;
            editor.Select(afterSpan, 1);
            Assert.Equal(editor.BackColor.ToArgb(), editor.SelectionBackColor.ToArgb());

            Assert.True(editor.Desanitize());
            Assert.False(editor.IsSanitized);
            Assert.Equal("start k-<secret-1>-k end", editor.Text);
            Assert.False(editor.CanUndo);
            Assert.False(editor.Desanitize());
        });
    }

    [Fact]
    public void RepeatedSanitizeAndRuleChangesUseRestoredText()
    {
        RunSta(editor =>
        {
            editor.Text = "secret-1 and k";
            Sanitize(editor, Token);
            Type(editor, editor.TextLength, 0, " secret-1");
            Sanitize(editor, Token, Key);
            Assert.Equal(Token.Placeholder + " and " + Key.Placeholder + " " + Token.Placeholder, editor.Text);
            Assert.Equal(3, editor.Spans.Count);
            Assert.Equal("secret-1 and k secret-1", editor.PromptText);
            Assert.False(Sanitizer.TrySanitize(editor.PromptText,
                new[] { new SanitizerRule("a", "same"), new SanitizerRule("b", "same") }, out _, out var none));
            Assert.Empty(none);
            Assert.Equal(3, editor.Spans.Count);
            Assert.True(editor.Desanitize());
            Assert.Equal("secret-1 and k secret-1", editor.Text);
        });
    }

    [Fact]
    public void LoadingTextEndsTheSessionAndKeepsLineEndings()
    {
        RunSta(editor =>
        {
            var changes = 0;
            editor.SanitizedChanged += (_, _) => changes++;
            editor.Text = "line secret-1\r\nnext";
            Assert.Equal("line secret-1\r\nnext", editor.PromptText);
            Sanitize(editor, Token);
            Assert.Equal("line secret-1\r\nnext", editor.PromptText);
            Type(editor, editor.TextLength, 0, "\nmore");
            Assert.Equal("line secret-1\r\nnext\r\nmore", editor.PromptText);

            var view = editor.CaptureView();
            var raw = editor.PromptText;
            editor.Text = raw;
            Assert.False(editor.IsSanitized);
            Assert.True(editor.RestoreView(view));
            Assert.True(editor.IsSanitized);
            Assert.Equal(raw, editor.PromptText);

            editor.Text = "other card";
            Assert.False(editor.IsSanitized);
            Assert.False(editor.RestoreView(view));
            Assert.Equal("other card", editor.PromptText);
            Assert.True(changes >= 4);
        });
    }

    [Fact]
    public void QuickSanitizeOverloadReturnsSpansOnlyOnSuccess()
    {
        var written = new List<string>();
        Assert.True(Sanitizer.TryQuickSanitize("x secret-1", () => new[] { Token }, written.Add,
            out var status, out var output, out var spans));
        Assert.Equal("Sanitized and copied. " + Sanitizer.QuickSanitizeScope, status);
        Assert.Equal(output, Assert.Single(written));
        Assert.Equal(new SanitizedSpan(2, Token.Placeholder, "secret-1"), Assert.Single(spans));

        Assert.False(Sanitizer.TryQuickSanitize("x secret-1", () => new[] { Token },
            _ => throw new System.Runtime.InteropServices.ExternalException("busy"),
            out _, out var failed, out var none));
        Assert.Null(failed);
        Assert.Empty(none);
    }
}
