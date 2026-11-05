using System.Drawing;
using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using Xunit;

namespace PromptFlipper.Tests;

[Collection("Quick sanitize Windows state")]
public sealed class SanitizerReviewLayoutTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string Source = "synthetic-secret public-value user@example.org\r\n" +
        "A second line remains available in the complete review.";

    [Fact]
    public void ReviewUsesAllSavedRulesAndClearsAfterRulesOrPiiModeChange()
    {
        WithDialog(Source, (dialog, rules) =>
        {
            var review = Field<TextBox>(dialog, "reviewText");
            var button = Field<Button>(dialog, "piiReviewButton");
            Assert.True(review.ReadOnly && review.Multiline);
            Assert.Equal(ScrollBars.Both, review.ScrollBars);
            Assert.True(button.Enabled);
            Field<CheckBox>(dialog, "privateOnly").Checked = true;
            Assert.Equal("public", Assert.Single(Field<ListBox>(dialog, "ruleNames").Items.Cast<string>()));
            Assert.Contains("1 private entry hidden", Field<Label>(dialog, "status").Text);
            button.PerformClick();
            var expected = Expected(rules);
            Assert.Equal(expected, review.Text);
            Assert.DoesNotContain("synthetic-secret", review.Text);
            Assert.Contains("{{PromptFlipper.Sanitizer.secret}}", review.Text);
            Assert.Contains("A second line", review.Text);

            Field<CheckBox>(dialog, "piiDetection").Checked = true;
            Assert.Empty(review.Text);
            button.PerformClick();
            Assert.Equal(expected, review.Text);
            Assert.Contains("PII detected:", Field<Label>(dialog, "reviewStatus").Text);
            Assert.DoesNotContain("user@example.org", Field<Label>(dialog, "reviewStatus").Text);

            Replace(dialog, ("changed", "public-value", true));
            Assert.Empty(review.Text);
            Assert.Empty(Field<Label>(dialog, "reviewStatus").Text);
            button.PerformClick();
            expected = Expected(rules);
            Assert.Equal(expected, review.Text);

            Replace(dialog, ("invalid", "", true));
            button.PerformClick();
            Assert.Empty(review.Text);
            Assert.Contains("Review unavailable", Field<Label>(dialog, "reviewStatus").Text);
            Replace(dialog);
            button.PerformClick();
            Assert.Empty(review.Text);
            Assert.Contains("Add a secret", Field<Label>(dialog, "reviewStatus").Text);
        });
    }

    [Fact]
    public void MissingCardHasNoReviewAndEscapeDismissesSettings()
    {
        WithDialog(null, (dialog, _) =>
        {
            Assert.False(Field<Button>(dialog, "piiReviewButton").Enabled);
            Invoke(dialog, "ShowReview");
            Assert.Empty(Field<TextBox>(dialog, "reviewText").Text);
            Assert.Equal("Open a card to review.", Field<Label>(dialog, "reviewStatus").Text);
            Assert.True((bool)Invoke(dialog, "ProcessCmdKey", new Message(), Keys.Escape)!);
            Assert.False(dialog.Visible);
        });
    }

    [Fact]
    public void FramedFieldsAndActionsFitTheAnnotatedLayoutAtBothSizes()
    {
        WithDialog(Source, (dialog, _) =>
        {
            var list = Field<ListBox>(dialog, "ruleNames");
            var search = Field<TextBox>(dialog, "ruleSearch");
            var editor = Field<TextBox>(dialog, "valueEditor");
            var review = Field<TextBox>(dialog, "reviewText");
            var reviewButton = Field<Button>(dialog, "piiReviewButton");
            var actions = Descendants(dialog).OfType<Button>()
                .Where(button => button.Text is "&Import..." or "Re&move" or "&Save").ToArray();
            Assert.Equal(3, actions.Length);
            Assert.DoesNotContain(Descendants(dialog).OfType<Button>(), button => button.Text == "Close");
            list.SelectedIndex = 0;
            reviewButton.PerformClick();
            foreach (var minimum in new[] { false, true })
            {
                if (minimum) dialog.Size = dialog.MinimumSize;
                Application.DoEvents();
                Rectangle Screen(Control control) => control.RectangleToScreen(control.ClientRectangle);
                Assert.True(list.ClientSize.Height >= list.ItemHeight * 4);
                Assert.True(review.ClientSize.Height >= review.Font.Height * 4);
                foreach (var action in actions)
                {
                    Assert.True(Screen(action).Top >= Screen(list.Parent!).Bottom);
                    Assert.True(Screen(action).Right <= Screen(list.Parent!).Right);
                    Assert.True(action.Width >= action.PreferredSize.Width);
                }
                Assert.True(Screen(review).Left >= Screen(list.Parent!).Right);
                Assert.True(Screen(reviewButton).Top >= Screen(review.Parent!).Bottom);
                Assert.True(Screen(reviewButton).Right > Screen(review).Left + review.Width / 2);
                foreach (var field in new Control[] { list, search, editor, review })
                {
                    var frame = Assert.IsType<Panel>(field.Parent);
                    Assert.True(frame.Padding.All > 0);
                    Assert.True(frame.ClientRectangle.Contains(field.Bounds));
                    using var bitmap = new Bitmap(frame.Width, frame.Height);
                    frame.DrawToBitmap(bitmap, frame.ClientRectangle);
                    var border = frame.ContainsFocus ? PromptNavigator.ToolbarArtwork.Accent : SystemColors.ControlDark;
                    Assert.Equal(border.ToArgb(), bitmap.GetPixel(frame.Width / 2, 0).ToArgb());
                    Assert.Equal(border.ToArgb(), bitmap.GetPixel(0, frame.Height / 2).ToArgb());
                    Assert.Equal(border.ToArgb(), bitmap.GetPixel(frame.Width - 1, frame.Height / 2).ToArgb());
                    Assert.Equal(border.ToArgb(), bitmap.GetPixel(frame.Width / 2, frame.Height - 1).ToArgb());
                    for (Control? control = frame; control?.Parent is { } parent; control = parent)
                        Assert.True(parent.ClientRectangle.Contains(control.Bounds), $"{field.AccessibleName} is clipped by {parent.GetType().Name}.");
                }
                using var image = new Bitmap(dialog.Width, dialog.Height);
                dialog.DrawToBitmap(image, new Rectangle(Point.Empty, dialog.Size));
                image.Save(Path.Combine(Path.GetTempPath(), $"PromptFlipper-task110-{(minimum ? "minimum" : "default")}.png"));
            }
        });
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static T Field<T>(Form dialog, string name) => (T)dialog.GetType().GetField(name, Instance)!.GetValue(dialog)!;
    private static object? Invoke(Form dialog, string name, params object[] args) =>
        dialog.GetType().GetMethod(name, Instance)!.Invoke(dialog, args);

    private static string Expected(IList rules)
    {
        var copies = rules.Cast<object>().Select(rule => new SanitizerRule(
            (string)rule.GetType().GetProperty("Name")!.GetValue(rule)!,
            (string)rule.GetType().GetProperty("Value")!.GetValue(rule)!,
            (bool)rule.GetType().GetProperty("Private")!.GetValue(rule)!));
        Assert.True(Sanitizer.TrySanitize(Source, copies, out var expected));
        return expected!;
    }

    private static void Replace(Form dialog, params (string Name, string Value, bool Private)[] values)
    {
        var type = dialog.GetType().Assembly.GetType("PromptFlipper.SanitizerRule", true)!;
        var rules = Array.CreateInstance(type, values.Length);
        for (var index = 0; index < values.Length; index++)
            rules.SetValue(Activator.CreateInstance(type, values[index].Name, values[index].Value, values[index].Private), index);
        Invoke(dialog, "ReplaceRules", rules);
    }

    private static void WithDialog(string? source, Action<Form, IList> body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var dialog = SanitizerTestRules.CreateDialog(source, "Synthetic card");
                Replace(dialog, ("secret", "synthetic-secret", true), ("public", "public-value", false));
                var rules = Field<IList>(dialog, "rules");
                Invoke(dialog, "RefreshRuleList", null!, true);
                dialog.Show();
                Application.DoEvents();
                body(dialog, rules);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Settings review run timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
