namespace PromptNavigator;

internal sealed class CardTabEventArgs(string filePath) : EventArgs
{
  internal string FilePath { get; } = filePath;
}

// Owns open-tab state and presentation. The host decides whether a close can save
// successfully before calling Remove, and supplies the current card after navigation.
internal sealed class CardTabStrip : TabControl
{
  private readonly List<string> openCardPaths = [];
  private string? draggedCardPath;
  private Point tabDragOrigin;
  private bool tabDragStarted;
  private string[]? tabDragOriginalOrder;
  private string cardRoot = string.Empty;
  private HashSet<string> availablePaths = new(StringComparer.OrdinalIgnoreCase);
  private string? currentPath;
  private bool rebuilding;

  internal IReadOnlyList<string> Paths => openCardPaths.AsReadOnly();
  internal event EventHandler<CardTabEventArgs>? TabSelected;
  internal event EventHandler<CardTabEventArgs>? TabClosing;
  internal event EventHandler? OrderChanged;

  internal CardTabStrip()
  {
    AccessibleName = "Open card tabs";
    Dock = DockStyle.Top;
    DrawMode = TabDrawMode.OwnerDrawFixed;
    Height = 28;
    Margin = new Padding(0, 0, 0, ToolbarArtwork.Spacing(ToolbarArtwork.SpacingStep.XS));
    Padding = new Point(18, 3);
    ShowToolTips = true;
    SelectedIndexChanged += (_, _) =>
    {
      if (!rebuilding && SelectedTab?.Tag is string path)
        TabSelected?.Invoke(this, new CardTabEventArgs(path));
    };
    DrawItem += CardTabsOnDrawItem;
    MouseDown += CardTabsOnMouseDown;
    MouseMove += CardTabsOnMouseMove;
    MouseUp += CardTabsOnMouseUp;
    MouseCaptureChanged += (_, _) =>
    {
      if (!Capture) CancelTabDrag();
    };
  }

