using Xunit;
using System.Security.Cryptography;
using System.Runtime.ExceptionServices;

namespace PromptFlipper.Tests;

[Collection("Quick sanitize Windows state")]
public sealed class SanitizerTests
{
    [Fact]
    public void ControlsFitSettingsAtDefaultAndMinimumSize()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var assembly = System.Reflection.Assembly.Load("PromptFlipper");
                var type = assembly.GetType("PromptNavigator.SanitizerDialog", true)!;
                var ruleType = assembly.GetType("PromptFlipper.SanitizerRule", true)!;
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                using var dialog = SanitizerTestRules.CreateDialog("synthetic-secret", "Artwork sample");
                var rules = (System.Collections.IList)type.GetField("rules", flags)!.GetValue(dialog)!;
                rules.Clear();
                rules.Add(Activator.CreateInstance(ruleType, "sample", "synthetic-secret"));
                type.GetMethod("RefreshRuleList", flags)!.Invoke(dialog, new object?[] { null, true });
                var list = (System.Windows.Forms.ListBox)type.GetField("ruleNames", flags)!.GetValue(dialog)!;
                list.SelectedIndex = 0;

                void Inspect(System.Windows.Forms.Form form, string[] captions)
                {
                    form.Show();
                    System.Windows.Forms.Application.DoEvents();
                    Assert.NotNull(form.Icon);
                    var buttons = Descendants(form).OfType<System.Windows.Forms.Button>().ToArray();
                    foreach (var caption in captions) Assert.Single(buttons, button => button.Text == caption);
                    foreach (var minimum in new[] { false, true })
                    {
                        if (minimum) form.Size = form.MinimumSize;
                        System.Windows.Forms.Application.DoEvents();
                        // The findings buttons stay hidden until a Review finds something.
                        foreach (var button in buttons.Where(button => button.Visible))
                        {
                            Assert.True(button.TabStop && button.UseMnemonic);
                            Assert.True(button.Parent!.ClientRectangle.Contains(button.Bounds),
                                $"Settings: {button.Text} exceeds its action row at DPI {form.DeviceDpi}, minimum={minimum}.");
                            Assert.True(form.RectangleToScreen(form.ClientRectangle).Contains(button.RectangleToScreen(button.ClientRectangle)));
                            Assert.True(button.Width >= button.PreferredSize.Width,
                                $"{button.Text}: width {button.Width}, preferred {button.PreferredSize.Width}, minimum={minimum}.");
                            if (button.Image is not { } artwork) continue;
                            Assert.Equal(artwork.Width, artwork.Height);
                            Assert.True(button.Height >= artwork.Height);
                        }
                        using var image = new System.Drawing.Bitmap(form.Width, form.Height);
                        form.DrawToBitmap(image, new System.Drawing.Rectangle(System.Drawing.Point.Empty, form.Size));
                        image.Save(Path.Combine(Path.GetTempPath(),
                            $"PromptFlipper-task107-settings-{form.DeviceDpi}dpi-{(minimum ? "minimum" : "default")}.png"));
                    }
                    var editLabel = Descendants(form).OfType<System.Windows.Forms.Label>()
                        .Single(label => label.Text == "&Add / Edit");
                    Assert.True((bool)typeof(System.Windows.Forms.Label).GetMethod("ProcessMnemonic", flags)!
                        .Invoke(editLabel, new object[] { 'a' })!);
                    Assert.True(((System.Windows.Forms.TextBox)type.GetField("valueEditor", flags)!.GetValue(form)!).Focused);
                    Assert.Single(list.SelectedItems);
                    form.Hide();
                }

