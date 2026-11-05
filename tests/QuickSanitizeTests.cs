using System.Drawing;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Xunit;

namespace PromptFlipper.Tests;

[CollectionDefinition("Quick sanitize Windows state", DisableParallelization = true)]
public sealed class QuickSanitizeWindowsCollection { }

[Collection("Quick sanitize Windows state")]
public sealed class QuickSanitizeTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string Placeholder = "{{PromptFlipper.Sanitizer.token}}";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MissingCardNeverLoadsOrWrites(string? input)
    {
        Assert.False(Sanitizer.TryQuickSanitize(input, () => throw new Exception("Must not load"),
            _ => throw new Exception("Must not write"), out var status));
        Assert.Contains("Nothing was copied", status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void InvalidOrUnavailableRulesNeverWrite(int scenario)
    {
        IEnumerable<SanitizerRule> Load() => scenario switch
        {
            0 => Array.Empty<SanitizerRule>(),
            1 => new[] { new SanitizerRule("empty", "") },
            2 => new[] { new SanitizerRule("one", "same"), new SanitizerRule("two", "same") },
            _ => throw new IOException("Unavailable store")
        };
        Assert.False(Sanitizer.TryQuickSanitize("secret", Load,
            _ => throw new Exception("Must not write"), out var status));
        Assert.Contains("Nothing was copied", status);
    }

    [Fact]
    public void ClipboardFailureHasNoSuccessConfirmation()
    {
        Assert.False(Sanitizer.TryQuickSanitize("secret", () => new[] { new SanitizerRule("token", "secret") },
            _ => throw new ExternalException("Unavailable"), out var status));
        Assert.DoesNotContain("Sanitized and copied", status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClipboardCleanupFailureReturnsFromStaAndPreservesAssertion(bool assertionFails)
    {
        var assertionFailure = new Xunit.Sdk.XunitException("Original assertion failure");
        var cleanupFailure = new ExternalException("Simulated clipboard restoration failure");
        var attempts = 0;
        var remainingCleanupRan = false;

        var failure = Record.Exception(() => MainFormTestHarness.RunInSta(() =>
            WindowsStateTestScope.RunWithCleanup(
                () => { if (assertionFails) throw assertionFailure; },
                () => WindowsStateTestScope.RetryClipboardRestore(() =>
                {
                    attempts++;
                    throw cleanupFailure;
                }, retryDelayMilliseconds: 0),
                () => remainingCleanupRan = true)));

        Assert.Equal(5, attempts);
        Assert.True(remainingCleanupRan);
        if (assertionFails)
        {
            var combined = Assert.IsType<AggregateException>(failure);
            Assert.Equal(new Exception[] { assertionFailure, cleanupFailure }, combined.InnerExceptions);
        }
        else Assert.Same(cleanupFailure, failure);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void ClipboardRestoreStopsRetryingAfterSuccess(int failuresBeforeSuccess)
    {
        var attempts = 0;
        WindowsStateTestScope.RetryClipboardRestore(() =>
        {
            if (attempts++ < failuresBeforeSuccess) throw new ExternalException("Clipboard busy");
        }, retryDelayMilliseconds: 0);
        Assert.Equal(failuresBeforeSuccess + 1, attempts);
    }

    [Fact]
    public void ExistingPlaceholderNeverWrites()
    {
        var rule = new SanitizerRule("token", "secret");
        Assert.False(Sanitizer.TryQuickSanitize(rule.Placeholder, () => new[] { rule },
            _ => throw new Exception("Must not write"), out _));
    }

    [Fact]
    public void HeaderQuickActionCopiesSavedRulesWhileSetupRemainsOnToolbar()
    {
        RunWithSavedRule(new[] { "card.md" }, (assembly, root) =>
        {
            var mainType = assembly.GetType("PromptNavigator.MainForm", true)!;
            var dialogType = assembly.GetType("PromptNavigator.SanitizerDialog", true)!;
            var path = Path.Combine(root, "card.md");
            using var form = OpenForm(assembly, root);
            var copy = (Button)mainType.GetField("copyButton", Instance)!.GetValue(form)!;
            var quick = FindButton(copy, "Sanitizer");
            Assert.Empty(quick.Text);
            Assert.NotNull(quick.Image);
            var setup = Descendants(form).OfType<ToolStrip>().SelectMany(strip => strip.Items.OfType<ToolStripButton>())
                .Single(button => button.AccessibleName == "Settings");
            Assert.Equal("Settings", setup.ToolTipText);
            Assert.NotNull(setup.Image);
            var editor = (RichTextBox)mainType.GetField("cardPane", Instance)!.GetValue(form)!;
            editor.Text = "Unsaved synthetic-secret";
            editor.Select(2, 4);
            Clipboard.SetText("sentinel");
            quick.PerformClick();
            Assert.Equal("Unsaved " + Placeholder, Clipboard.GetText());
            Assert.Equal("Unsaved " + Placeholder, editor.Text);
            Assert.Equal("Unsaved synthetic-secret", PromptText(editor));
            Assert.Equal(0, editor.SelectionStart);
            Assert.Equal(0, editor.SelectionLength);
            Assert.Equal(OriginalCard, File.ReadAllText(path));
            var status = (ToolStripStatusLabel)mainType.GetField("fileStatusLabel", Instance)!.GetValue(form)!;
            Assert.Equal("Sanitized and copied", status.Text);
            Assert.DoesNotContain(Application.OpenForms.Cast<Form>(), window => window.GetType() == dialogType);

            // The separate setup button still opens the configuration dialog.
            var setupOpened = false;
            using var timer = new System.Windows.Forms.Timer { Interval = 25 };
            timer.Tick += (_, _) =>
            {
                var dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(window => window.GetType() == dialogType);
                if (dialog is null) return;
                setupOpened = true;
                dialog.Close();
            };
            timer.Start();
            setup.PerformClick();
            timer.Stop();
            Assert.True(setupOpened);
            editor.Text = "Original card";
            form.Close();
        });
    }

    [Fact]
    public void EditorViewSurvivesSaveEndsOnCardChangeAndShowsFeedbackOnlyOnSuccess()
    {
        RunWithSavedRule(new[] { "a-card.md", "b-card.md" }, (assembly, root) =>
        {
            var mainType = assembly.GetType("PromptNavigator.MainForm", true)!;
            using var form = OpenForm(assembly, root);
            var copy = (Button)mainType.GetField("copyButton", Instance)!.GetValue(form)!;
            // One toggle performs both actions; it names and shows the action its next click performs.
            var quick = FindButton(copy, "Sanitizer");
            var editor = (RichTextBox)mainType.GetField("cardPane", Instance)!.GetValue(form)!;
            var comic = MainFormTestHarness.SuccessComic(form);
            var status = (ToolStripStatusLabel)mainType.GetField("fileStatusLabel", Instance)!.GetValue(form)!;
            var first = Path.Combine(root, "a-card.md");
            Assert.Equal("Original card", editor.Text);
            Assert.DoesNotContain(copy.Parent!.Controls.OfType<Button>(), b => b.AccessibleName == "De-Sanitize");
            Assert.True(quick.Enabled);
            var sanitizeImage = quick.Image;
            Assert.NotNull(sanitizeImage);

            // De-Sanitize without a session reports and shows no comic.
            mainType.GetMethod("DesanitizeCurrentPrompt", Instance)!.Invoke(form, null);
            Assert.False(comic.Visible);
            Assert.Contains("not sanitized", status.Text);

            // Success shows the sanitized comic inside the editor frame and swaps the toggle artwork.
            editor.Text = "Keep synthetic-secret here";
            quick.PerformClick();
            Assert.Equal("De-Sanitize", quick.AccessibleName);
            Assert.NotNull(quick.Image);
            Assert.NotSame(sanitizeImage, quick.Image);
            AssertComicFits(comic, "Sanitized");

            // At minimum size the toggle de-sanitizes, then sanitizes again, with both comics fitting.
            form.Size = form.MinimumSize;
            Application.DoEvents();
            quick.PerformClick();
            Assert.Equal("Sanitizer", quick.AccessibleName);
            Assert.Same(sanitizeImage, quick.Image);
            AssertComicFits(comic, "De-sanitized");
            quick.PerformClick();
            AssertComicFits(comic, "Sanitized");
            Assert.Equal("Keep " + Placeholder + " here", editor.Text);

            // Surrounding text stays editable; the save writes originals and keeps the sanitized view.
            editor.Select(editor.TextLength, 0);
            editor.SelectedText = "!";
            Assert.True((bool)mainType.GetMethod("TrySaveCurrentCard", Instance)!.Invoke(form, null)!);
            Assert.Equal("Keep synthetic-secret here!", File.ReadAllText(first).Replace("\r\n", "\n"));
            Assert.Equal("Keep " + Placeholder + " here!", editor.Text);
            Assert.Equal("De-Sanitize", quick.AccessibleName);

            // De-Sanitize restores originals with the retained edit and shows its comic.
            quick.PerformClick();
            Assert.Equal("Keep synthetic-secret here!", editor.Text);
            Assert.Equal("Sanitizer", quick.AccessibleName);
            AssertComicFits(comic, "De-sanitized");

            // A failed sanitization (text already holding a placeholder) shows no comic and leaves the editor unchanged.
            editor.Text = "Already " + Placeholder;
            quick.PerformClick();
            Assert.False(comic.Visible);
            Assert.Equal("Already " + Placeholder, editor.Text);
            Assert.Equal("Sanitizer", quick.AccessibleName);

            // A card holding no saved secret reports it, shows no comic and keeps the toggle on Sanitize.
            editor.Text = "Nothing secret here";
            quick.PerformClick();
            Assert.False(comic.Visible);
            Assert.Contains("No saved secret", status.Text);
            Assert.Equal("Nothing secret here", editor.Text);
            Assert.Equal("Sanitizer", quick.AccessibleName);

            // A card change ends the session; the next card never shows the first card's placeholders.
            editor.Text = "Keep synthetic-secret here!";
            quick.PerformClick();
            Assert.Equal("De-Sanitize", quick.AccessibleName);
            mainType.GetMethod("MoveCard", Instance)!.Invoke(form, new object[] { 1 });
            Assert.Equal("Original card", editor.Text);
            Assert.Equal("Sanitizer", quick.AccessibleName);
            Assert.Equal("Keep synthetic-secret here!", File.ReadAllText(first).Replace("\r\n", "\n"));
            mainType.GetMethod("MoveCard", Instance)!.Invoke(form, new object[] { -1 });
            Assert.Equal("Keep synthetic-secret here!", editor.Text);
            Assert.Equal("Sanitizer", quick.AccessibleName);
            form.Close();
        });
    }

    private const string OriginalCard = "tags:\n\nOriginal card";

    private static void AssertComicFits(PictureBox comic, string name)
    {
        Assert.True(comic.Visible);
        Assert.NotNull(comic.Image);
        Assert.Equal(name, comic.AccessibleName);
        Assert.True(comic.Parent!.ClientRectangle.Contains(comic.Bounds),
            $"Comic {comic.Bounds} exceeds frame {comic.Parent.ClientRectangle}.");
    }

    private static string PromptText(RichTextBox editor) =>
        (string)editor.GetType().GetProperty("PromptText")!.GetValue(editor)!;

    private static Button FindButton(Control sibling, string name) =>
        sibling.Parent!.Controls.OfType<Button>().Single(button => button.AccessibleName == name);

    private static IEnumerable<Control> Descendants(Control control) => control.Controls.Cast<Control>()
        .SelectMany(child => new[] { child }.Concat(Descendants(child)));

    private static Form OpenForm(Assembly assembly, string root)
    {
        var repo = assembly.GetType("PromptNavigator.CardRepository", true)!;
        var cards = repo.GetMethod("Load")!.Invoke(Activator.CreateInstance(repo), new object[] { root });
        var form = SanitizerTestRules.CreateMainForm(root, cards!);
        form.Show();
        Application.DoEvents();
        return form;
    }

    /// <summary>Uses one synthetic rule and temporary cards on an STA thread. Restores the clipboard.</summary>
    private static void RunWithSavedRule(string[] cardNames, Action<Assembly, string> body)
    {
        MainFormTestHarness.RunInSta(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "quick-sanitize-" + Guid.NewGuid());
            Directory.CreateDirectory(root);
            var assembly = Assembly.Load("PromptFlipper");
            var clipboard = Clipboard.GetDataObject();
            WindowsStateTestScope.RunWithCleanup(() =>
            {
                foreach (var name in cardNames) File.WriteAllText(Path.Combine(root, name), OriginalCard);
                body(assembly, root);
            }, () => WindowsStateTestScope.RestoreClipboard(clipboard),
                () => Directory.Delete(root, true));
        }, "Quick Sanitize Windows run timed out.");
    }
}

internal static class WindowsStateTestScope
{
    internal static void RunWithCleanup(Action body, params Action[] cleanupActions)
    {
        var failures = new List<Exception>();
        void Capture(Action action)
        {
            try { action(); }
            catch (Exception exception)
            {
                failures.Add(exception is TargetInvocationException { InnerException: { } inner } ? inner : exception);
            }
        }

        Capture(body);
        foreach (var cleanup in cleanupActions) Capture(cleanup);
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException("Windows-state test and cleanup failures.", failures);
    }

    internal static void RestoreClipboard(IDataObject? original) => RetryClipboardRestore(() =>
    {
        if (original is not null) Clipboard.SetDataObject(original, true, 10, 100);
        else Clipboard.Clear();
    });

    internal static void RetryClipboardRestore(Action restore, int retryDelayMilliseconds = 100)
    {
        const int maxAttempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                restore();
                return;
            }
            catch (ExternalException) when (attempt < maxAttempts)
            {
                Thread.Sleep(retryDelayMilliseconds);
            }
        }
    }
}
