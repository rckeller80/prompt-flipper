namespace PromptNavigator;

internal sealed class CardFolderChangedEventArgs(IReadOnlyList<(string OldPath, string NewPath)> renames) : EventArgs
{
  internal string MapPath(string path)
  {
    foreach (var rename in renames)
      path = CardFolderWatcher.MapRenamedPath(path, rename.OldPath, rename.NewPath);
    return path;
  }
}

// Watcher callbacks are marshalled before touching the timer or the rename batch.
internal sealed class CardFolderWatcher : IDisposable
{
  private readonly Control owner;
  private readonly System.Windows.Forms.Timer timer = new() { Interval = 350 };
  private readonly List<(string OldPath, string NewPath)> renames = [];
  private FileSystemWatcher? watcher;
  private bool disposed;
  internal event EventHandler<CardFolderChangedEventArgs>? Changed;

  internal CardFolderWatcher(Control owner)
  {
    this.owner = owner;
    owner.Disposed += OwnerDisposed;
    timer.Tick += (_, _) =>
    {
      timer.Stop();
      var changes = new CardFolderChangedEventArgs(renames.ToArray());
      renames.Clear();
      Changed?.Invoke(this, changes);
    };
  }

  internal void Watch(string root)
  {
    timer.Stop();
    renames.Clear();
    watcher?.Dispose();
    watcher = null;
    if (disposed || !owner.IsHandleCreated || !Directory.Exists(root)) return;

    watcher = new FileSystemWatcher(root)
    {
      IncludeSubdirectories = true,
      NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
        | NotifyFilters.LastWrite | NotifyFilters.Size
    };
    watcher.Changed += FilesChanged;
    watcher.Created += FilesChanged;
    watcher.Deleted += FilesChanged;
    watcher.Renamed += FilesChanged;
    watcher.Error += WatcherError;
    watcher.EnableRaisingEvents = true;
  }

  internal void RequestRefresh()
  {
    if (disposed) return;
    timer.Stop();
    timer.Start();
  }

  private void FilesChanged(object sender, FileSystemEventArgs args) => QueueRefresh(sender, args);
  private void WatcherError(object sender, ErrorEventArgs args) => QueueRefresh(sender, null);

  private void QueueRefresh(object sender, FileSystemEventArgs? args)
  {
    if (disposed || owner.IsDisposed || owner.Disposing || !owner.IsHandleCreated) return;
    try
    {
      owner.BeginInvoke(() =>
      {
        // Ignore queued events from a previous root or a disposed watcher.
        if (disposed || !ReferenceEquals(sender, watcher)) return;
        if (args is RenamedEventArgs renamed)
          renames.Add((renamed.OldFullPath, renamed.FullPath));
        RequestRefresh();
      });
    }
    catch (InvalidOperationException)
    {
      // The owner closed after the event was raised.
    }
  }

  internal static string MapRenamedPath(string path, string oldPath, string newPath)
  {
    if (string.Equals(path, oldPath, StringComparison.OrdinalIgnoreCase)) return newPath;
    var prefix = Path.TrimEndingDirectorySeparator(oldPath) + Path.DirectorySeparatorChar;
    return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
      ? Path.Combine(newPath, path[prefix.Length..]) : path;
  }

  private void OwnerDisposed(object? sender, EventArgs args) => Dispose();

  public void Dispose()
  {
    if (disposed) return;
    disposed = true;
    owner.Disposed -= OwnerDisposed;
    watcher?.Dispose();
    watcher = null;
    timer.Dispose();
    renames.Clear();
  }
}
