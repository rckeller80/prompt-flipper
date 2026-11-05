using System.Reflection;
using static PromptFlipper.Tests.MainFormTestHarness;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Xunit;

namespace PromptFlipper.Tests;

// Windows Forms initializes process-wide shortcut caches when forms are first created.
[CollectionDefinition("Organization UI", DisableParallelization = true)]
public sealed class OrganizationUiCollection { }

[Collection("Organization UI")]
public sealed class MainFormOrganizationTests
{
  [Fact]
  public void RepositoryCreatesFirstFreeNameAndDeletesOnlyTheSelectedCard()
  {
    using var fixture = new MainFormTestHarness();
    var root = fixture.Root;
    var repositoryType = AppAssembly.GetType("PromptNavigator.CardRepository", true)!;
    var repository = Activator.CreateInstance(repositoryType)!;
    var create = repositoryType.GetMethod("CreateNew")!;
    var delete = repositoryType.GetMethod("Delete")!;
    const string content = "New prompt";

    var first = create.Invoke(repository, [root])!;
    var firstPath = Path.Combine(root, "new-prompt.md");
    Assert.Equal(content, File.ReadAllText(firstPath));
    Assert.Equal(System.Text.Encoding.UTF8.GetBytes(content), File.ReadAllBytes(firstPath));
    var occupiedPath = Path.Combine(root, "new-prompt-2.md");
    File.WriteAllText(occupiedPath, "Existing file");
    var third = create.Invoke(repository, [root])!;
    var thirdPath = Path.Combine(root, "new-prompt-3.md");
    Assert.Equal(thirdPath, third.GetType().GetProperty("FilePath")!.GetValue(third));
    Assert.Equal(content, File.ReadAllText(thirdPath));
    Assert.Equal("Existing file", File.ReadAllText(occupiedPath));

    delete.Invoke(repository, [first]);
    Assert.False(File.Exists(firstPath));
    Assert.Equal(content, File.ReadAllText(thirdPath));
    Assert.Equal("Existing file", File.ReadAllText(occupiedPath));
    var replacement = create.Invoke(repository, [root])!;
    Assert.Equal(firstPath, replacement.GetType().GetProperty("FilePath")!.GetValue(replacement));
    Assert.Equal(content, File.ReadAllText(firstPath));
  }

