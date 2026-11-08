namespace PromptNavigator;

internal sealed class HelpWindow : Form
{
  // A group is a contents node with child entries. A section is a leaf entry.
  private abstract record HelpEntry(string Title, string Body);
  private sealed record HelpSection(string Title, string Body) : HelpEntry(Title, Body);
  private sealed record HelpGroup(string Title, string Body, HelpEntry[] Entries) : HelpEntry(Title, Body);

  private sealed record HelpPalette(
    Color Back,
    Color Surface,
    Color Text,
    Color Muted,
    Color Accent)
  {
    public static HelpPalette Read() => SystemInformation.HighContrast
      ? new HelpPalette(
          SystemColors.Window,
          SystemColors.Window,
          SystemColors.WindowText,
          SystemColors.WindowText,
          SystemColors.WindowText)
      : new HelpPalette(
          Color.White,
          Color.FromArgb(244, 246, 250),
          Color.FromArgb(16, 24, 40),
          Color.FromArgb(84, 96, 116),
          Color.FromArgb(0, 70, 173));
  }

  private static readonly HelpGroup Contents = new HelpGroup("Cards", """
    Prompt Flipper keeps prompts as cards.
    Each card is a text file in a selected folder.
    Edit template prompts and copy them to any AI tool.
    The Sanitizer can replace secrets before you share a prompt.
    """,
  [
    new HelpGroup("Toolbar", """
      What the buttons do.
      """,
    [
      new HelpSection("Attach Folder", """
        Select Attach Folder or press Ctrl+O to select a folder with cards.
        Prompt Flipper shows each file in that folder as a card.
        Press Ctrl+Shift+O to add a file from a template.
        Prompt Flipper adds a copy of the template to the attached folder as a new card.
        The template and the attached folder stay as they are.
        """),
      new HelpSection("Save", """
        Press Save or Ctrl+S to save the active card.
        Prompt Flipper also saves when you change cards or close the application.
        """),
      new HelpSection("Print", """
        Press Print or Ctrl+P to print the active card.
        """),
      new HelpSection("Search", """
        A. Select Search or press Ctrl+F.
        B. Type a few letters to show the matching cards.
        C. Select a result to open that card.

        Press Esc to clear the search text and close the results.
        """),
      new HelpSection("Wrap", """
        Choose Wrap to shorten scrolling of long lines.
        Wrap changes the editor view only. It does not change card text.
        """),
      new HelpSection("Text size", """
        Select Text size, then select a size from 8 to 24 points.
        The size changes the editor view only. It does not change card text.
        """),
      new HelpSection("Settings", """
        Configure the Sanitizer here.
        You manage your secrets here. Windows encryption protects the stored secrets.
        """)
    ]),
    new HelpGroup("Tabs", """
      An active card has one tab.
      """,
    [
      new HelpSection("Tabs control", """
        Select x on a tab to close it.
        Prompt Flipper auto saves if you close.
        Drag tabs to re-order active tabs.
        Closing tabs does not delete cards.
        """)
    ]),
    new HelpGroup("Editor", """
      Content of active cards.
      """,
    [
      new HelpSection("Edit text", """
        Each card is just a plain text file.
        """),
      new HelpSection("Rename files", """
        1. Select the card name above the editor or press F2.
        2. Change the file name.
        3. Press Enter to save or just click mouse button anywhere else.
        Esc to keep the old name.
        """),
      new HelpSection("Flip cards", """
        Previous/Next or Alt+Left/Right to navigate.
        """)
    ]),
    new HelpGroup("Quick Buttons", """
      Quick buttons are located right above the card text.
      """,
    [
      new HelpSection("Copy", """
        Select Copy to copy all text of an active card.
        Paste card contents into any AI tool.
        """),
      new HelpSection("Sanitizer", """
        Sanitizer replaces your saved secrets with placeholders. Don't forget to review the result.
        De-Sanitize puts back original placeholder values.
        Review in Settings shows the result and lists personal details that are not sanitized yet.
        Select Add as secret or Add all to replace them from then on.
        """),
      new HelpSection("Trash", """
        Select Trash to delete any active card. Prompt Flipper will ask to confirm.
        Deletion is permanent without option to be restored.
        """),
      new HelpSection("Add Card", """
        The plus-image is to add a new card in the attached folder with the name new-prompt.md.
        """)
    ]),
    new HelpSection("Shortcuts", """
      F1 = Help
      F2 = Rename
      Ctrl+S = Save
      Ctrl+O = Attach Folder
      Ctrl+Shift+O = Add file from template
      Ctrl+P = Print
      Ctrl+F = Search
      Alt+Left/Right = previous/next card
      Alt+Enter = File properties
      Esc = Cancel
      """),
    new HelpSection("About", AboutDialog.HelpText())
  ]);

