using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using static PromptFlipper.Tests.MainFormTestHarness;
using System.Windows.Forms;
using Xunit;

namespace PromptFlipper.Tests;

[Collection("Quick sanitize Windows state")]
public sealed class ConfirmationFeedbackTests
{
    [Fact]
    public void SuccessfulActionsConfirmVisiblyAndNudgeWithoutDrift()
    {
        RunInSta(() =>
        {
            using var fixture = new MainFormTestHarness();
            var root = fixture.Root;
            var clipboard = Clipboard.GetDataObject();
            try
            {
                File.WriteAllText(Path.Combine(root, "card.md"), "tags:\n\nKeep synthetic-secret here");
                using var form = SanitizerTestRules.CreateMainForm(root, fixture.LoadCards());
                form.Show();
                Application.DoEvents();
                var copy = CopyButton(form);
                var toggle = SanitizeToggle(form);
                var editor = CardPane(form);
                var comic = SuccessComic(form);
                var status = FileStatusLabel(form);
                var origin = form.Location;

                copy.PerformClick();
                Assert.Equal("Keep synthetic-secret here", Clipboard.GetText());
                Assert.Equal("\u2713", copy.Text);
                Assert.Null(copy.Image);
                Assert.Equal("Copied to clipboard", status.Text);
                if (IsSuccessNudging(form)) Assert.NotEqual(origin, form.Location);
                Capture(form, "copy");
                Pump(250);
                Assert.False(IsSuccessNudging(form));
                Assert.Equal(origin, form.Location);
                Pump(1500);
                Assert.Empty(copy.Text);
                Assert.NotNull(copy.Image);

                foreach (var size in new[] { form.Size, form.MinimumSize })
                {
                    form.Size = size;
                    Application.DoEvents();
                    origin = form.Location;
                    toggle.PerformClick();
                    Assert.Equal("De-Sanitize", toggle.AccessibleName);
                    Assert.Equal("Keep {{PromptFlipper.Sanitizer.token}} here", editor.Text);
                    Assert.True(comic.Visible);
                    Assert.NotNull(comic.Image);
                    Assert.Equal("Sanitized", comic.AccessibleName);
                    Assert.True(comic.Parent!.ClientRectangle.Contains(comic.Bounds));
                    Assert.Equal(0, comic.Parent.Controls.GetChildIndex(comic));
                    Capture(form, "sanitized-" + size.Width);
                    Pump(250);
                    Assert.Equal(origin, form.Location);
                    toggle.PerformClick();
                    Assert.Equal("Keep synthetic-secret here", editor.Text);
                    Assert.Equal("Sanitizer", toggle.AccessibleName);
                    Assert.True(comic.Visible);
                    Assert.Equal("De-sanitized", comic.AccessibleName);
                    Capture(form, "restored-" + size.Width);
                    Pump(250);
                    Assert.Equal(origin, form.Location);
                }

                // Rapid repeated confirmations restart from the same resting position.
                copy.PerformClick();
                copy.PerformClick();
                Pump(250);
                Assert.Equal(origin, form.Location);
                copy.PerformClick();
                HideSuccessComic(form);
                Assert.False(IsSuccessNudging(form));
                Assert.Equal(origin, form.Location);
                Assert.Empty(copy.Text);
                Assert.NotNull(copy.Image);
                copy.PerformClick();
                var moved = new Point(origin.X + 20, origin.Y + 10);
                form.Location = moved;
                Pump(250);
                Assert.Equal(moved, form.Location);
                Assert.False(IsSuccessNudging(form));
                form.WindowState = FormWindowState.Maximized;
                Application.DoEvents();
                copy.PerformClick();
                Assert.False(IsSuccessNudging(form));
                Assert.Equal(FormWindowState.Maximized, form.WindowState);
                form.WindowState = FormWindowState.Normal;
                HideSuccessComic(form);

                // No-match and invalid input never announce a sanitization or nudge.
                editor.Text = "No matching values";
                toggle.PerformClick();
                Assert.False(comic.Visible);
                Assert.False(IsSuccessNudging(form));
                Assert.Equal("Sanitizer", toggle.AccessibleName);
                editor.Text = "{{PromptFlipper.Sanitizer.token}}";
                toggle.PerformClick();
                Assert.False(comic.Visible);
                Assert.False(IsSuccessNudging(form));
                editor.Text = "Keep synthetic-secret here";
                toggle.PerformClick();
                Assert.True(comic.Visible);
                Pump(1750);
                Assert.False(comic.Visible);
                toggle.PerformClick();
                Assert.True(comic.Visible);
                form.Close();
                Assert.False(IsSuccessNudging(form));
                Assert.True(comic.IsDisposed);
                Assert.Null(comic.Image);
            }
            finally
            {
                if (clipboard is null) Clipboard.Clear();
                else Clipboard.SetDataObject(clipboard, true, 10, 100);

            }
        }, timeoutMessage: "Confirmation UI run timed out.");
    }

    private static void Pump(int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
    }

    private static void Capture(Form form, string name)
    {
        var directory = Environment.GetEnvironmentVariable("PROMPT_FLIPPER_FEEDBACK_CAPTURE");
        if (string.IsNullOrEmpty(directory)) return;
        Directory.CreateDirectory(directory);
        form.TopMost = true;
        form.Activate();
        form.BringToFront();
        Pump(80);
        form.Refresh();
        using var bitmap = new Bitmap(form.Width, form.Height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(form.Location, Point.Empty, bitmap.Size);
        bitmap.Save(Path.Combine(directory, name + ".png"), ImageFormat.Png);
    }
}