  [Fact]
  public void NumberedCardsRemainOrdinaryFilesWithoutFavoriteControls()
  {
    RunInSta(() =>
    {
      using var fixture = new MainFormTestHarness();
      var root = fixture.Root;
      var path = Path.Combine(root, "01-first.md");
      var secondPath = Path.Combine(root, "99-second.md");
      const string content = "tags: example\n\nOriginal prompt";
      File.WriteAllText(path, content);
      File.WriteAllText(secondPath, content);
      var repositoryType = AppAssembly.GetType("PromptNavigator.CardRepository", true)!;
      var repository = Activator.CreateInstance(repositoryType)!;
      var cards = repositoryType.GetMethod("Load")!.Invoke(repository, [root])!;
      var first = ((System.Collections.IEnumerable)cards).Cast<object>().First();
      Assert.Equal("01-first", first.GetType().GetProperty("Title")!.GetValue(first));
      using (var form = fixture.CreateForm(cards))
      {
        form.Show();
        var items = Descendants(form).OfType<ToolStrip>()
          .SelectMany(strip => strip.Items.Cast<ToolStripItem>()).ToArray();
        Assert.DoesNotContain(items, item =>
          new[] { item.Text, item.AccessibleName, item.ToolTipText }.Any(text =>
            text?.Contains("pin", StringComparison.OrdinalIgnoreCase) == true
            || text?.Contains("favorite", StringComparison.OrdinalIgnoreCase) == true));
        Assert.DoesNotContain(AppAssembly.GetManifestResourceNames(),
          name => name.EndsWith(".Favorite.png", StringComparison.Ordinal));
        var editor = CardPane(form);
        Assert.Equal("Original prompt", editor.Text);
        editor.Text = "Edited numbered prompt";
        MoveCardMethod.Invoke(form, [1]);
        Assert.Contains("Edited numbered prompt", File.ReadAllText(path));
        Assert.Equal(content, File.ReadAllText(secondPath));
        Assert.Equal("Original prompt", editor.Text);
        MoveCardMethod.Invoke(form, [1]);
        Assert.Equal("Edited numbered prompt", editor.Text);
        Assert.Equal(new[] { "01-first.md", "99-second.md" },
          Directory.GetFiles(root).Select(Path.GetFileName).OrderBy(name => name).ToArray());
        form.Close();
      }
      // An explicit rename uses the requested name without retaining a numeric prefix.
      var renamed = repositoryType.GetMethod("Rename")!.Invoke(repository, [root, first, "renamed.md"])!;
      Assert.Equal("renamed", renamed.GetType().GetProperty("Title")!.GetValue(renamed));
      Assert.False(File.Exists(path));
      Assert.Contains("Edited numbered prompt", File.ReadAllText(Path.Combine(root, "renamed.md")));
    }, timeoutMessage: "Windows tab interaction timed out.");
  }

  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public void DeleteControlsIdentifyFileDeletionAndRequireConfirmation(bool confirm)
  {
    RunInSta(() =>
    {
      using var fixture = new MainFormTestHarness();
      var root = fixture.Root;
      var path = Path.Combine(root, "delete-test.md");
      File.WriteAllText(path, "tags:\n\nDeletion test");
      using var form = fixture.CreateForm();
      form.Show();
      var controls = Descendants(form).ToArray();
      var button = Assert.Single(controls.OfType<Button>(), item => item.AccessibleName == "Trash");
      Assert.Equal(string.Empty, button.Text);
      Assert.DoesNotContain(controls.OfType<Label>(), item => item.Text == "All prompts");
      Assert.DoesNotContain(controls.OfType<ToolStrip>()
        .SelectMany(strip => strip.Items.OfType<ToolStripButton>()),
        item => item.AccessibleName?.Contains("Trash", StringComparison.OrdinalIgnoreCase) == true);
      Assert.Equal("Trash", button.AccessibleName);
      Assert.NotNull(button.Image);

      // Closing a tab preserves the file and leaves deletion available after reopening.
      CloseCardTabMethod.Invoke(form, [path]);
      Assert.True(File.Exists(path));
      SelectCardMethod.Invoke(form, [path]);

      var dialogSeen = false;
      var defaultIsNo = false;
      using var timer = new System.Windows.Forms.Timer { Interval = 25 };
      timer.Tick += (_, _) =>
      {
        var dialog = FindWindow("#32770", "Delete prompt card file");
        if (dialog == IntPtr.Zero || GetWindow(dialog, 4) != form.Handle) return;
        dialogSeen = true;
        defaultIsNo = (SendMessage(dialog, 0x400, IntPtr.Zero, IntPtr.Zero).ToInt64() & 0xffff) == 7;
        SendMessage(dialog, 0x111, (IntPtr)(confirm ? 6 : 7), IntPtr.Zero);
      };
      timer.Start();
      button.PerformClick();
      timer.Stop();
      Assert.True(dialogSeen);
      Assert.True(defaultIsNo);
      Assert.Equal(!confirm, File.Exists(path));
      if (confirm)
      {
        // An empty collection must not open another confirmation or throw.
        RemoveCurrentCardMethod.Invoke(form, null);
      }
      form.Close();
    }, timeoutMessage: "Windows tab interaction timed out.");
  }

