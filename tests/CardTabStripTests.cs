using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using static PromptFlipper.Tests.MainFormTestHarness;
using Xunit;

namespace PromptFlipper.Tests;

[Collection("Organization UI")]
public sealed class CardTabStripTests
{
  [Fact]
  public void OwnsOrderedPathsAndRefreshesLabelsWithoutSelectingCards()
  {
    RunInSta(() =>
    {
      using var fixture = new MainFormTestHarness();
      using var host = new Form();
      using var tabs = CreateTabStrip();
      host.Controls.Add(tabs);
      host.Show();
      var first = Path.Combine(fixture.Root, "first.md");
      var second = Path.Combine(fixture.Root, "Folder", "second.md");
      var renamed = Path.Combine(fixture.Root, "Folder", "renamed.md");
      var selections = new List<string>();
      SubscribeTabEvent(tabs, "TabSelected", (_, args) => selections.Add(EventPath(args)));
      InvokeTab(tabs, "Open", first);
      InvokeTab(tabs, "Open", first.ToUpperInvariant());
      InvokeTab(tabs, "Open", second);
      InvokeTab(tabs, "UpdateCards", fixture.Root, new[] { first, second }, second);
      Assert.Equal(new[] { first, second }, Paths(tabs));
      Assert.Equal(second, tabs.SelectedTab!.Tag);
      Assert.Equal("second.md", tabs.SelectedTab.Text);
      Assert.Equal("Open card tab second.md", tabs.SelectedTab.AccessibleName);
      Assert.Equal(Path.Combine("Folder", "second.md"), tabs.SelectedTab.ToolTipText);
      Assert.Empty(selections);
      var oldPages = tabs.TabPages.Cast<TabPage>().ToArray();
      InvokeTab(tabs, "Rename", second.ToUpperInvariant(), renamed);
      InvokeTab(tabs, "UpdateCards", fixture.Root, new[] { first, renamed }, renamed);
      Assert.All(oldPages, page => Assert.True(page.IsDisposed));
      Assert.Equal(new[] { first, renamed }, Paths(tabs));
      Assert.Equal("renamed.md", tabs.SelectedTab!.Text);
      Assert.Empty(selections);
      tabs.SelectedIndex = 0;
      Assert.Equal(new[] { first }, selections);
      InvokeTab(tabs, "MapPaths", (Func<string, string>)(path => path + ".moved"));
      InvokeTab(tabs, "RetainPaths", (object)new[] { (renamed + ".moved").ToUpperInvariant() });
      Assert.Equal(new[] { renamed + ".moved" }, Paths(tabs));
      InvokeTab(tabs, "Remove", (renamed + ".moved").ToUpperInvariant());
      InvokeTab(tabs, "UpdateCards", fixture.Root, Array.Empty<string>(), null);
      Assert.Empty(Paths(tabs));
      Assert.Empty(tabs.TabPages);
      Assert.False(tabs.Visible);
      InvokeTab(tabs, "Open", first);
      InvokeTab(tabs, "Clear");
      Assert.Empty(Paths(tabs));
      host.Close();
    });
  }

  [Fact]
  public void CloseRequestsLeaveRemovalToTheHost()
  {
    RunInSta(() =>
    {
      using var fixture = new MainFormTestHarness();
      using var host = new Form();
      using var tabs = CreateTabStrip();
      host.Controls.Add(tabs);
      var path = Path.Combine(fixture.Root, "first.md");
      InvokeTab(tabs, "Open", path);
      InvokeTab(tabs, "UpdateCards", fixture.Root, new[] { path }, path);
      host.Show();
      var closing = new List<string>();
      SubscribeTabEvent(tabs, "TabClosing", (_, args) => closing.Add(EventPath(args)));
      var close = GetTabCloseBounds(tabs, 0);
      Mouse(tabs, "Down", new Point(close.Left + 3, close.Top + 3));
      Assert.Equal(new[] { path }, closing);
      Assert.Equal(new[] { path }, Paths(tabs));
      Assert.Single(tabs.TabPages.Cast<TabPage>());
      Assert.False((bool)InvokeTab(tabs, "CancelTabDrag")!);
      host.Close();
    });
  }

  [Theory]
  [InlineData("commit")]
  [InlineData("escape")]
  [InlineData("capture")]
  [InlineData("outside")]
  public void ReorderNotifiesOnlyForCommittedChanges(string finish)
  {
    RunInSta(() =>
    {
      using var fixture = new MainFormTestHarness();
      using var host = new Form { Width = 700 };
      using var tabs = CreateTabStrip();
      host.Controls.Add(tabs);
      var paths = new[] { "first.md", "second.md", "third.md" }
        .Select(name => Path.Combine(fixture.Root, name)).ToArray();
      foreach (var path in paths) InvokeTab(tabs, "Open", path);
      InvokeTab(tabs, "UpdateCards", fixture.Root, paths, paths[0]);
      host.Show();
      var changes = 0;
      var selections = 0;
      SubscribeTabEvent(tabs, "OrderChanged", (_, _) => changes++);
      SubscribeTabEvent(tabs, "TabSelected", (_, _) => selections++);
      var first = tabs.GetTabRect(0);
      var last = tabs.GetTabRect(2);
      var start = new Point(first.Left + 8, first.Top + first.Height / 2);
      var end = new Point(last.Right - 8, last.Top + last.Height / 2);
      Mouse(tabs, "Down", start);
      Mouse(tabs, "Move", end);
      Assert.Equal(new[] { paths[1], paths[2], paths[0] }, Paths(tabs));
      Assert.Equal(paths[0], tabs.SelectedTab!.Tag);
      Assert.Equal(0, selections);
      Assert.Equal(0, changes);
      switch (finish)
      {
        case "escape": Assert.True((bool)InvokeTab(tabs, "CancelTabDrag")!); break;
        case "capture": tabs.Capture = false; break;
        case "outside": Mouse(tabs, "Up", new Point(end.X, tabs.Height + 40)); break;
        default: Mouse(tabs, "Up", end); break;
      }
      Assert.Equal(finish == "commit" ? new[] { paths[1], paths[2], paths[0] } : paths, Paths(tabs));
      Assert.Equal(finish == "commit" ? 1 : 0, changes);
      Assert.False((bool)InvokeTab(tabs, "CancelTabDrag")!);
      Assert.Equal(paths[0], tabs.SelectedTab!.Tag);
      Assert.Equal(0, selections);
      Assert.Equal(Cursors.Default, tabs.Cursor);
      host.Close();
    });
  }

  private static string[] Paths(TabControl tabs) => ((IReadOnlyList<string>)tabs.GetType()
    .GetProperty("Paths", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(tabs)!).ToArray();

  private static string EventPath(EventArgs args) => (string)args.GetType()
    .GetProperty("FilePath", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(args)!;

  private static void Mouse(TabControl tabs, string action, Point point) => typeof(Control)
    .GetMethod("OnMouse" + action, BindingFlags.Instance | BindingFlags.NonPublic)!
    .Invoke(tabs, [new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0)]);
}
