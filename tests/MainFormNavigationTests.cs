using PromptFlipper.Tests;
using System.Collections;
using System.Reflection;
using static PromptFlipper.Tests.MainFormTestHarness;
using System.Runtime.InteropServices;
using Xunit;

namespace PromptNavigator.Tests;

public sealed class MainFormNavigationTests
{
  [Fact]
  public void PreviousAndNextWrapWithoutOpeningAdjacentCardTabs()
  {
    RunInSta(RunNavigationTest);
  }

  private static void RunNavigationTest()
  {
    using var fixture = new MainFormTestHarness();
    var root = fixture.Root;

    var firstPath = Path.Combine(root, "first.md");
    var secondPath = Path.Combine(root, "second.md");
    File.WriteAllText(firstPath, "tags:\n\nfirst");
    File.WriteAllText(secondPath, "tags:\n\nsecond");

    var appAssembly = Assembly.Load("PromptFlipper");
    var cardType = appAssembly.GetType("PromptNavigator.Card", throwOnError: true)!;

    var cards = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(cardType))!;
    cards.Add(CreateCard(cardType, "First", firstPath, "first"));
    cards.Add(CreateCard(cardType, "Second", secondPath, "second"));

    using var form = fixture.CreateForm(cards);
    var moveCard = MoveCardMethod;
    var selectCard = SelectCardMethod;
    var currentIndex = CurrentIndexField;
    var collectionStatus = (System.Windows.Forms.ToolStripStatusLabel)CollectionStatusLabelField.GetValue(form)!;
    var search = SearchBox(form);

    Assert.Equal(0, currentIndex.GetValue(form));
    Assert.Equal("2 prompts - browse in Search", collectionStatus.Text);
    Assert.Equal(2, search.Items.Count);
    Assert.Equal([firstPath], OpenCardPaths(form).ToArray());

    moveCard.Invoke(form, [1]);
    Assert.Equal(1, currentIndex.GetValue(form));
    Assert.Equal([firstPath], OpenCardPaths(form).ToArray());

    moveCard.Invoke(form, [1]);
    Assert.Equal(0, currentIndex.GetValue(form));
    Assert.Equal([firstPath], OpenCardPaths(form).ToArray());

    moveCard.Invoke(form, [-1]);
    Assert.Equal(1, currentIndex.GetValue(form));
    Assert.Equal([firstPath], OpenCardPaths(form).ToArray());

    selectCard.Invoke(form, [secondPath]);
    Assert.Equal([firstPath, secondPath], OpenCardPaths(form).ToArray());
    RefreshSearch(form);
    Assert.Equal(1, currentIndex.GetValue(form));
    Assert.Equal(2, search.Items.Count);

    // Browsing the collection must not replace the selected card or open every tab.
    BrowseAllCardsMethod.Invoke(form, null);
    Assert.Equal(1, currentIndex.GetValue(form));
    Assert.Equal(2, search.Items.Count);

    selectCard.Invoke(form, [firstPath]);
    Assert.Equal(0, currentIndex.GetValue(form));
    selectCard.Invoke(form, [secondPath]);
    var editor = (System.Windows.Forms.RichTextBox)CardPaneField.GetValue(form)!;
    var window = (System.Windows.Forms.Form)form;
    window.Show();
    System.Windows.Forms.Application.DoEvents();
    VerifyToolbar(window, editor, Descendants(window).OfType<System.Windows.Forms.ToolStrip>()
      .Single(strip => strip is not System.Windows.Forms.MenuStrip && strip is not System.Windows.Forms.StatusStrip));
    Assert.True(editor.Parent!.Width >= window.ClientSize.Width - 24);
    Assert.Null(CollectionListField);
    editor.Text = "Saved second prompt";
    Assert.True((bool)TrySaveCurrentCardMethod.Invoke(form, null)!);
    Assert.Equal(1, currentIndex.GetValue(form));
    Assert.Contains("Saved second prompt", File.ReadAllText(secondPath));