  private static IEnumerable<Control> Descendants(Control parent) =>
    parent.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Descendants(child)));

  [DllImport("user32.dll", CharSet = CharSet.Unicode)]
  private static extern IntPtr FindWindow(string className, string windowName);

  [DllImport("user32.dll")]
  private static extern IntPtr GetWindow(IntPtr window, uint command);

  [Fact]
  public void LocationCueDistinguishesRootAndNestedCards()
  {
    var root = Path.Combine(Path.GetTempPath(), "prompt-flipper-cards");

    var rootCue = BuildLocationCue(root, Path.Combine(root, "root-card.md"));
    var nestedCue = BuildLocationCue(
      root,
      Path.Combine(root, "Work", "Drafts", "nested-card.md"));

    Assert.Equal("root-card.md", rootCue);
    Assert.Equal("Work > Drafts > nested-card.md", nestedCue);
    Assert.NotEqual(rootCue, nestedCue);
  }

  [Fact]
  public void OrientationCuesChangeWithTheCurrentCard()
  {
    var root = Path.Combine(Path.GetTempPath(), "prompt-flipper-cards");

    var firstLocation = BuildLocationCue(root, Path.Combine(root, "first.md"));
    var firstProgress = BuildProgressCue(0, 2);
    var secondLocation = BuildLocationCue(root, Path.Combine(root, "Archive", "second.md"));
    var secondProgress = BuildProgressCue(1, 2);

    Assert.Equal("first.md", firstLocation);
    Assert.Equal("Card 1 / 2", firstProgress);
    Assert.Equal("Archive > second.md", secondLocation);
    Assert.Equal("Card 2 / 2", secondProgress);
  }

  [Fact]
  public void WindowsTabDraggingPreservesEditorAndSupportsNavigationAndClosing()
  {
    RunInSta(() =>
    {
      using var fixture = new MainFormTestHarness();
      var root = fixture.Root;
      var paths = new[] { "alpha.md", "bravo.md", "charlie.md" }
        .Select(name => Path.Combine(root, name)).ToArray();
      foreach (var path in paths)
      {
        File.WriteAllText(path, "tags:\n\n" + Path.GetFileNameWithoutExtension(path));
      }

      using var form = fixture.CreateForm();
      form.Show();
      var tabs = CardTabs(form);
      var editor = CardPane(form);
      foreach (var path in paths)
      {
        SelectCardMethod.Invoke(form, [path]);
      }

      ClickTab(tabs, 0);
      Assert.Equal(paths[0], tabs.SelectedTab!.Tag);
      editor.Text = "unsaved drag text";
      editor.Select(3, 5);

      DragTab(tabs, 0, 2, after: true);
      AssertTabOrder(tabs, paths[1], paths[2], paths[0]);
      Assert.Equal(paths[0], tabs.SelectedTab!.Tag);
      Assert.Equal("unsaved drag text", editor.Text);
      Assert.Equal(3, editor.SelectionStart);
      Assert.Equal(5, editor.SelectionLength);
      Assert.DoesNotContain("unsaved", File.ReadAllText(paths[0]));

      DragTab(tabs, 2, 1, after: false);
      AssertTabOrder(tabs, paths[1], paths[0], paths[2]);
      Assert.Equal(paths[0], tabs.SelectedTab!.Tag);
      Assert.Equal("unsaved drag text", editor.Text);

      // Ordinary clicks still select cards and save the prior editor.
      ClickTab(tabs, 2);
      Assert.Equal(paths[2], tabs.SelectedTab!.Tag);
      Assert.Contains("unsaved drag text", File.ReadAllText(paths[0]));
      tabs.Focus();
      var key = new KeyEventArgs(Keys.Left);
      MainFormOnKeyDownMethod.Invoke(form, [form, key]);
      Assert.True(key.Handled);
      Assert.Equal("bravo", editor.Text);

      var close = GetTabCloseBounds(tabs, 1);
      ClickAt(tabs, new Point(close.Left + 3, close.Top + 3));
      AssertTabOrder(tabs, paths[1], paths[2]);
      Assert.Equal(paths[2], tabs.SelectedTab!.Tag);

      // Empty-strip drops commit the live order.
      var empty = new Point(tabs.GetTabRect(1).Right + 30, tabs.GetTabRect(1).Top + 5);
      SendMouse(tabs, 0x201, TabPoint(tabs, 0, false), true);
      SendMouse(tabs, 0x200, empty, true);
      AssertTabOrder(tabs, paths[2], paths[1]);
      SendMouse(tabs, 0x202, empty, false);
      AssertTabOrder(tabs, paths[2], paths[1]);
      DragTab(tabs, 1, 0, after: false);

      // Below-threshold clicks, outside drops, Escape and capture loss cancel.
      var start = TabPoint(tabs, 0, false);
      SendMouse(tabs, 0x201, start, true);
      SendMouse(tabs, 0x202, start, false);
      AssertTabOrder(tabs, paths[1], paths[2]);
      SendMouse(tabs, 0x201, start, true);
      SendMouse(tabs, 0x200, new Point(start.X + 30, start.Y + 40), true);
      SendMouse(tabs, 0x202, new Point(start.X + 30, start.Y + 40), false);
      AssertTabOrder(tabs, paths[1], paths[2]);
      foreach (var escape in new[] { true, false })
      {
        SendMouse(tabs, 0x201, start, true);
        SendMouse(tabs, 0x200, TabPoint(tabs, 1, true), true);
        AssertTabOrder(tabs, paths[2], paths[1]);
        Assert.Equal(Cursors.Default, tabs.Cursor);
        if (escape)
        {
          var arguments = new object[] { new Message(), Keys.Escape };
          Assert.Equal(true, ProcessCmdKeyMethod.Invoke(form, arguments));
        }
        else
        {
          tabs.Capture = false;
        }
        SendMouse(tabs, 0x202, TabPoint(tabs, 1, true), false);
        AssertTabOrder(tabs, paths[1], paths[2]);
        Assert.Equal(Cursors.Default, tabs.Cursor);
      }
      form.Close();
    }, timeoutMessage: "Windows tab interaction timed out.");
  }

  private static void AssertTabOrder(TabControl tabs, params string[] paths) =>
    Assert.Equal(paths, tabs.TabPages.Cast<TabPage>().Select(tab => (string)tab.Tag!).ToArray());

  private static Point TabPoint(TabControl tabs, int index, bool after)
  {
    var bounds = tabs.GetTabRect(index);
    return new Point(bounds.Left + bounds.Width * (after ? 3 : 1) / 4, bounds.Top + bounds.Height / 2);
  }

  private static void ClickTab(TabControl tabs, int index) => ClickAt(tabs, TabPoint(tabs, index, false));

  private static void ClickAt(TabControl tabs, Point point)
  {
    SendMouse(tabs, 0x201, point, true);
    SendMouse(tabs, 0x202, point, false);
  }

  private static void DragTab(TabControl tabs, int source, int target, bool after)
  {
    var expected = tabs.TabPages.Cast<TabPage>().Select(tab => (string)tab.Tag!).ToList();
    var moved = expected[source];
    expected.RemoveAt(source);
    var insertion = target + (after ? 1 : 0);
    if (source < insertion) insertion--;
    expected.Insert(insertion, moved);
    SendMouse(tabs, 0x201, TabPoint(tabs, source, false), true);
    var form = tabs.FindForm()!;
    Assert.NotNull(DraggedCardPath(form));
    var destination = TabPoint(tabs, target, after);
    SendMouse(tabs, 0x200, destination, true);
    Assert.True(TabDragStarted(form));
    Assert.Equal(Cursors.Default, tabs.Cursor);
    AssertTabOrder(tabs, expected.ToArray());
    SendMouse(tabs, 0x202, destination, false);
  }

  private static void SendMouse(TabControl tabs, int message, Point point, bool leftDown)
  {
    if (message == 0x200)
    {
      // WM_MOUSEMOVE reads physical button state; supply the held button to the
      // managed event so this Windows control test does not move the user's mouse.
      typeof(Control).GetMethod("OnMouseMove", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(tabs, [new MouseEventArgs(leftDown ? MouseButtons.Left : MouseButtons.None,
          0, point.X, point.Y, 0)]);
      return;
    }
    SendMessage(tabs.Handle, message, leftDown ? (IntPtr)1 : IntPtr.Zero,
      (IntPtr)((point.Y << 16) | (point.X & 0xffff)));
  }

  [DllImport("user32.dll")]
  private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

}