  [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
  private static extern int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);

  private static TreeNode BuildNode(HelpEntry entry)
  {
    var node = new TreeNode(entry.Title) { Tag = entry };
    if (entry is HelpGroup group)
      foreach (var child in group.Entries) node.Nodes.Add(BuildNode(child));
    return node;
  }

  private static IEnumerable<HelpEntry> Flatten(HelpEntry entry) => entry is HelpGroup group
    ? group.Entries.SelectMany(Flatten).Prepend(entry)
    : [entry];

  private readonly TreeView topics;
  private readonly TextBox search;

  // An empty query shows the full contents tree; otherwise a flat list of matching topics.
  private void RefreshTopics()
  {
    topics.BeginUpdate();
    try
    {
      topics.Nodes.Clear();
      if (PromptFlipper.TextSearch.Terms(search.Text).Length == 0)
      {
        topics.Nodes.Add(BuildNode(Contents));
        topics.ExpandAll();
      }
      else
      {
        foreach (var entry in PromptFlipper.TextSearch.Filter(Flatten(Contents), search.Text,
          entry => [entry.Title, entry.Body]))
          topics.Nodes.Add(new TreeNode(entry.Title) { Tag = entry });
      }
    }
    finally
    {
      topics.EndUpdate();
    }
    if (topics.Nodes.Count > 0) topics.SelectedNode = topics.Nodes[0];
  }