    cards.Clear();
    CardsField.SetValue(form, cards);
    currentIndex.SetValue(form, -1);
    RefreshSearch(form);
    ShowCurrentCardMethod.Invoke(form, [true]);
    Assert.Equal("No prompts in this folder", collectionStatus.Text);
    Assert.False(collectionStatus.Enabled);
    Assert.Empty(search.Items.Cast<object>());

    using var emptyForm = fixture.CreateForm(cards);
    Assert.Equal(-1, currentIndex.GetValue(emptyForm));
    Assert.Equal("No prompts in this folder", ((System.Windows.Forms.ToolStripStatusLabel)
      CollectionStatusLabelField.GetValue(emptyForm)!).Text);
  }

  private static void VerifyToolbar(System.Windows.Forms.Form window,
    System.Windows.Forms.RichTextBox editor, System.Windows.Forms.ToolStrip toolbar)
  {
    var host = Assert.Single(toolbar.Items.OfType<System.Windows.Forms.ToolStripControlHost>(),
      item => item.Control is System.Windows.Forms.CheckBox);
    var wrap = Assert.IsType<System.Windows.Forms.CheckBox>(host.Control);
    var size = Assert.Single(toolbar.Items.OfType<System.Windows.Forms.ToolStripDropDownButton>(),
      item => item.AccessibleName == "Text Size");
    var content = editor.Text;
    var originalSize = window.Size;
    foreach (var width in new[] { window.Width, window.MinimumSize.Width })
    {
      window.Width = width;
      System.Windows.Forms.Application.DoEvents();
      Assert.True(host.Visible);
      Assert.True(size.Visible);
      Assert.True(toolbar.ClientRectangle.Contains(host.Bounds));
      Assert.True(toolbar.ClientRectangle.Contains(size.Bounds));
      wrap.Checked = false;
      Assert.False(editor.WordWrap);
      Assert.Equal(System.Windows.Forms.RichTextBoxScrollBars.Both, editor.ScrollBars);
      wrap.Checked = true;
      Assert.True(editor.WordWrap);
      var largest = Assert.IsType<System.Windows.Forms.ToolStripMenuItem>(size.DropDownItems[16]);
      largest.PerformClick();
      Assert.Equal(24f, editor.Font.Size);
      Assert.True(largest.Checked);
      Assert.IsType<System.Windows.Forms.ToolStripMenuItem>(size.DropDownItems[2]).PerformClick();
      Assert.Equal(10f, editor.Font.Size);
      Assert.False(largest.Checked);
      Assert.Equal(content, editor.Text);
    }
    window.Size = originalSize;
    Assert.DoesNotContain(toolbar.Items.OfType<System.Windows.Forms.ToolStripLabel>(),
      item => item.Text == "Text size (pt):");
    foreach (var command in new[] { "Open", "Save", "Print", "Settings", "Help" })
    {
      var button = Assert.Single(toolbar.Items.OfType<System.Windows.Forms.ToolStripButton>(),
        item => item.Text == command);
      Assert.NotNull(button.Image);
      Assert.Equal(toolbar.ImageScalingSize, button.Image.Size);
      Assert.Equal(System.Windows.Forms.ToolStripItemDisplayStyle.Image, button.DisplayStyle);
    }
    var previewPath = Environment.GetEnvironmentVariable("PROMPT_FLIPPER_TOOLBAR_PREVIEW");
    if (!string.IsNullOrWhiteSpace(previewPath))
    {
      using var preview = new System.Drawing.Bitmap(window.Width, window.Height);
      window.DrawToBitmap(preview, new System.Drawing.Rectangle(System.Drawing.Point.Empty, window.Size));
      preview.Save(previewPath);
    }
  }

  [Theory]
  [InlineData("toolbar")]
  [InlineData("menu")]
  [InlineData("F1")]
  public void HelpEntryPointsOpenTopicHelpAndEscapeClosesIt(string entryPoint)
  {
    RunInSta(() =>
    {
      using var fixture = new MainFormTestHarness();
      using var main = fixture.CreateForm();
      main.Show();
      if (entryPoint == "F1")
      {
        Assert.True((bool)ProcessCmdKeyMethod
          .Invoke(main, [new System.Windows.Forms.Message(), System.Windows.Forms.Keys.F1])!);
      }
      else
      {
        var strips = Descendants(main).OfType<System.Windows.Forms.ToolStrip>();
        var items = strips.SelectMany(strip => strip.Items.Cast<System.Windows.Forms.ToolStripItem>());
        if (entryPoint == "toolbar")
          Assert.Single(items.OfType<System.Windows.Forms.ToolStripButton>(), item => item.Text == "Help").PerformClick();
        else
          Assert.Single(items.OfType<System.Windows.Forms.ToolStripMenuItem>()
            .SelectMany(item => item.DropDownItems.Cast<System.Windows.Forms.ToolStripItem>()),
            item => item.Text == "View &Help").PerformClick();
      }
      using var help = (System.Windows.Forms.Form)HelpWindowField.GetValue(main)!;
      Assert.NotNull(help);
      Assert.True(help.Visible);
      ShowHelpMethod.Invoke(main, null);
      Assert.Same(help, HelpWindowField.GetValue(main));
      VerifyCompactHelp(help);
      Assert.True((bool)ProcessFormKey(help, System.Windows.Forms.Keys.Escape)!);
      Assert.False(help.Visible);
      Assert.Null(HelpWindowField.GetValue(main));
      ShowHelpMethod.Invoke(main, null);
      using var reopened = (System.Windows.Forms.Form)HelpWindowField.GetValue(main)!;
      Assert.NotSame(help, reopened);
      Assert.True(reopened.Visible);
    });
  }

  private static IEnumerable<System.Windows.Forms.Control> Descendants(System.Windows.Forms.Control parent)
  {
    foreach (System.Windows.Forms.Control control in parent.Controls)
    {
      yield return control;
      foreach (var child in Descendants(control)) yield return child;
    }
  }

  private static void VerifyCompactHelp(System.Windows.Forms.Form help)
  {
    var controls = Descendants(help).ToArray();
    Assert.Empty(controls.OfType<System.Windows.Forms.ComboBox>());
    var text = Assert.Single(controls.OfType<System.Windows.Forms.TextBox>(), box => box.ReadOnly);
    Assert.Single(controls.OfType<System.Windows.Forms.TextBox>(), box => box.AccessibleName == "Search help");
    Assert.True(text.WordWrap);
    Assert.Equal(System.Windows.Forms.ScrollBars.Vertical, text.ScrollBars);
    var topics = Assert.Single(controls.OfType<System.Windows.Forms.TreeView>());
    Assert.Equal("Help topics", topics.AccessibleName);
    Assert.True(topics.Focused);
    Assert.Equal("Cards", topics.SelectedNode!.Text);
    Assert.Equal("Cards help", text.AccessibleName);
    Assert.Contains(controls.OfType<System.Windows.Forms.Label>(), label => label.Text == "Description");
    Assert.Single(topics.Nodes.Cast<System.Windows.Forms.TreeNode>());
    var root = topics.Nodes[0];
    Assert.Equal(new[] { "Toolbar", "Tabs", "Editor", "Quick Buttons", "Shortcuts", "About" },
      root.Nodes.Cast<System.Windows.Forms.TreeNode>().Select(node => node.Text));
    var expected = new Dictionary<string, string[]>
    {
      ["Toolbar"] = ["Attach Folder", "Save", "Print", "Search", "Wrap", "Text size", "Settings"],
      ["Tabs"] = ["Tabs control"],
      ["Editor"] = ["Edit text", "Rename files", "Flip cards"],
      ["Quick Buttons"] = ["Copy", "Sanitizer", "Trash", "Add Card"]
    };
    foreach (System.Windows.Forms.TreeNode group in root.Nodes)
      Assert.Equal(expected.GetValueOrDefault(group.Text, []),
        group.Nodes.Cast<System.Windows.Forms.TreeNode>().Select(node => node.Text));
    SendMessage(topics.Handle, 0x0100, (IntPtr)System.Windows.Forms.Keys.Down, IntPtr.Zero);
    SendMessage(topics.Handle, 0x0101, (IntPtr)System.Windows.Forms.Keys.Down, IntPtr.Zero);
    Assert.Equal("Toolbar", topics.SelectedNode.Text);
    help.SelectNextControl(topics, true, true, true, true);
    Assert.True(text.Focused);
    var allText = new System.Text.StringBuilder();
    var allNodes = new List<System.Windows.Forms.TreeNode>();
    void Collect(System.Windows.Forms.TreeNode node)
    {
      allNodes.Add(node);
      foreach (System.Windows.Forms.TreeNode child in node.Nodes) Collect(child);
    }
    Collect(root);
    foreach (var topic in allNodes)
    {
      topics.SelectedNode = topic;
      Assert.Equal(topic.Text + " help", text.AccessibleName);
      Assert.False(string.IsNullOrWhiteSpace(text.Text));
      Assert.DoesNotContain(controls.OfType<System.Windows.Forms.Label>(), label => label.Text == topic.Text);
      allText.AppendLine(text.Text);
    }
    foreach (var instruction in new[] { "Windows encryption", "Ctrl+Shift+O",
      "Wrap", "Text size", "De-Sanitize", "Attach Folder", "Trash", "Author: RC Keller", "Licence: MIT" })
      Assert.Contains(instruction, allText.ToString());
    Assert.Contains("Add file from template", allText.ToString());
    foreach (var obsolete in new[] { "Select New", "Copy reviewed result", "Preferences", "one card file only" })
      Assert.DoesNotContain(obsolete, allText.ToString());
    var settings = allNodes.MaxBy(node => { topics.SelectedNode = node; return text.TextLength; })!;
    var sizes = new[] { help.Size, help.MinimumSize };
    foreach (var size in sizes)
    {
      help.Size = size;
      help.PerformLayout();
      topics.SelectedNode = settings;
      var previewPath = Environment.GetEnvironmentVariable("PROMPT_FLIPPER_HELP_PREVIEW");
      if (!string.IsNullOrWhiteSpace(previewPath))
      {
        using var preview = new System.Drawing.Bitmap(help.Width, help.Height);
        help.DrawToBitmap(preview, new System.Drawing.Rectangle(System.Drawing.Point.Empty, help.Size));
        preview.Save($"{previewPath}-{size.Width}.png");
      }
      Assert.True(text.Width >= 350, $"Text width: {text.Width}");
      Assert.True(text.Height >= 100, $"Text height: {text.Height}");
      Assert.True(topics.Width >= 150);
      Assert.True(help.ClientRectangle.Contains(help.RectangleToClient(topics.RectangleToScreen(topics.ClientRectangle))));
      Assert.True(topics.Right <= topics.Parent!.ClientSize.Width);
      Assert.True(help.ClientRectangle.Contains(help.RectangleToClient(text.RectangleToScreen(text.ClientRectangle))));
      var overflows = text.GetPositionFromCharIndex(text.TextLength - 1).Y >= text.ClientSize.Height;
      text.SelectionStart = text.TextLength;
      text.ScrollToCaret();
      System.Windows.Forms.Application.DoEvents();
      if (overflows)
        Assert.True(text.GetCharIndexFromPosition(new System.Drawing.Point(2, 2)) > 0);
      text.SelectionStart = 0;
      text.ScrollToCaret();
    }
  }

  [DllImport("user32.dll", EntryPoint = "SendMessageW")]
  private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr key, IntPtr data);

  private static object CreateCard(Type cardType, string title, string filePath, string content)
  {
    return Activator.CreateInstance(
      cardType,
      title,
      filePath,
      string.Empty,
      content,
      content)!;
  }

}