  internal int IndexOf(string path) => openCardPaths.FindIndex(candidate =>
    string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase));

  internal void Open(string path)
  {
    if (IndexOf(path) < 0) openCardPaths.Add(path);
  }

  internal void Remove(string path)
  {
    var index = IndexOf(path);
    if (index >= 0) openCardPaths.RemoveAt(index);
  }

  internal void Clear() => openCardPaths.Clear();

  internal void Rename(string oldPath, string newPath)
  {
    var index = IndexOf(oldPath);
    if (index >= 0) openCardPaths[index] = newPath;
  }

  internal void MapPaths(Func<string, string> map)
  {
    for (var index = 0; index < openCardPaths.Count; index++)
      openCardPaths[index] = map(openCardPaths[index]);
  }

  internal void RetainPaths(IEnumerable<string> paths)
  {
    var retained = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
    openCardPaths.RemoveAll(path => !retained.Contains(path));
  }

  internal void UpdateCards(string root, IEnumerable<string> paths, string? selectedPath)
  {
    cardRoot = root;
    availablePaths = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
    currentPath = selectedPath;
    RebuildTabs();
  }

  private void RebuildTabs()
  {
    rebuilding = true;
    try
    {
      var oldTabs = TabPages.Cast<TabPage>().ToArray();
      TabPages.Clear();
      foreach (var tab in oldTabs) tab.Dispose();
      foreach (var path in openCardPaths)
      {
        if (!availablePaths.Contains(path)) continue;
        var name = Path.GetFileName(path);
        TabPages.Add(new TabPage(name)
        {
          AccessibleName = $"Open card tab {name}",
          Tag = path,
          ToolTipText = Path.GetRelativePath(cardRoot, path)
        });
      }
      var selected = TabPages.Cast<TabPage>().FirstOrDefault(tab =>
        string.Equals((string)tab.Tag!, currentPath, StringComparison.OrdinalIgnoreCase));
      if (selected is not null) SelectedTab = selected;
      Visible = TabCount > 0;
    }
    finally { rebuilding = false; }
    Invalidate();
  }

  private void CardTabsOnDrawItem(object? sender, DrawItemEventArgs eventArgs)
  {
    var tab = TabPages[eventArgs.Index];
    var tabBounds = GetTabRect(eventArgs.Index);
    var selected = eventArgs.Index == SelectedIndex;
    var background = selected ? SystemColors.Window : SystemColors.Control;
    using var backgroundBrush = new SolidBrush(background);
    eventArgs.Graphics.FillRectangle(backgroundBrush, tabBounds);
    if (selected)
    {
      // The one open-and-current tab carries the shared accent, same cue as the focused editor.
      using var accentBrush = new SolidBrush(ToolbarArtwork.Accent);
      eventArgs.Graphics.FillRectangle(accentBrush, tabBounds.Left, tabBounds.Top, tabBounds.Width, 2);
    }

    var closeBounds = GetTabCloseBounds(eventArgs.Index);
    var textBounds = new Rectangle(
      tabBounds.Left + 6,
      tabBounds.Top + 2,
      Math.Max(0, closeBounds.Left - tabBounds.Left - 8),
      tabBounds.Height - 4);
    TextRenderer.DrawText(
      eventArgs.Graphics,
      tab.Text,
      Font,
      textBounds,
      SystemColors.ControlText,
      TextFormatFlags.EndEllipsis | TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
    TextRenderer.DrawText(
      eventArgs.Graphics,
      "x",
      Font,
      closeBounds,
      SystemColors.ControlText,
      TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

    if (selected)
    {
      eventArgs.DrawFocusRectangle();
    }
  }

  private void CardTabsOnMouseDown(object? sender, MouseEventArgs eventArgs)
  {
    CancelTabDrag();
    if (eventArgs.Button != MouseButtons.Left)
    {
      return;
    }

    for (var index = 0; index < TabPages.Count; index++)
    {
      if (GetTabCloseBounds(index).Contains(eventArgs.Location)
          && TabPages[index].Tag is string filePath)
      {
        TabClosing?.Invoke(this, new CardTabEventArgs(filePath));
        return;
      }

      if (GetTabRect(index).Contains(eventArgs.Location)
          && TabPages[index].Tag is string draggedPath)
      {
        draggedCardPath = draggedPath;
        tabDragOrigin = eventArgs.Location;
        tabDragOriginalOrder = openCardPaths.ToArray();
        Capture = true;
        return;
      }
    }
  }

  private void CardTabsOnMouseMove(object? sender, MouseEventArgs eventArgs)
  {
    if (draggedCardPath is null || eventArgs.Button != MouseButtons.Left)
    {
      return;
    }

    var dragSize = SystemInformation.DragSize;
    var threshold = new Rectangle(
      tabDragOrigin.X - dragSize.Width / 2,
      tabDragOrigin.Y - dragSize.Height / 2,
      dragSize.Width,
      dragSize.Height);
    if (!tabDragStarted && !threshold.Contains(eventArgs.Location))
    {
      tabDragStarted = true;
      Cursor = Cursors.Default;
    }
    if (tabDragStarted)
    {
      MoveDraggedTab(eventArgs.Location);
    }
  }

  private void CardTabsOnMouseUp(object? sender, MouseEventArgs eventArgs)
  {
    var originalOrder = tabDragOriginalOrder;
    var committed = tabDragStarted && eventArgs.Button == MouseButtons.Left && IsInTabStrip(eventArgs.Location);
    if (committed)
    {
      MoveDraggedTab(eventArgs.Location);
      tabDragOriginalOrder = null;
    }
    CancelTabDrag();
    if (committed && originalOrder is not null && !openCardPaths.SequenceEqual(originalOrder))
    {
      OrderChanged?.Invoke(this, EventArgs.Empty);
    }
  }

  private bool IsInTabStrip(Point point) => TabCount > 0
    && point.X >= 0 && point.X < ClientSize.Width
    && point.Y >= GetTabRect(0).Top && point.Y < GetTabRect(0).Bottom;

  private void MoveDraggedTab(Point point)
  {
    if (draggedCardPath is null || !IsInTabStrip(point))
    {
      return;
    }

    var sourceIndex = IndexOf(draggedCardPath);
    if (sourceIndex < 0)
    {
      return;
    }
    var insertionIndex = sourceIndex;
    for (var index = 0; index < TabCount; index++)
    {
      var bounds = GetTabRect(index);
      var midpoint = bounds.Left + bounds.Width / 2;
      if (index < sourceIndex && point.X < midpoint)
      {
        insertionIndex = index;
        break;
      }
      if (index > sourceIndex && point.X >= midpoint)
      {
        insertionIndex = index;
      }
    }

    if (sourceIndex != insertionIndex)
    {
      openCardPaths.RemoveAt(sourceIndex);
      openCardPaths.Insert(insertionIndex, draggedCardPath);
      RebuildTabs();
    }
  }

  internal bool CancelTabDrag()
  {
    var hadDrag = draggedCardPath is not null;
    var originalOrder = tabDragOriginalOrder;
    tabDragOriginalOrder = null;
    draggedCardPath = null;
    tabDragStarted = false;
    Cursor = Cursors.Default;
    Capture = false;
    if (originalOrder is not null && !openCardPaths.SequenceEqual(originalOrder))
    {
      openCardPaths.Clear();
      openCardPaths.AddRange(originalOrder);
      RebuildTabs();
    }
    return hadDrag;
  }

  internal Rectangle GetTabCloseBounds(int index)
  {
    var tabBounds = GetTabRect(index);
    const int closeSize = 16;
    return new Rectangle(
      tabBounds.Right - closeSize - 4,
      tabBounds.Top + (tabBounds.Height - closeSize) / 2,
      closeSize,
      closeSize);
  }

}