  public HelpWindow()
  {
    var palette = HelpPalette.Read();
    Text = "Help";
    Icon = ToolbarArtwork.LoadIcon("PromptFlipper.ico");
    ShowIcon = true;
    Disposed += (_, _) => Icon?.Dispose();
    StartPosition = FormStartPosition.CenterParent;
    FormBorderStyle = FormBorderStyle.Sizable;
    ShowInTaskbar = false;
    AutoScaleDimensions = new SizeF(96f, 96f);
    AutoScaleMode = AutoScaleMode.Dpi;
    ClientSize = new Size(740, 500);
    MinimumSize = new Size(600, 420);
    Font = new Font(SystemFonts.MessageBoxFont!, FontStyle.Regular);
    BackColor = palette.Back;
    ForeColor = palette.Text;
    KeyPreview = true;

    var content = new RichTextBox
    {
      AccessibleName = "Help topic",
      BackColor = palette.Surface,
      BorderStyle = BorderStyle.FixedSingle,
      DetectUrls = false,
      Dock = DockStyle.Fill,
      ForeColor = palette.Text,
      Multiline = true,
      ReadOnly = true,
      ScrollBars = RichTextBoxScrollBars.Vertical,
      TabStop = true,
      TabIndex = 0,
      WordWrap = true
    };

    var topicTitle = new Label
    {
      Text = "Description",
      AutoSize = true,
      Dock = DockStyle.Top,
      Font = new Font(Font, FontStyle.Bold),
      Padding = new Padding(0, 0, 0, 8)
    };
    topicTitle.Disposed += (_, _) => topicTitle.Font.Dispose();
    topics = new TreeView
    {
      AccessibleName = "Help topics",
      Dock = DockStyle.Fill,
      BackColor = palette.Surface,
      ForeColor = palette.Text,
      BorderStyle = BorderStyle.FixedSingle,
      FullRowSelect = true,
      HideSelection = false,
      ShowLines = false,
      ShowPlusMinus = true,
      ShowRootLines = false,
      Indent = 16,
      ItemHeight = Font.Height + 10,
      TabIndex = 0
    };
    // The Explorer theme draws Windows 11 chevrons and hover rows instead of classic +/- boxes.
    topics.HandleCreated += (_, _) => SetWindowTheme(topics.Handle, "Explorer", null);
    topics.Nodes.Add(BuildNode(Contents));
    topics.ExpandAll();
    topics.AfterSelect += (_, args) =>
    {
      if (args.Node?.Tag is not HelpEntry entry) return;
      content.AccessibleName = entry.Title + " help";
      // RichTextBox stores line breaks as a single \n, so highlight offsets match this text.
      content.Text = entry.Body.ReplaceLineEndings("\n");
      var first = -1;
      foreach (var (start, length) in PromptFlipper.TextSearch.Ranges(content.Text, search.Text))
      {
        content.Select(start, length);
        content.SelectionBackColor = SystemColors.Highlight;
        content.SelectionColor = SystemColors.HighlightText;
        if (first < 0 || start < first) first = start;
      }
      content.Select(Math.Max(first, 0), 0);
      content.ScrollToCaret();
    };

    var readingPane = new Panel { Dock = DockStyle.Fill, TabIndex = 1 };
    readingPane.Controls.Add(content);
    readingPane.Controls.Add(topicTitle);
    var contentsTitle = new Label
    {
      Text = "Contents",
      AutoSize = true,
      Dock = DockStyle.Top,
      Padding = new Padding(0, 0, 0, 8)
    };
    search = SearchField.Create(SearchProfile.Load("help"), out var searchFrame);
    search.TextChanged += (_, _) => RefreshTopics();
    // Tab order in the contents pane: search, then topics, then on to the reading pane.
    searchFrame.TabIndex = 0;
    topics.TabIndex = 1;
    search.KeyDown += (_, args) =>
    {
      if (args.KeyCode != Keys.Down || topics.Nodes.Count == 0) return;
      topics.Focus();
      args.Handled = true;
    };
    var contentsPane = new Panel { Dock = DockStyle.Fill, TabIndex = 0 };
    contentsPane.Controls.Add(topics);
    contentsPane.Controls.Add(searchFrame);
    contentsPane.Controls.Add(contentsTitle);
    var split = new SplitContainer
    {
      Dock = DockStyle.Fill,
      Size = new Size(716, 300),
      FixedPanel = FixedPanel.Panel1,
      Panel1MinSize = 150,
      Panel2MinSize = 250,
      SplitterDistance = 180,
      SplitterWidth = 8,
      TabStop = false
    };
    split.Panel1.Controls.Add(contentsPane);
    split.Panel2.Controls.Add(readingPane);

    var body = new TableLayoutPanel
    {
      ColumnCount = 1,
      Dock = DockStyle.Fill,
      Padding = new Padding(12, 4, 12, 0),
      RowCount = 1,
      TabIndex = 0
    };
    body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
    body.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
    body.Controls.Add(split, 0, 0);
    content.Margin = Padding.Empty;

    var hint = new Label
    {
      AutoSize = true,
      Dock = DockStyle.Bottom,
      ForeColor = palette.Muted,
      Padding = new Padding(12, 6, 12, 0),
      Text = "Up/Down: choose entry   Left/Right: collapse or expand   Tab: read   Esc: close"
    };

    var closeButton = new Button
    {
      Anchor = AnchorStyles.Right,
      AutoSize = true,
      Text = "Close"
    };
    if (!SystemInformation.HighContrast)
    {
      closeButton.FlatStyle = FlatStyle.Flat;
      closeButton.BackColor = palette.Surface;
      closeButton.ForeColor = palette.Text;
      closeButton.FlatAppearance.BorderColor = palette.Accent;
    }

    closeButton.Click += (_, _) => Close();

    var buttons = new FlowLayoutPanel
    {
      AutoSize = true,
      Dock = DockStyle.Bottom,
      FlowDirection = FlowDirection.RightToLeft,
      Padding = new Padding(8)
    };
    buttons.Controls.Add(closeButton);

    Controls.Add(body);
    Controls.Add(hint);
    Controls.Add(buttons);

    buttons.TabIndex = 1;
    AcceptButton = closeButton;
    CancelButton = closeButton;

    topics.SelectedNode = topics.Nodes[0];
    ActiveControl = topics;
    Shown += (_, _) => topics.Focus();
  }

  protected override bool ProcessCmdKey(ref Message message, Keys keyData)
  {
    if (keyData == Keys.Escape)
    {
      Close();
      return true;
    }

    return base.ProcessCmdKey(ref message, keyData);
  }
}