                Inspect(dialog, ["&Import...", "Re&move", "&Save", "Re&view"]);
                Assert.DoesNotContain(Descendants(dialog).OfType<System.Windows.Forms.Button>(), b => b.Text == "Close");
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Settings layout Windows run timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void HelpSearchListsOnlyTheMatchingTopic()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var type = System.Reflection.Assembly.Load("PromptFlipper").GetType("PromptNavigator.HelpWindow", true)!;
                using var window = (System.Windows.Forms.Form)Activator.CreateInstance(type, true)!;
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var search = (System.Windows.Forms.TextBox)type.GetField("search", flags)!.GetValue(window)!;
                var topics = (System.Windows.Forms.TreeView)type.GetField("topics", flags)!.GetValue(window)!;
                Assert.Equal("Search help", search.AccessibleName);
                Assert.Equal("Search...", search.PlaceholderText);
                search.Text = "PERMANENT";
                Assert.Equal("Trash", Assert.Single(topics.Nodes.Cast<System.Windows.Forms.TreeNode>()).Text);
                Assert.Equal("Trash", topics.SelectedNode?.Text);
                search.Clear();
                Assert.Equal("Cards", Assert.Single(topics.Nodes.Cast<System.Windows.Forms.TreeNode>()).Text);
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Help search Windows run timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void CardAndPlaceholderSearchUseTheSharedMatcher()
    {
        // Two words in different fields match; a word absent from every field does not.
        Assert.True(TextSearch.Matches("alpha BETA", ["alpha one", "beta two"]));
        Assert.False(TextSearch.Matches("alpha gamma", ["alpha one", "beta two"]));
        // Highlight ranges: every occurrence of every word, case-insensitive.
        Assert.Equal([(0, 5), (10, 5), (6, 3)], TextSearch.Ranges("Alpha one alpha", "alpha one").ToArray());
        Assert.Empty(TextSearch.Ranges("text", ""));
        var rules = new[] { new SanitizerRule("alpha", "beta-value"), new SanitizerRule("alpha2", "other-secret") };
        Assert.Equal("alpha", string.Join(",", Sanitizer.FilterRules(rules, "alpha beta").Select(rule => rule.Name)));

        var app = System.Reflection.Assembly.Load("PromptFlipper");
        var cardType = app.GetType("PromptNavigator.Card", true)!;
        var repositoryType = app.GetType("PromptNavigator.CardRepository", true)!;
        var root = Directory.CreateTempSubdirectory("pf-search-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "one.md"), "alpha\nbeta text");
            File.WriteAllText(Path.Combine(root, "two.md"), "alpha only");
            var repository = Activator.CreateInstance(repositoryType, true)!;
            var cards = repositoryType.GetMethod("Load")!.Invoke(repository, [root])!;
            var hits = (System.Collections.IEnumerable)repositoryType.GetMethod("Search")!.Invoke(repository, [cards, "alpha beta"])!;
            var names = hits.Cast<object>().Select(card => Path.GetFileName((string)cardType.GetProperty("FilePath",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!.GetValue(card)!));
            Assert.Equal(new[] { "one.md" }, names);
        }
        finally { Directory.Delete(root, true); }
    }

    private static IEnumerable<System.Windows.Forms.Control> Descendants(System.Windows.Forms.Control parent)
    {
        foreach (System.Windows.Forms.Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void BatchRemovalUsesVisibleSelectionAndPersistsOnce(bool multiple, bool filtered, bool fail)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var assembly = System.Reflection.Assembly.Load("PromptFlipper");
                var type = assembly.GetType("PromptNavigator.SanitizerDialog", true)!;
                var ruleType = assembly.GetType("PromptFlipper.SanitizerRule", true)!;
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var callbackType = typeof(Action<>).MakeGenericType(typeof(IEnumerable<>).MakeGenericType(ruleType));
                var writes = 0;
                string[] saved = [];
                string Name(object rule) => (string)ruleType.GetProperty("Name")!.GetValue(rule)!;
                Action<object> persist = candidate =>
                {
                    writes++;
                    if (fail) throw new IOException("Synthetic persistence failure");
                    saved = ((System.Collections.IEnumerable)candidate).Cast<object>().Select(Name).ToArray();
                };
                using var dialog = SanitizerTestRules.CreateDialog(saveDelegate: Delegate.CreateDelegate(callbackType, persist.Target, persist.Method));
                object Field(string name) => type.GetField(name, flags)!.GetValue(dialog)!;
                void Invoke(string name, params object?[] args) => type.GetMethod(name, flags)!.Invoke(dialog, args);
                var rules = (System.Collections.IList)Field("rules");
                rules.Clear();
                foreach (var name in new[] { "match-a", "match-b", "public", "other" })
                    rules.Add(Activator.CreateInstance(ruleType, name, "synthetic-" + name));
                Invoke("RefreshRuleList", new object?[] { null, true });
                dialog.Show();
                System.Windows.Forms.Application.DoEvents();
                var list = (System.Windows.Forms.ListBox)Field("ruleNames");
                var remove = (System.Windows.Forms.Button)Field("removeButton");
                var editor = (System.Windows.Forms.TextBox)Field("valueEditor");
                var add = Descendants(dialog).OfType<System.Windows.Forms.Button>().Single(b => b.Text == "&Save");
                var import =Descendants(dialog).OfType<System.Windows.Forms.Button>().Single(b => b.Text == "&Import...");
                Assert.Equal(System.Windows.Forms.SelectionMode.MultiExtended, list.SelectionMode);
                Assert.False(remove.Enabled);
                Invoke("RemoveSelectedRule");
                Assert.Equal(0, writes);
                if (filtered)
                {
                    ((System.Windows.Forms.TextBox)Field("ruleSearch")).Text = "match";
                    Assert.Equal(2, list.Items.Count);
                }
                list.SetSelected(0, true);
                Assert.True(editor.Enabled);
                Assert.Equal("synthetic-match-a", editor.Text);
                if (multiple)
                {
                    list.SetSelected(1, true);
                    Assert.False(editor.Enabled);
                    add.PerformClick();
                    Assert.Equal(0, writes);
                }
                foreach (var minimum in new[] { false, true })
                {
                    if (minimum) dialog.Size = dialog.MinimumSize;
                    System.Windows.Forms.Application.DoEvents();
                    Assert.True(list.ClientSize.Height >= list.ItemHeight * 4,
                        $"Placeholder list cannot display four rows at {dialog.Size}.");
                    Assert.Same(import.Parent, remove.Parent);
                    Assert.Equal(import.Parent!.Controls.GetChildIndex(import) + 1, remove.Parent!.Controls.GetChildIndex(remove));
                    Assert.True(remove.Left >= import.Right && remove.Top == import.Top);
                    Assert.True(remove.Parent.ClientRectangle.Contains(remove.Bounds));
                    Assert.True(dialog.RectangleToScreen(dialog.ClientRectangle).Contains(remove.RectangleToScreen(remove.ClientRectangle)));
                }
                remove.PerformClick();
                Assert.Equal(1, writes);
                var remaining = rules.Cast<object>().Select(Name).ToArray();
                if (fail)
                {
                    Assert.Equal(4, remaining.Length);
                    Assert.Equal(2, list.SelectedItems.Count);
                    Assert.True(remove.Enabled);
                    Assert.Contains("Nothing changed", ((System.Windows.Forms.Label)Field("status")).Text);
                }
                else
                {
                    Assert.Equal(multiple ? new[] { "public", "other" } : new[] { "match-b", "public", "other" }, remaining);
                    Assert.Equal(remaining, saved);
                    Assert.False(remove.Enabled);
                    Assert.Empty(list.SelectedItems);
                    Assert.True(editor.Enabled);
                    // Editing still works after a batch removal, including hidden survivors.
                    ((System.Windows.Forms.TextBox)Field("ruleSearch")).Clear();
                    list.SetSelected(0, true);
                    editor.Text = "synthetic-edited";
                    add.PerformClick();
                    Assert.Equal(2, writes);
                    Assert.Contains(rules.Cast<object>(), rule => (string)ruleType.GetProperty("Value")!.GetValue(rule)! == "synthetic-edited");
                }
                dialog.Close();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Batch removal Windows run timed out.");
        Assert.Null(failure);
    }

    [Theory]
    [InlineData("TOKEN", "token")]
    [InlineData("public-value", "public")]
    [InlineData("hidden-value", "token")]
    [InlineData("", "token,public,other")]
    public void SearchMatchesNamesAndValuesAndLeavesRulesUnchanged(string search, string expected)
    {
        var rules = new[] { new SanitizerRule("token", "hidden-value"),
            new SanitizerRule("public", "public-value"), new SanitizerRule("other", "third-value") };
        Assert.True(Sanitizer.TrySanitize("hidden-value public-value third-value", rules, out var before));
        Assert.Equal(expected, string.Join(",", Sanitizer.FilterRules(rules, search).Select(rule => rule.Name)));
        Assert.Equal(3, rules.Length);
        Assert.True(Sanitizer.TrySanitize("hidden-value public-value third-value", rules, out var after));
        Assert.Equal(before, after);
    }

    [Theory]
    [InlineData("abcdefg", false, "abcdefg (sanitize-001)")]
    [InlineData("abcdefg", true, "******** (sanitize-001)")]
    public void ListTextLeadsWithTheFullValueOrTheMask(string value, bool hidden, string expected)
    {
        Assert.Equal(expected, new SanitizerRule("sanitize-001", value).ListText(hidden));
    }

    [Fact]
    public void SettingsControlsAreLabelledAndFitAtBothSizes()
    {
        RunSta("Settings controls", () =>
        {
            var type = System.Reflection.Assembly.Load("PromptFlipper").GetType("PromptNavigator.SanitizerDialog", true)!;
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            using var dialog = SanitizerTestRules.CreateDialog(load: () => [new SanitizerRule("token", "hidden-value")]);
            object Field(string name) => type.GetField(name, flags)!.GetValue(dialog)!;
            dialog.Show();
            System.Windows.Forms.Application.DoEvents();
            var list = (System.Windows.Forms.ListBox)Field("ruleNames");
            var search = (System.Windows.Forms.TextBox)Field("ruleSearch");
            var hideAll = (System.Windows.Forms.CheckBox)Field("hideAll");
            var sort = (System.Windows.Forms.CheckBox)Field("sortByValue");
            string Caption(System.Windows.Forms.Control control) => control.Text.Replace("&", "");
            Assert.Equal("Settings", dialog.Text);
            Assert.Equal("Hide all", Caption(hideAll));
            Assert.Equal("Hide all secret values", hideAll.AccessibleName);
            Assert.Equal("Sort A-Z", Caption(sort));
            Assert.Equal("Search...", search.PlaceholderText);
            Assert.DoesNotContain(Descendants(dialog).OfType<System.Windows.Forms.CheckBox>(),
                box => Caption(box) is "Private" or "Hide private");
            foreach (var minimum in new[] { false, true })
            {
                if (minimum) dialog.Size = dialog.MinimumSize;
                System.Windows.Forms.Application.DoEvents();
                foreach (var control in new System.Windows.Forms.Control[] { search, list, hideAll, sort })
                {
                    Assert.True(control.Visible && control.Width > 0 && control.Height > 0);
                    Assert.True(dialog.RectangleToScreen(dialog.ClientRectangle).Contains(control.RectangleToScreen(control.ClientRectangle)),
                        $"Control {control.AccessibleName} {control.RectangleToScreen(control.ClientRectangle)} is clipped at {dialog.Size} " +
                        $"{dialog.RectangleToScreen(dialog.ClientRectangle)}, minimum {dialog.MinimumSize}, dpi {dialog.DeviceDpi}.");
                }
                Assert.True(hideAll.PointToScreen(System.Drawing.Point.Empty).Y >=
                    list.PointToScreen(new System.Drawing.Point(0, list.Height)).Y);
            }
            dialog.Close();
        });
    }

    [Fact]
    public void ImportShowsEveryValueAndHideAllMasksAndRestoresWithoutChangingOutput()
    {
        RunSta("Import and Hide all", () =>
        {
            var assembly = System.Reflection.Assembly.Load("PromptFlipper");
            var type = assembly.GetType("PromptNavigator.SanitizerDialog", true)!;
            var ruleType = assembly.GetType("PromptFlipper.SanitizerRule", true)!;
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            using var dialog = SanitizerTestRules.CreateDialog("delta alpha charlie", "Synthetic card");
            object Field(string name) => type.GetField(name, flags)!.GetValue(dialog)!;
            dialog.Show();
            System.Windows.Forms.Application.DoEvents();
            var list = (System.Windows.Forms.ListBox)Field("ruleNames");
            var editor = (System.Windows.Forms.TextBox)Field("valueEditor");
            var hideAll = (System.Windows.Forms.CheckBox)Field("hideAll");
            var rules = (System.Collections.IList)Field("rules");
            string[] Rows() => list.Items.Cast<object>().Select(item => list.GetItemText(item)!).ToArray();
            string Sanitized()
            {
                var copies = rules.Cast<object>().Select(rule => new SanitizerRule(
                    (string)ruleType.GetProperty("Name")!.GetValue(rule)!, (string)ruleType.GetProperty("Value")!.GetValue(rule)!));
                Assert.True(Sanitizer.TrySanitize("delta alpha charlie", copies, out var output));
                return output!;
            }

            Func<IEnumerable<string>> lines = () => ["delta", "alpha", "charlie"];
            type.GetMethod("ImportFrom", flags)!.Invoke(dialog, [lines]);
            Assert.False(hideAll.Checked);
            Assert.Equal(new[] { "delta (sanitize-001)", "alpha (sanitize-002)", "charlie (sanitize-003)" }, Rows());
            list.SelectedIndex = 1;
            Assert.Equal("alpha", editor.Text);
            Assert.False(editor.UseSystemPasswordChar);

            var reviewButton = (System.Windows.Forms.Button)Field("piiReviewButton");
            var review = (System.Windows.Forms.TextBox)Field("reviewText");
            reviewButton.PerformClick();
            var reviewBefore = review.Text;
            var before = Sanitized();

            hideAll.Checked = true;
            Assert.All(Rows(), row => Assert.StartsWith("******** (", row));
            Assert.True(editor.UseSystemPasswordChar);
            Assert.Equal(before, Sanitized());
            Assert.Equal(reviewBefore, review.Text);

            hideAll.Checked = false;
            Assert.Equal(new[] { "delta (sanitize-001)", "alpha (sanitize-002)", "charlie (sanitize-003)" }, Rows());
            Assert.False(editor.UseSystemPasswordChar);
            Assert.Equal(before, Sanitized());
            dialog.Close();
        });
    }

    [Fact]
    public void SortOrdersTheListAToZByValueAndReturnsToNameOrder()
    {
        RunSta("Sort", () =>
        {
            var type = System.Reflection.Assembly.Load("PromptFlipper").GetType("PromptNavigator.SanitizerDialog", true)!;
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            using var dialog = SanitizerTestRules.CreateDialog(load: () => [new SanitizerRule("sanitize-001", "delta"),
                new SanitizerRule("sanitize-002", "Alpha"), new SanitizerRule("sanitize-003", "charlie")]);
            object Field(string name) => type.GetField(name, flags)!.GetValue(dialog)!;
            var list = (System.Windows.Forms.ListBox)Field("ruleNames");
            var sort = (System.Windows.Forms.CheckBox)Field("sortByValue");
            string[] Names() => list.Items.Cast<string>().ToArray();
            Assert.Equal(new[] { "sanitize-001", "sanitize-002", "sanitize-003" }, Names());
            sort.Checked = true;
            Assert.Equal(new[] { "sanitize-002", "sanitize-003", "sanitize-001" }, Names());
            sort.Checked = false;
            Assert.Equal(new[] { "sanitize-001", "sanitize-002", "sanitize-003" }, Names());
        });
    }

    [Fact]
    public void SaveShowsTheValueAndReportsWhenTheSearchHidesIt()
    {
        RunSta("Save and filter", () =>
        {
            var type = System.Reflection.Assembly.Load("PromptFlipper").GetType("PromptNavigator.SanitizerDialog", true)!;
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            using var dialog = SanitizerTestRules.CreateDialog(load: () => [new SanitizerRule("alpha", "synthetic-alpha")]);
            object Field(string name) => type.GetField(name, flags)!.GetValue(dialog)!;
            dialog.Show();
            System.Windows.Forms.Application.DoEvents();
            var list = (System.Windows.Forms.ListBox)Field("ruleNames");
            var search = (System.Windows.Forms.TextBox)Field("ruleSearch");
            var editor = (System.Windows.Forms.TextBox)Field("valueEditor");
            var status = (System.Windows.Forms.Label)Field("status");
            var save = Descendants(dialog).OfType<System.Windows.Forms.Button>().Single(b => b.Text == "&Save");
            string[] Rows() => list.Items.Cast<object>().Select(item => list.GetItemText(item)!).ToArray();

            search.Text = "alpha";
            type.GetMethod("StartNewRule", flags)!.Invoke(dialog, null);
            editor.Text = "synthetic-draft";
            save.PerformClick();
            Assert.Equal(new[] { "synthetic-alpha (alpha)" }, Rows());
            Assert.Contains("The current filter hides it.", status.Text);
            search.Clear();
            Assert.Contains("synthetic-draft (sanitize-001)", Rows());
            dialog.Close();
        });
    }

    private static void RunSta(string surface, Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), $"{surface} Windows run timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void LegacyPrivateStoreReopensWithValuesVisible()
    {
        RunSta("Legacy store", () =>
        {
            var type = System.Reflection.Assembly.Load("PromptFlipper").GetType("PromptNavigator.SanitizerDialog", true)!;
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            using var fixture = new SanitizerTestStore();
            var legacy = System.Text.Encoding.UTF8.GetBytes(
                "[{\"name\":\"legacy\",\"value\":\"synthetic-legacy\",\"private\":true}]");
            var protect = typeof(PromptNavigator.ProtectedRuleStore).GetMethod("Protect",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            File.WriteAllBytes(fixture.PathName, (byte[])protect.Invoke(null, [legacy])!);
            using var dialog = fixture.CreateDialog();
            var list = (System.Windows.Forms.ListBox)type.GetField("ruleNames", flags)!.GetValue(dialog)!;
            Assert.Equal("synthetic-legacy (legacy)", list.GetItemText(Assert.Single(list.Items.Cast<object>())));
        });
    }

    [Fact]
    public void RuleEditor_WindowsRunKeepsGeneratedPlaceholdersWhenValuesChange()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var assembly = System.Reflection.Assembly.Load("PromptFlipper");
                var type = assembly.GetType("PromptNavigator.SanitizerDialog", true)!;
                var appRuleType = assembly.GetType("PromptFlipper.SanitizerRule", true)!;
                var enumerableType = typeof(IEnumerable<>).MakeGenericType(appRuleType);
                var persistType = typeof(Action<>).MakeGenericType(enumerableType);
                var persisted = 0;
                Action<object> persist = _ => persisted++;
                var callback = Delegate.CreateDelegate(persistType, persist.Target, persist.Method);
                using var dialog = SanitizerTestRules.CreateDialog("first-secret second-secret", saveDelegate: callback);
                void Invoke(string name, params object?[] arguments) => type.GetMethod(name,
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(dialog, arguments);
                object Field(string name) => type.GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(dialog)!;
                var list = (System.Windows.Forms.ListBox)Field("ruleNames");
                var editor = (System.Windows.Forms.TextBox)Field("valueEditor");
                var rules = (System.Collections.IList)Field("rules");
                rules.Clear();
                var ruleType = assembly.GetType("PromptFlipper.SanitizerRule", true)!;
                Invoke("RefreshRuleList", new object?[] { null, true });
                foreach (var value in new[] { "first-secret", "second-secret" })
                {
                    Invoke("StartNewRule");
                    editor.Text = value;
                    Invoke("SaveEditedRule");
                }
                Assert.Equal(2, rules.Count);
                dialog.CreateControl();
                for (var index = 0; index < 2; index++)
                {
                    list.ClearSelected();
                    list.SelectedIndex = index;
                    var selectedName = (string)list.SelectedItem!;
                    object SelectedRule() => rules.Cast<object>().Single(rule =>
                        (string)ruleType.GetProperty("Name")!.GetValue(rule)! == selectedName);
                    var placeholder = ruleType.GetProperty("Placeholder")!.GetValue(SelectedRule());
                    Assert.Equal("{{PromptFlipper.Sanitizer.sanitize-00" + (index + 1) + "}}", placeholder);
                    editor.Text = "changed-secret-" + index;
                    dialog.Show();
                    System.Windows.Forms.Application.DoEvents();
                    Descendants(dialog).OfType<System.Windows.Forms.Button>().Single(button => button.Text == "&Save").PerformClick();
                    Assert.Equal(placeholder, ruleType.GetProperty("Placeholder")!.GetValue(SelectedRule()));
                }
                Assert.Equal(4, persisted);
                Assert.Equal("changed-secret-1", ruleType.GetProperty("Value")!.GetValue(rules[1]));
                Invoke("RemoveSelectedRule");
                Assert.Single(rules.Cast<object>());
                Assert.Equal(5, persisted);
                Invoke("StartNewRule");
                editor.Text = "replacement-secret";
                Invoke("SaveEditedRule");
                Assert.Equal(2, rules.Count);
                Assert.Equal(2, rules.Cast<object>().Select(rule => ruleType.GetProperty("Placeholder")!.GetValue(rule)).Distinct().Count());
                dialog.Close();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Windows rule editor run timed out.");
        Assert.Null(failure);
    }

    public static TheoryData<string, SanitizerRule[]> InvalidRulesForClipboard => new()
    {
        { "unchanged", new[] { Rule(string.Empty, "secret") } },
        { "unchanged", new[] { Rule("Empty", string.Empty) } },
        { "secret", new[] { Rule("Same", "secret"), Rule("Same", "other") } },
        { "secret", new[] { Rule("First", "secret"), Rule("Second", "secret") } },
        { "already {{PromptFlipper.Sanitizer.api-key}}", new[] { Rule("Api Key", "secret") } },
        { "unchanged", new SanitizerRule[] { null! } }
    };

    [Fact]
    public void TrySanitize_ReplacesEveryConfiguredValue()
    {
        var rules = new[]
        {
            Rule("ApiKey", "secret-123"),
            Rule("User", "alice")
        };

        var success = Sanitizer.TrySanitize(
            "user=alice; token=secret-123; again=secret-123",
            rules,
            out var sanitized);

        Assert.True(success);
        Assert.Equal(
            "user={{PromptFlipper.Sanitizer.user}}; token={{PromptFlipper.Sanitizer.apikey}}; " +
            "again={{PromptFlipper.Sanitizer.apikey}}",
            sanitized);
    }

    [Fact]
    public void TrySanitize_ReplacesLongerOverlappingValueFirst()
    {
        var rules = new[]
        {
            Rule("Short", "abc"),
            Rule("Long", "abcdef")
        };

        var success = Sanitizer.TrySanitize("abcdef abc", rules, out var sanitized);

        Assert.True(success);
        Assert.Equal(
            "{{PromptFlipper.Sanitizer.long}} {{PromptFlipper.Sanitizer.short}}",
            sanitized);
    }

    [Fact]
    public void TrySanitize_ReturnsNoOutputForEmptyValue()
    {
        var success = Sanitizer.TrySanitize(
            "unchanged",
            new[] { Rule("Empty", string.Empty) },
            out var sanitized);

        Assert.False(success);
        Assert.Null(sanitized);
    }

    [Fact]
    public void TrySanitize_ReturnsNoOutputForDuplicateValue()
    {
        var rules = new[]
        {
            Rule("First", "shared-secret"),
            Rule("Second", "shared-secret")
        };

        var success = Sanitizer.TrySanitize("shared-secret", rules, out var sanitized);

        Assert.False(success);
        Assert.Null(sanitized);
    }

    [Fact]
    public void TrySanitize_ReturnsNoOutputWhenInputContainsGeneratedPlaceholder()
    {
        var success = Sanitizer.TrySanitize(
            "already {{PromptFlipper.Sanitizer.api-key}} and secret-123",
            new[] { Rule("Api Key", "secret-123") },
            out var sanitized);

        Assert.False(success);
        Assert.Null(sanitized);
    }

    [Fact]
    public void TrySanitize_UsesOrdinalCaseSensitiveMatching()
    {
        var success = Sanitizer.TrySanitize(
            "Secret secret",
            new[] { Rule("Value", "secret") },
            out var sanitized);

        Assert.True(success);
        Assert.Equal("Secret {{PromptFlipper.Sanitizer.value}}", sanitized);
    }

    [Theory]
    [InlineData(" API_key  42 ", "api-key-42")]
    [InlineData("Customer.Name", "customer-name")]
    [InlineData("Cafe\u0301", "caf\u00e9")]
    public void NormalizeName_IsDeterministic(string label, string expected)
    {
        Assert.Equal(expected, SanitizerRule.NormalizeName(label));
    }

    [Fact]
    public void Placeholder_AlwaysUsesFixedNamespace()
    {
        var rule = Rule("API key", "secret");

        Assert.Equal("PromptFlipper.Sanitizer", SanitizerRule.PlaceholderNamespace);
        Assert.Equal("{{PromptFlipper.Sanitizer.api-key}}", rule.Placeholder);
    }

    [Fact]
    public void TrySanitize_ReturnsNoOutputForNormalizedNameCollision()
    {
        var rules = new[]
        {
            Rule("Customer ID", "first-secret"),
            Rule("customer_id", "second-secret")
        };

        var success = Sanitizer.TrySanitize("first-secret second-secret", rules, out var sanitized);

        Assert.False(success);
        Assert.Null(sanitized);
    }

    [Fact]
    public void TrySanitize_ReturnsNoOutputForLabelWithoutNormalizedName()
    {
        var success = Sanitizer.TrySanitize("secret", new[] { Rule("---", "secret") }, out var sanitized);

        Assert.False(success);
        Assert.Null(sanitized);
    }

    [Fact]
    public void TryLoadRules_ProtectedStorageUnavailableDisablesSanitizing()
    {
        var success = Sanitizer.TryLoadRules(
            () => throw new UnauthorizedAccessException("Protected storage is unavailable."),
            out var rules);

        Assert.False(success);
        Assert.Empty(rules);
    }

    [Fact]
    public void TryLoadRules_CorruptProtectedStorageDisablesSanitizing()
    {
        var success = Sanitizer.TryLoadRules(
            () => throw new CryptographicException("Protected storage is corrupt."),
            out var rules);

        Assert.False(success);
        Assert.Empty(rules);
    }

    [Fact]
    public void TryImportRulesAndWrite_IgnoresBlankLinesAndUsesDeterministicNonSecretNames()
    {
        IReadOnlyList<SanitizerRule>? persisted = null;

        var success = Sanitizer.TryImportRulesAndWrite(
            () => ["first-secret", string.Empty, "   ", "second-secret"],
            Array.Empty<SanitizerRule>(),
            rules => persisted = rules,
            out var rules);

        Assert.True(success);
        Assert.Same(rules, persisted);
        Assert.Collection(
            rules,
            rule =>
            {
                Assert.Equal("sanitize-001", rule.Name);
                Assert.Equal("first-secret", rule.Value);
                Assert.Equal("{{PromptFlipper.Sanitizer.sanitize-001}}", rule.Placeholder);
            },
            rule =>
            {
                Assert.Equal("sanitize-002", rule.Name);
                Assert.Equal("second-secret", rule.Value);
                Assert.Equal("{{PromptFlipper.Sanitizer.sanitize-002}}", rule.Placeholder);
            });
    }

    [Fact]
    public void TryImportRulesAndWrite_DuplicateValuesDoNotPersistAnyRules()
    {
        var persistenceCalls = 0;

        var success = Sanitizer.TryImportRulesAndWrite(
            () => ["shared-secret", "shared-secret"],
            Array.Empty<SanitizerRule>(),
            _ => persistenceCalls++,
            out var rules);

        Assert.False(success);
        Assert.Empty(rules);
        Assert.Equal(0, persistenceCalls);
    }

    [Fact]
    public void TryImportRulesAndWrite_ExistingValueConflictDoesNotPersistAnyRules()
    {
        var persistenceCalls = 0;

        var success = Sanitizer.TryImportRulesAndWrite(
            () => ["existing-secret"],
            new[] { Rule("Existing", "existing-secret") },
            _ => persistenceCalls++,
            out var rules);

        Assert.False(success);
        Assert.Empty(rules);
        Assert.Equal(0, persistenceCalls);
    }

    [Fact]
    public void TryImportRulesAndWrite_SkipsExistingNormalizedNumberedNames()
    {
        var persistenceCalls = 0;

        var success = Sanitizer.TryImportRulesAndWrite(
            () => ["new-secret"],
            new[] { Rule("sanitize_001", "existing-secret") },
            _ => persistenceCalls++,
            out var rules);

        Assert.True(success);
        Assert.Equal("sanitize-002", rules[1].Name);
        Assert.Equal(2, rules.Select(rule => rule.Placeholder).Distinct().Count());
        Assert.Equal(1, persistenceCalls);
    }

    [Fact]
    public void TryImportRulesAndWrite_UnreadableSourceDoesNotPersistAnyRules()
    {
        var persistenceCalls = 0;

        var success = Sanitizer.TryImportRulesAndWrite(
            () => throw new IOException("The source cannot be read."),
            Array.Empty<SanitizerRule>(),
            _ => persistenceCalls++,
            out var rules);

        Assert.False(success);
        Assert.Empty(rules);
        Assert.Equal(0, persistenceCalls);
    }

    [Fact]
    public void TryImportRulesAndWrite_PersistenceFailureReturnsNoRules()
    {
        var persistenceCalls = 0;

        var success = Sanitizer.TryImportRulesAndWrite(
            () => ["new-secret"],
            Array.Empty<SanitizerRule>(),
            _ =>
            {
                persistenceCalls++;
                throw new IOException("The protected store cannot be written.");
            },
            out var rules);

        Assert.False(success);
        Assert.Empty(rules);
        Assert.Equal(1, persistenceCalls);
    }

    [Theory]
    [MemberData(nameof(InvalidRulesForClipboard))]
    public void TrySanitizeAndWrite_ValidationFailureDoesNotInvokeClipboard(
        string input,
        SanitizerRule[] rules)
    {
        var clipboardWrites = 0;

        var success = Sanitizer.TrySanitizeAndWrite(
            input,
            rules,
            _ => clipboardWrites++,
            out var sanitized);

        Assert.False(success);
        Assert.Null(sanitized);
        Assert.Equal(0, clipboardWrites);
    }

    private static SanitizerRule Rule(string name, string value)
    {
        return new SanitizerRule(name, value);
    }
}
