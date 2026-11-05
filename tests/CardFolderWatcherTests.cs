using System.Diagnostics;
using System.Windows.Forms;
using Xunit;

namespace PromptFlipper.Tests;

public sealed class CardFolderWatcherTests
{
  [Fact]
  public void RefreshRequestsAreDebouncedOnOwnerThread() => MainFormTestHarness.RunInSta(() =>
  {
    using var owner = new Control();
    _ = owner.Handle;
    using var watcher = new MainFormTestHarness.FolderWatcherProbe(owner);
    for (var i = 0; i < 10; i++) watcher.RequestRefresh();
    PumpUntil(() => watcher.Changes.Count > 0);
    PumpFor(500);
    Assert.Single(watcher.Changes);
    Assert.Equal(Environment.CurrentManagedThreadId, Assert.Single(watcher.CallbackThreads));
  });

  [Fact]
  public void FileAndDirectoryRenamesMapPathsInOrder() => MainFormTestHarness.RunInSta(() =>
  {
    using var fixture = new MainFormTestHarness();
    var folder = Directory.CreateDirectory(Path.Combine(fixture.Root, "before")).FullName;
    var original = Path.Combine(folder, "card.md");
    File.WriteAllText(original, "card");
    using var owner = new Control();
    _ = owner.Handle;
    using var watcher = new MainFormTestHarness.FolderWatcherProbe(owner);
    watcher.Watch(fixture.Root);
    var renamedFile = Path.Combine(folder, "renamed.md");
    File.Move(original, renamedFile);
    // Let the native rename callback reach the UI queue without waiting for debounce.
    PumpFor(100);
    var renamedFolder = Path.Combine(fixture.Root, "after");
    Directory.Move(folder, renamedFolder);
    PumpUntil(() => watcher.Changes.Count > 0);
    Assert.Single(watcher.Changes);
    Assert.Equal(Path.Combine(renamedFolder, "renamed.md"), watcher.MapPath(0, original));
    var sibling = Path.Combine(fixture.Root, "before-other", "card.md");
    Assert.Equal(sibling, watcher.MapPath(0, sibling));
  });

  [Fact]
  public void SwitchingRootsClearsPendingChangesAndWatchesNewRoot() => MainFormTestHarness.RunInSta(() =>
  {
    using var first = new MainFormTestHarness();
    using var second = new MainFormTestHarness();
    using var owner = new Control();
    _ = owner.Handle;
    using var watcher = new MainFormTestHarness.FolderWatcherProbe(owner);
    watcher.Watch(first.Root);
    File.WriteAllText(Path.Combine(first.Root, "old.md"), "old");
    Thread.Sleep(100); // Queue a native callback while the UI thread is occupied.
    watcher.RequestRefresh();
    watcher.Watch(second.Root);
    PumpFor(600);
    Assert.Empty(watcher.Changes);
    File.WriteAllText(Path.Combine(second.Root, "new.md"), "new");
    PumpUntil(() => watcher.Changes.Count > 0);
  });

  [Fact]
  public void DisposingOwnerCancelsPendingRefresh() => MainFormTestHarness.RunInSta(() =>
  {
    using var owner = new Control();
    _ = owner.Handle;
    using var watcher = new MainFormTestHarness.FolderWatcherProbe(owner);
    watcher.RequestRefresh();
    owner.Dispose();
    PumpFor(600);
    Assert.Empty(watcher.Changes);
    watcher.RequestRefresh();
  });

  private static void PumpUntil(Func<bool> ready)
  {
    var timeout = Stopwatch.StartNew();
    while (!ready() && timeout.Elapsed < TimeSpan.FromSeconds(5)) PumpFor(10);
    Assert.True(ready(), "Folder change was not delivered.");
  }

  private static void PumpFor(int milliseconds)
  {
    var timeout = Stopwatch.StartNew();
    while (timeout.ElapsedMilliseconds < milliseconds)
    {
      Application.DoEvents();
      Thread.Sleep(5);
    }
  }
}
