using System.Drawing.Printing;
using System.Runtime.InteropServices;

namespace PromptNavigator;

internal sealed class MainForm : Form
{
  private readonly CardRepository cardRepository = new();
  private readonly SettingsStore settingsStore;
  private readonly Label cardHeader = new();
  private readonly TextBox cardNameEditor = new();
  private readonly PromptFlipper.SanitizedPromptEditor cardPane = new();
  private readonly Button sanitizeToggle = new();
  private readonly SuccessFeedback successFeedback;
  private Image? copyArtwork;
  private readonly ToolStripStatusLabel fileStatusLabel = new();
  private readonly ToolStripStatusLabel collectionStatusLabel = new();
  private readonly MenuStrip menuStrip = new();
  private readonly Label pageLabel = new();
  private readonly Button previousButton = new();
  private readonly Button nextButton = new();
  private readonly Button copyButton = new();
  private readonly ToolTip chromeToolTip = new();
  private readonly CardSearch cardSearch;
  private ToolStripButton? searchToggle;
  private readonly CardTabStrip cardTabs = new();
  private readonly CardFolderWatcher cardWatcher;

  private string cardRoot;
  private IReadOnlyList<Card> cards;
  private int currentIndex;
  private string? pendingSelectedPath;
  private HelpWindow? helpWindow;

  private readonly Func<IEnumerable<PromptFlipper.SanitizerRule>> loadSanitizerRules;
  private readonly Action<IEnumerable<PromptFlipper.SanitizerRule>> saveSanitizerRules;

  public MainForm(string cardRoot, IReadOnlyList<Card> cards)
    : this(cardRoot, cards, new ProtectedRuleStore().LoadRules, new ProtectedRuleStore().Save, new SettingsStore())
  {
  }

  // Tests pass their own settings file so they never touch the user's profile.
  internal MainForm(string cardRoot, IReadOnlyList<Card> cards, string settingsPath)
    : this(cardRoot, cards, new ProtectedRuleStore().LoadRules, new ProtectedRuleStore().Save,
      new SettingsStore(settingsPath))
  {
  }

  internal MainForm(string cardRoot, IReadOnlyList<Card> cards,
    Func<IEnumerable<PromptFlipper.SanitizerRule>> loadSanitizerRules,
    Action<IEnumerable<PromptFlipper.SanitizerRule>> saveSanitizerRules,
    string settingsPath)
    : this(cardRoot, cards, loadSanitizerRules, saveSanitizerRules, new SettingsStore(settingsPath))
  {
  }

  private MainForm(string cardRoot, IReadOnlyList<Card> cards,
    Func<IEnumerable<PromptFlipper.SanitizerRule>> loadSanitizerRules,
    Action<IEnumerable<PromptFlipper.SanitizerRule>> saveSanitizerRules,
    SettingsStore settingsStore)
  {
    this.settingsStore = settingsStore;
    this.loadSanitizerRules = loadSanitizerRules;
    this.saveSanitizerRules = saveSanitizerRules;
    this.cardRoot = cardRoot;
    this.cards = cards;
    currentIndex = cards.Count > 0 ? 0 : -1;

    Text = "Prompt Flipper";
    Icon = LoadApplicationIcon();
    StartPosition = FormStartPosition.CenterScreen;
    ClientSize = new Size(820, 520);
    MinimumSize = new Size(560, 360);
    BackColor = SystemColors.Control;
    Font = ToolbarArtwork.UiFont;
    KeyPreview = true;
    KeyDown += MainFormOnKeyDown;
    cardWatcher = new CardFolderWatcher(this);
    cardWatcher.Changed += (_, changes) =>
    {
      cardTabs.MapPaths(changes.MapPath);
      if (CurrentCard is { } card)
        pendingSelectedPath = changes.MapPath(pendingSelectedPath ?? card.FilePath);
      RefreshCardsFromDisk();
    };

    cardSearch = new CardSearch(DeviceDpi);
    cardSearch.CardChosen += (_, args) => SelectCard(args.FilePath);
    cardSearch.Dismissed += (_, _) => cardPane.Focus();

    BuildMenu();
    var toolbar = BuildToolbar();
    cardSearch.SetCards(cards);
    MainMenuStrip = menuStrip;

    cardTabs.TabSelected += (_, eventArgs) => SelectCard(eventArgs.FilePath);
    cardTabs.TabClosing += (_, eventArgs) => CloseCardTab(eventArgs.FilePath);
    cardTabs.OrderChanged += (_, _) => UpdateStatusStrip();

    cardHeader.AutoEllipsis = true;
    cardHeader.BackColor = SystemColors.Control;
    cardHeader.BorderStyle = BorderStyle.None;
    cardHeader.Dock = DockStyle.Fill;
    cardHeader.Font = new Font(Font, FontStyle.Regular);
    cardHeader.Padding = new Padding(ToolbarArtwork.Spacing(ToolbarArtwork.SpacingStep.XS), 0, ToolbarArtwork.Spacing(ToolbarArtwork.SpacingStep.S), 0);
    cardHeader.TextAlign = ContentAlignment.MiddleLeft;
    cardHeader.Cursor = Cursors.IBeam;
    cardHeader.MouseDown += (_, eventArgs) =>
    {
      if (eventArgs.Button == MouseButtons.Left)
      {
        BeginRenameCurrentCard();
      }
    };

    cardNameEditor.BorderStyle = BorderStyle.FixedSingle;
    cardNameEditor.AutoSize = false;
    cardNameEditor.AccessibleName = "Card file name";
    cardNameEditor.Visible = false;
    cardNameEditor.KeyDown += CardNameEditorOnKeyDown;
    cardNameEditor.Leave += (_, _) => CommitCurrentCardRename();

    cardPane.Dock = DockStyle.Fill;
    cardPane.Multiline = true;
    cardPane.AcceptsTab = true;
    cardPane.ScrollBars = RichTextBoxScrollBars.Vertical;
    cardPane.WordWrap = true;
    cardPane.BorderStyle = BorderStyle.None;
    cardPane.BackColor = SystemColors.Window;
    cardPane.Font = new Font(FontFamily.GenericMonospace, 10f);
    cardPane.Margin = new Padding(0);
    cardPane.TextChanged += (_, _) => { UpdateStatusStrip(); UpdateCopyButton(); };
    cardPane.SanitizedChanged += (_, _) => UpdateSanitizeToggle();
    cardPane.ProtectedEditRejected += (_, _) =>
      fileStatusLabel.Text = "Placeholders are protected. Use De-Sanitize before editing them.";

    // Unfocused: a plain 1px frame. Focused: the shared accent marks it as the current field.
    var editorFrame = new Panel
    {
      Dock = DockStyle.Fill,
      BackColor = SystemColors.Window,
      BorderStyle = BorderStyle.None,
      Margin = new Padding(0),
      Padding = new Padding(ToolbarArtwork.Spacing(ToolbarArtwork.SpacingStep.XS))
    };
    editorFrame.Paint += (_, eventArgs) =>
    {
      using var pen = new Pen(cardPane.Focused ? ToolbarArtwork.Accent : SystemColors.ControlDark,
        cardPane.Focused ? 2 : 1);
      var rectangle = new Rectangle(0, 0, editorFrame.Width - 1, editorFrame.Height - 1);
      eventArgs.Graphics.DrawRectangle(pen, rectangle);
    };
    cardPane.Enter += (_, _) => editorFrame.Invalidate();
    cardPane.Leave += (_, _) => editorFrame.Invalidate();
    successFeedback = new SuccessFeedback(this, editorFrame, ResetCopyFeedback);
    editorFrame.Controls.Add(cardPane);



    var navigationPanel = BuildNavigationPanel();
    var layout = new TableLayoutPanel
    {
      ColumnCount = 1,
      RowCount = 7,
      Dock = DockStyle.Fill,
      Padding = new Padding(ToolbarArtwork.Spacing(ToolbarArtwork.SpacingStep.S)),
      BackColor = SystemColors.Control
    };
    layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
    layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    // The header row holds the quick actions plus their margins at the current scale.
    layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ToolbarArtwork.Scale(
      ToolbarArtwork.QuickButtonSize.Height + 2 * ToolbarArtwork.QuickButtonMargin, DeviceDpi)));
    layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
    layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24f));
    layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ToolbarArtwork.Scale(
      ToolbarArtwork.QuickButtonSize.Height + 2 * ToolbarArtwork.QuickButtonMargin, DeviceDpi)));
    layout.Controls.Add(menuStrip, 0, 0);
    layout.Controls.Add(toolbar, 0, 1);
    layout.Controls.Add(cardTabs, 0, 2);
    layout.Controls.Add(BuildHeaderPanel(), 0, 3);
    layout.Controls.Add(editorFrame, 0, 4);
    layout.Controls.Add(BuildStatusStrip(), 0, 5);
    layout.Controls.Add(navigationPanel, 0, 6);

    Controls.Add(layout);
    RestoreSession();
    ShowCurrentCard();
  }

  // Reopens the tabs saved at the last exit, skipping cards that no longer exist.
  private void RestoreSession()
  {
    if (settingsStore.LoadSession(cardRoot) is not { } session)
    {
      return;
    }

    foreach (var path in session.OpenCards)
    {
      if (FindCardIndex(path) is var index and >= 0) cardTabs.Open(cards[index].FilePath);
    }

    var activeIndex = session.ActiveCard is null ? -1 : FindCardIndex(session.ActiveCard);
    if (activeIndex >= 0) currentIndex = activeIndex;
    else if (cardTabs.Paths.Count > 0) currentIndex = FindCardIndex(cardTabs.Paths[0]);
  }

  protected override void OnShown(EventArgs eventArgs)
  {
    base.OnShown(eventArgs);
    WatchCardRoot();
  }

  private static Icon? LoadApplicationIcon() => ToolbarArtwork.LoadIcon("PromptFlipper.ico");

  private Image? LoadCommandIcon(string command, int logicalSize = 16) =>
    ToolbarArtwork.LoadCommandIcon(command, logicalSize, DeviceDpi);

  private Image? LoadSupportIcon(int logicalSize) => ToolbarArtwork.LoadSupportIcon(logicalSize, DeviceDpi);

  private void BuildMenu()
  {
    var save = new ToolStripMenuItem("&Save", LoadCommandIcon("Save"), (_, _) => TrySaveCurrentCard())
    {
      ShortcutKeys = Keys.Control | Keys.S
    };
    var openFolder = new ToolStripMenuItem("Open &Folder...", LoadCommandIcon("Open"), (_, _) => OpenFolder())
    {
      ShortcutKeys = Keys.Control | Keys.O
    };
    var openFile = new ToolStripMenuItem("Add F&ile from Template...", LoadCommandIcon("Open"), (_, _) => AddFileFromTemplate())
    {
      ShortcutKeys = Keys.Control | Keys.Shift | Keys.O
    };
    var print = new ToolStripMenuItem("&Print...", LoadCommandIcon("Print"), (_, _) => PrintCurrentCard())
    {
      ShortcutKeys = Keys.Control | Keys.P
    };
    var properties = new ToolStripMenuItem("P&roperties", null, (_, _) => ShowProperties())
    {
      ShortcutKeys = Keys.Alt | Keys.Enter
    };
    var close = new ToolStripMenuItem("&Close", LoadCommandIcon("Exit"), (_, _) => Close())
    {
      ShortcutKeys = Keys.Alt | Keys.F4
    };

    var fileMenu = new ToolStripMenuItem("&File");
    fileMenu.DropDownItems.AddRange([
      save,
      new ToolStripSeparator(),
      openFolder,
      openFile,
      new ToolStripSeparator(),
      print,
      properties,
      new ToolStripSeparator(),
      close
    ]);

    var sanitize = new ToolStripMenuItem("&Sanitizer...", LoadCommandIcon("Sanitize"), (_, _) => ShowSanitizer());
    var editMenu = new ToolStripMenuItem("&Edit");
    editMenu.DropDownItems.Add(sanitize);

    var viewHelp = new ToolStripMenuItem("View &Help", LoadCommandIcon("Help"), (_, _) => ShowHelp())
    {
      ShortcutKeys = Keys.F1
    };
    var about = new ToolStripMenuItem("&About Prompt Flipper", LoadCommandIcon("About"), (_, _) => ShowAbout());
    var helpMenu = new ToolStripMenuItem("&Help");
    helpMenu.DropDownItems.AddRange([viewHelp, new ToolStripSeparator(), about]);

    menuStrip.Dock = DockStyle.Fill;
    menuStrip.GripStyle = ToolStripGripStyle.Hidden;
    menuStrip.Margin = new Padding(0, 0, 0, ToolbarArtwork.Spacing(ToolbarArtwork.SpacingStep.XS));
    menuStrip.RenderMode = ToolStripRenderMode.System;
    menuStrip.Visible = false;
    menuStrip.Items.AddRange([fileMenu, editMenu, helpMenu]);
    menuStrip.MenuDeactivate += (_, _) => menuStrip.Visible = false;

    // The artwork is already picked at the DPI size, so the strip must not rescale it.
    menuStrip.ImageScalingSize = new Size(16 * DeviceDpi / 96, 16 * DeviceDpi / 96);
    foreach (var item in new[] { fileMenu, editMenu, helpMenu })
    {
      foreach (var dropDownItem in item.DropDownItems)
      {
        if (dropDownItem is ToolStripMenuItem { Image: not null } withIcon)
        {
          withIcon.ImageScaling = ToolStripItemImageScaling.None;
        }
      }
    }

  }

  private ToolStrip BuildToolbar()
  {
    var openFolder = CreateToolbarButton(
      "Open",
      "Open",
      "Attach Folder",
      (_, _) => OpenFolder(),
      showText: false);
    var save = CreateToolbarButton(
      "Save",
      "Save",
      "Save",
      (_, _) => TrySaveCurrentCard(),
      showText: false);
    var print = CreateToolbarButton(
      "Print",
      "Print",
      "Print",
      (_, _) => PrintCurrentCard(),
      showText: false);

    searchToggle = CreateToolbarButton("Search", "Search", "Search",
      (_, _) => cardSearch.Show(searchToggle), showText: false);
    searchToggle.Overflow = ToolStripItemOverflow.Never;

    var wordWrap = ToolbarArtwork.CreateImageToggle("wrap", "Wrap",initiallyChecked: true,
      ToolbarArtwork.ToolbarToggleIconSize);
    chromeToolTip.SetToolTip(wordWrap, "Wrap");
    var wrapControl = new ToolStripControlHost(wordWrap)
    {
      ToolTipText = "Wrap",
      Overflow = ToolStripItemOverflow.Never
    };
    wordWrap.CheckedChanged += (_, _) =>
    {
      cardPane.WordWrap = wordWrap.Checked;
      cardPane.ScrollBars = wordWrap.Checked ? RichTextBoxScrollBars.Vertical : RichTextBoxScrollBars.Both;
    };
    var textSizeMenu = new ToolStripDropDownButton
    {
      Image = LoadCommandIcon("TextSize", ToolbarArtwork.ToolbarIconSize),
      ImageScaling = ToolStripItemImageScaling.None,
      DisplayStyle = ToolStripItemDisplayStyle.Image,
      AccessibleName = "Text Size",
      ToolTipText = "Text Size",
      Overflow = ToolStripItemOverflow.Never
    };
    const int defaultPointSize = 10;
    for (var size = 8; size <= 24; size++)
    {
      var pointSize = size;
      var sizeItem = new ToolStripMenuItem($"{pointSize} pt") { Checked = pointSize == defaultPointSize };
      sizeItem.Click += (_, _) =>
      {
        foreach (var sibling in textSizeMenu.DropDownItems.OfType<ToolStripMenuItem>())
        {
          sibling.Checked = false;
        }
        sizeItem.Checked = true;
        var previousFont = cardPane.Font;
        cardPane.Font = new Font(FontFamily.GenericMonospace, pointSize);
        previousFont.Dispose();
      };
      textSizeMenu.DropDownItems.Add(sizeItem);
    }
    var help = CreateToolbarButton(
      "Help",
      "Help",
      "Help",
      (_, _) => ShowHelp(),
      showText: false);
    var sanitizerSetup = CreateToolbarButton("Settings", "Settings", "Settings",
      (_, _) => ShowSanitizer(), showText: false);
    sanitizerSetup.Overflow = ToolStripItemOverflow.Never;
    var support = new ToolStripButton(
      string.Empty,
      LoadSupportIcon(ToolbarArtwork.ToolbarIconSize),
      (_, _) => SupportLink.Open(this, Text))
    {
      AccessibleName = "Support",
      Alignment = ToolStripItemAlignment.Right,
      DisplayStyle = ToolStripItemDisplayStyle.Image,
      ImageScaling = ToolStripItemImageScaling.None,
      ToolTipText = "Support"
    };

    var toolbar = new ToolStrip
    {
      AutoSize = true,
      Dock = DockStyle.Fill,
      GripStyle = ToolStripGripStyle.Hidden,
      Margin = new Padding(0, 0, 0, ToolbarArtwork.Spacing(ToolbarArtwork.SpacingStep.XS)),
      RenderMode = ToolStripRenderMode.System,
      ImageScalingSize = ToolbarArtwork.Scale(new Size(ToolbarArtwork.ToolbarIconSize, ToolbarArtwork.ToolbarIconSize), DeviceDpi)
    };
    toolbar.Items.AddRange([
      openFolder,
      save,
      print,
      new ToolStripSeparator(),
      searchToggle,
      new ToolStripSeparator(),
      wrapControl,
      textSizeMenu,
      sanitizerSetup,
      help,
      support
    ]);
    return toolbar;
  }

  private ToolStripButton CreateToolbarButton(
    string text,
    string iconName,
    string accessibleName,
    EventHandler clickHandler,
    bool showText = true)
  {
    return new ToolStripButton(text, LoadCommandIcon(iconName, ToolbarArtwork.ToolbarIconSize), clickHandler)
    {
      AccessibleName = accessibleName,
      DisplayStyle = showText
        ? ToolStripItemDisplayStyle.ImageAndText
        : ToolStripItemDisplayStyle.Image,
      ImageScaling = ToolStripItemImageScaling.None,
      ToolTipText = accessibleName
    };
  }

  private void SelectCard(string filePath)
  {
    if (!TrySaveCurrentCard())
    {
      return;
    }

    var selectedIndex = FindCardIndex(filePath);
    if (selectedIndex < 0) return;
    currentIndex = selectedIndex;
    ShowCurrentCard();
  }

  private int FindCardIndex(string path) => FindCardIndex(cards, path);

  private static int FindCardIndex(IReadOnlyList<Card> cards, string path)
  {
    for (var index = 0; index < cards.Count; index++)
      if (string.Equals(cards[index].FilePath, path, StringComparison.OrdinalIgnoreCase)) return index;
    return -1;
  }

  private void OpenCurrentCardTab()
  {
    if (CurrentCard is { } card) cardTabs.Open(card.FilePath);
    RebuildCardTabs();
  }

  private void RebuildCardTabs() => cardTabs.UpdateCards(
    cardRoot, cards.Select(card => card.FilePath), CurrentCard?.FilePath);

  private void CloseCardTab(string filePath)
  {
    var tabIndex = cardTabs.IndexOf(filePath);
    if (tabIndex < 0)
    {
      return;
    }

    var closesCurrentCard = CurrentCard is { } card
      && string.Equals(card.FilePath, filePath, StringComparison.OrdinalIgnoreCase);
    if (closesCurrentCard && !TrySaveCurrentCard())
    {
      return;
    }

    cardTabs.Remove(filePath);
    if (closesCurrentCard && cardTabs.Paths.Count > 0)
    {
      SelectCard(cardTabs.Paths[Math.Min(tabIndex, cardTabs.Paths.Count - 1)]);
      return;
    }

    RebuildCardTabs();
  }

  private void RenameOpenCardTab(string oldPath, string newPath) => cardTabs.Rename(oldPath, newPath);

  private void RemoveOpenCardTab(string filePath) => cardTabs.Remove(filePath);

  private void SynchronizeOpenCardTabs() => cardTabs.RetainPaths(cards.Select(card => card.FilePath));

  private Control BuildNavigationPanel()
  {
    ConfigureChromeButton(
      previousButton,
      string.Empty,
      "Previous Card",
      (_, _) => MoveCard(-1),
      ToolbarArtwork.Scale(ToolbarArtwork.QuickButtonSize, DeviceDpi));
    ConfigureChromeButton(
      nextButton,
      string.Empty,
      "Next Card",
      (_, _) => MoveCard(1),
      ToolbarArtwork.Scale(ToolbarArtwork.QuickButtonSize, DeviceDpi));

    previousButton.Image = LoadCommandIcon("Previous", ToolbarArtwork.QuickIconSize);
    nextButton.Image = LoadCommandIcon("Next", ToolbarArtwork.QuickIconSize);

    pageLabel.AutoSize = false;
    pageLabel.Font = new Font(FontFamily.GenericMonospace, 9f);
    pageLabel.Margin = new Padding(2);
    pageLabel.Size = new Size(ToolbarArtwork.Scale(96, DeviceDpi),
      ToolbarArtwork.Scale(ToolbarArtwork.QuickButtonSize.Height, DeviceDpi));
    pageLabel.TextAlign = ContentAlignment.MiddleCenter;

    var navigationActions = new FlowLayoutPanel
    {
      AutoSize = true,
      Dock = DockStyle.Fill,
      FlowDirection = FlowDirection.LeftToRight,
      Margin = new Padding(0),
      Padding = new Padding(0),
      WrapContents = false
    };
    navigationActions.Controls.AddRange([previousButton, pageLabel, nextButton]);

    var navigationPanel = new TableLayoutPanel
    {
      ColumnCount = 3,
      RowCount = 1,
      Dock = DockStyle.Fill,
      Margin = new Padding(0)
    };
    navigationPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
    navigationPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
    navigationPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
    navigationPanel.Controls.Add(navigationActions, 1, 0);
    return navigationPanel;
  }

  private Control BuildHeaderPanel()
  {
    var editingLabel = new Label
    {
      AutoSize = true,
      Dock = DockStyle.Fill,
      Font = new Font(Font, FontStyle.Regular),
      Margin = new Padding(4, 0, 0, 0),
      Text = "Editing:",
      TextAlign = ContentAlignment.MiddleLeft
    };

    // Quick actions share one DPI-scaled size large enough to identify each artwork.
    var quickSize = ToolbarArtwork.Scale(ToolbarArtwork.QuickButtonSize, DeviceDpi);
    var addCardButton = new Button();
    ConfigureChromeButton(
      addCardButton,
      "+",
      "Add Card",
      (_, _) => AddCard(),
      quickSize);
    addCardButton.Font = new Font(Font.FontFamily, 13f, FontStyle.Regular);

    var removeCardButton = new Button();
    ConfigureChromeButton(
      removeCardButton,
      string.Empty,
      "Trash",
      (_, _) => RemoveCurrentCard(),
      quickSize);
    removeCardButton.Image = LoadCommandIcon("Delete", ToolbarArtwork.QuickIconSize);
    removeCardButton.TextImageRelation = TextImageRelation.ImageBeforeText;

    ConfigureChromeButton(copyButton, string.Empty, "Copy",
      (_, _) => CopyCurrentPrompt(), quickSize);
    copyArtwork = copyButton.Image = LoadCommandIcon("Copy", ToolbarArtwork.QuickIconSize);
    // One toggle: it sanitizes a plain editor and de-sanitizes a sanitized one.
    ConfigureChromeButton(sanitizeToggle, string.Empty, "Sanitizer", (_, _) =>
    {
      if (cardPane.IsSanitized) DesanitizeCurrentPrompt();
      else QuickSanitizeCurrentPrompt();
    }, quickSize);
    sanitizeArtwork = LoadQuickIcon("sanitize_button", ToolbarArtwork.QuickIconSize);
    deSanitizeArtwork = LoadQuickIcon("de_sanitize_button", ToolbarArtwork.QuickIconSize);
    UpdateSanitizeToggle();

    var cardNamePanel = new Panel
    {
      Dock = DockStyle.Fill,
      Margin = new Padding(0)
    };
    cardNamePanel.Controls.Add(cardHeader);
    cardNamePanel.Controls.Add(cardNameEditor);
    cardNamePanel.Resize += (_, _) => SizeCardNameEditor();

    var cardActions = new FlowLayoutPanel
    {
      AutoSize = true,
      Dock = DockStyle.Fill,
      FlowDirection = FlowDirection.LeftToRight,
      Margin = new Padding(0),
      Padding = new Padding(0),
      WrapContents = false
    };
    cardActions.Controls.AddRange([
      copyButton,
      sanitizeToggle,
      removeCardButton,
      addCardButton
    ]);

    var headerPanel = new TableLayoutPanel
    {
      ColumnCount = 3,
      RowCount = 1,
      Dock = DockStyle.Fill,
      BackColor = SystemColors.Control,
      Margin = new Padding(0)
    };
    headerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
    headerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
    headerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
    headerPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
    headerPanel.Controls.Add(editingLabel, 0, 0);
    headerPanel.Controls.Add(cardNamePanel, 1, 0);
    headerPanel.Controls.Add(cardActions, 2, 0);
    return headerPanel;
  }

  private Image? sanitizeArtwork;
  private Image? deSanitizeArtwork;

  // The toggle names and shows the action its next click performs.
  private void UpdateSanitizeToggle()
  {
    var sanitized = cardPane.IsSanitized;
    var name = sanitized ? "De-Sanitize" : "Sanitizer";
    sanitizeToggle.Image = sanitized ? deSanitizeArtwork : sanitizeArtwork;
    sanitizeToggle.AccessibleName = name;
    chromeToolTip.SetToolTip(sanitizeToggle, name);
  }

  private void ConfigureChromeButton(
    Button button,
    string text,
    string accessibleName,
    EventHandler clickHandler,
    Size? fixedSize = null)
  {
    button.AutoSize = fixedSize is null;
    if (fixedSize is Size size)
    {
      button.Size = size;
    }
    button.Margin = new Padding(ToolbarArtwork.QuickButtonMargin);
    button.Text = text;
    button.AccessibleName = accessibleName;
    chromeToolTip.SetToolTip(button, accessibleName);
    button.Click += clickHandler;
  }

  private Control BuildStatusStrip()
  {
    fileStatusLabel.Spring = true;
    fileStatusLabel.TextAlign = ContentAlignment.MiddleLeft;

    var statusStrip = new StatusStrip
    {
      Dock = DockStyle.Fill,
      Margin = new Padding(0),
      Padding = new Padding(2, 0, 2, 0),
      SizingGrip = false
    };
    statusStrip.Items.Add(fileStatusLabel);
    collectionStatusLabel.ForeColor = SystemColors.GrayText;
    collectionStatusLabel.IsLink = true;
    collectionStatusLabel.LinkColor = SystemColors.GrayText;
    collectionStatusLabel.ActiveLinkColor = SystemColors.ControlText;
    collectionStatusLabel.LinkBehavior = LinkBehavior.HoverUnderline;
    collectionStatusLabel.Click += (_, _) => BrowseAllCards();
    statusStrip.Items.Add(collectionStatusLabel);
    return statusStrip;
  }

  private void BrowseAllCards()
  {
    cardSearch.Clear();
    cardSearch.Show(searchToggle);
  }

  protected override bool ProcessCmdKey(ref Message message, Keys keyData)
  {
    if (keyData == Keys.F1)
    {
      ShowHelp();
      return true;
    }

    if (keyData == Keys.Escape && cardTabs.CancelTabDrag())
    {
      return true;
    }

    if (keyData == Keys.Escape && cardSearch.Dismiss()) return true;

    if (keyData == (Keys.Control | Keys.F))
    {
      cardSearch.Show(searchToggle, selectAll: true);
      return true;
    }

    var opensMenu = keyData == Keys.Menu
      || keyData == (Keys.Alt | Keys.F)
      || keyData == (Keys.Alt | Keys.E)
      || keyData == (Keys.Alt | Keys.H);
    if (opensMenu && !menuStrip.Visible)
    {
      menuStrip.Visible = true;
      menuStrip.Focus();
      menuStrip.Items[0].Select();
      if (keyData == Keys.Menu)
      {
        return true;
      }
    }

    return base.ProcessCmdKey(ref message, keyData);
  }

  private void MainFormOnKeyDown(object? sender, KeyEventArgs eventArgs)
  {
    if (eventArgs.KeyCode == Keys.F2)
    {
      BeginRenameCurrentCard();
    }
    else if (eventArgs.Control && eventArgs.KeyCode == Keys.S)
    {
      TrySaveCurrentCard();
    }
    else if (cardPane.ContainsFocus && !eventArgs.Alt)
    {
      return;
    }
    else if (eventArgs.KeyCode == Keys.Left)
    {
      MoveCard(-1);
    }
    else if (eventArgs.KeyCode == Keys.Right)
    {
      MoveCard(1);
    }
    else
    {
      return;
    }

    eventArgs.Handled = true;
    eventArgs.SuppressKeyPress = true;
  }

  private void CardNameEditorOnKeyDown(object? sender, KeyEventArgs eventArgs)
  {
    if (eventArgs.KeyCode == Keys.Enter)
    {
      CommitCurrentCardRename();
    }
    else if (eventArgs.KeyCode == Keys.Escape)
    {
      EndCurrentCardRename();
      UpdateStatusStrip();
    }
    else
    {
      return;
    }

    eventArgs.Handled = true;
    eventArgs.SuppressKeyPress = true;
  }

  private void BeginRenameCurrentCard()
  {
    if (cardNameEditor.Visible
        || currentIndex < 0
        || currentIndex >= cards.Count
        || !TrySaveCurrentCard())
    {
      return;
    }

    var fileName = GetCardFileName(cards[currentIndex]);
    cardNameEditor.Text = fileName;
    SizeCardNameEditor();
    cardHeader.Visible = false;
    cardNameEditor.Visible = true;
    cardNameEditor.BringToFront();
    cardNameEditor.Focus();
    cardNameEditor.Select(0, Path.GetFileNameWithoutExtension(fileName).Length);
  }

  private void SizeCardNameEditor()
  {
    if (cardNameEditor.Parent is not Control parent)
    {
      return;
    }

    var measuredWidth = TextRenderer.MeasureText(cardNameEditor.Text, cardNameEditor.Font).Width + 12;
    var availableWidth = Math.Max(80, parent.ClientSize.Width - 8);
    cardNameEditor.Width = Math.Min(Math.Max(80, measuredWidth), availableWidth);
    cardNameEditor.Height = cardNameEditor.PreferredHeight;
    cardNameEditor.Location = new Point(4, Math.Max(0, (parent.ClientSize.Height - cardNameEditor.Height) / 2));
  }

  private void CommitCurrentCardRename()
  {
    if (!cardNameEditor.Visible || currentIndex < 0 || currentIndex >= cards.Count)
    {
      return;
    }

    try
    {
      var oldPath = cards[currentIndex].FilePath;
      var renamedCard = cardRepository.Rename(cardRoot, cards[currentIndex], cardNameEditor.Text);
      RenameOpenCardTab(oldPath, renamedCard.FilePath);
      ReloadCards(renamedCard.FilePath);
      EndCurrentCardRename();
      ShowCurrentCard();
    }
    catch (Exception exception) when (exception is ArgumentException
      or IOException
      or UnauthorizedAccessException)
    {
      fileStatusLabel.Text = exception.Message;
      cardNameEditor.Focus();
      cardNameEditor.SelectAll();
    }
  }

  private void EndCurrentCardRename()
  {
    cardNameEditor.Visible = false;
    cardHeader.Visible = true;
  }

  private void UpdateStatusStrip()
  {
    collectionStatusLabel.Text = cards.Count == 0
      ? "No prompts in this folder"
      : $"{cards.Count} {(cards.Count == 1 ? "prompt" : "prompts")} - browse in Search";
    collectionStatusLabel.Enabled = cards.Count > 0;
    if (currentIndex < 0 || currentIndex >= cards.Count)
    {
      fileStatusLabel.Text = "No prompt file selected";
      return;
    }

    var modifiedMarker = IsCurrentCardDirty ? " *" : string.Empty;
    var folderPath = Path.TrimEndingDirectorySeparator(cardRoot);
    fileStatusLabel.Text = $"Folder: {folderPath}{modifiedMarker}";
  }

  private void MoveCard(int offset)
  {
    if (cards.Count == 0 || !TrySaveCurrentCard())
    {
      return;
    }

    currentIndex = (currentIndex + offset + cards.Count) % cards.Count;
    ShowCurrentCard(openTab: false);
  }

  private void AddCard()
  {
    if (!TrySaveCurrentCard())
    {
      return;
    }

    try
    {
      var card = cardRepository.CreateNew(cardRoot);
      ReloadCards(card.FilePath);
      ShowCurrentCard();
      cardPane.Focus();
      cardPane.SelectionStart = cardPane.TextLength;
    }
    catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
    {
      ShowError("A new prompt card could not be added.", exception);
    }
  }

  private void RemoveCurrentCard()
  {
    var card = CurrentCard;
    if (card is null)
    {
      return;
    }

    var confirmation = MessageBox.Show(
      this,
      $"Delete '{card.Title}'?\n\nThis permanently deletes {Path.GetFileName(card.FilePath)} from disk.",
      "Delete prompt card file",
      MessageBoxButtons.YesNo,
      MessageBoxIcon.Warning,
      MessageBoxDefaultButton.Button2);
    if (confirmation != DialogResult.Yes)
    {
      return;
    }

    try
    {
      cardRepository.Delete(card);
      RemoveOpenCardTab(card.FilePath);
      cards = cardRepository.Load(cardRoot);
      currentIndex = cards.Count == 0 ? -1 : Math.Min(currentIndex, cards.Count - 1);
      ShowCurrentCard();
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
      ShowError("The prompt card could not be removed.", exception);
    }
  }

  private void UpdateCopyButton() => copyButton.Enabled =
    currentIndex >= 0 && currentIndex < cards.Count && cardPane.TextLength > 0;

  private void CopyCurrentPrompt()
  {
    if (!copyButton.Enabled) return;
    successFeedback.Hide();
    var start = cardPane.SelectionStart;
    var length = cardPane.SelectionLength;
    try
    {
      cardPane.SelectAll();
      cardPane.Copy();
      fileStatusLabel.Text = "Copied to clipboard";
      copyButton.Image = null;
      copyButton.Text = "\u2713";
      chromeToolTip.SetToolTip(copyButton, "Copied to clipboard");
      successFeedback.ConfirmCopy();
    }
    catch (ExternalException)
    {
      MessageBox.Show(this, "The clipboard is unavailable. Try copying again.", "Prompt Flipper",
        MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
    finally { cardPane.Select(start, length); }
  }

  private void ShowCurrentCard(bool openTab = true)
  {
    EndCurrentCardRename();
    var hasCards = currentIndex >= 0 && currentIndex < cards.Count;
    UpdateCopyButton();
    previousButton.Enabled = hasCards;
    nextButton.Enabled = hasCards;

    if (!hasCards)
    {
      cardHeader.Text = Path.TrimEndingDirectorySeparator(cardRoot);
      cardHeader.AccessibleName = "No current prompt card";
      chromeToolTip.SetToolTip(cardHeader, cardHeader.Text);
      cardPane.Text = "No prompt cards were found in this folder.";
      cardPane.ReadOnly = true;
      pageLabel.Text = BuildCollectionProgressCue(-1, 0);
      UpdateStatusStrip();
      RebuildCardTabs();
      return;
    }

    var card = cards[currentIndex];
    var locationCue = BuildCardLocationCue(cardRoot, card.FilePath);
    cardHeader.Text = locationCue;
    cardHeader.AccessibleName = $"Current prompt card: {locationCue}";
    chromeToolTip.SetToolTip(cardHeader, locationCue);
    cardPane.ReadOnly = false;
    cardPane.Text = CardRepository.GetEditableContent(card.Content);
    cardPane.SelectionStart = 0;
    cardPane.SelectionLength = 0;
    pageLabel.Text = BuildCollectionProgressCue(currentIndex, cards.Count);
    UpdateStatusStrip();
    if (openTab)
    {
      OpenCurrentCardTab();
    }
    else
    {
      RebuildCardTabs();
    }
  }

  private void OpenFolder()
  {
    using var dialog = new FolderBrowserDialog
    {
      Description = "Choose the folder that contains your prompt cards.",
      SelectedPath = cardRoot,
      ShowNewFolderButton = true,
      UseDescriptionForTitle = true
    };

    if (dialog.ShowDialog(this) == DialogResult.OK)
    {
      LoadCards(dialog.SelectedPath);
    }
  }

  private void AddFileFromTemplate()
  {
    using var dialog = new OpenFileDialog
    {
      Title = "Add file from template",
      InitialDirectory = cardRoot,
      Filter = "Markdown prompt cards (*.md)|*.md|All files (*.*)|*.*"
    };

    if (dialog.ShowDialog(this) != DialogResult.OK || AddCardFromTemplate(dialog.FileName) is not { } card)
    {
      return;
    }

    MessageBox.Show(
      this,
      $"Created {Path.GetFileName(card.FilePath)} from the template {Path.GetFileName(dialog.FileName)}.\n\n" +
      "The template file is unchanged. Edits apply to the new card only.",
      "New card from template",
      MessageBoxButtons.OK,
      MessageBoxIcon.Information);
  }

  // Copies the template into the attached folder as a new card and opens it in a tab.
  // The attached folder never changes.
  private Card? AddCardFromTemplate(string templatePath)
  {
    if (!TrySaveCurrentCard())
    {
      return null;
    }

    try
    {
      var card = cardRepository.CreateFromTemplate(cardRoot, Path.GetFullPath(templatePath));
      ReloadCards(card.FilePath);
      cardSearch.SetCards(cards);
      ShowCurrentCard();
      fileStatusLabel.Text = $"New card {Path.GetFileName(card.FilePath)} created from template";
      return card;
    }
    catch (Exception exception) when (exception is InvalidDataException
      or IOException
      or UnauthorizedAccessException
      or ArgumentException
      or NotSupportedException)
    {
      ShowError("A card could not be created from the template.", exception);
      return null;
    }
  }

  private void LoadCards(string root)
  {
    if (!TrySaveCurrentCard())
    {
      return;
    }

    try
    {
      var fullRoot = Path.GetFullPath(root);
      var loadedCards = cardRepository.Load(fullRoot);
      cardRoot = fullRoot;
      cards = loadedCards;
      cardSearch.SetCards(cards);
      cardTabs.Clear();
      currentIndex = loadedCards.Count > 0 ? 0 : -1;
      settingsStore.SaveCardRoot(cardRoot);
      WatchCardRoot();
      ShowCurrentCard();
    }
    catch (Exception exception) when (exception is IOException
      or UnauthorizedAccessException
      or InvalidDataException
      or ArgumentException
      or NotSupportedException)
    {
      MessageBox.Show(
        this,
        $"The prompt cards could not be opened.\n\n{exception.Message}",
        "Prompt Flipper",
        MessageBoxButtons.OK,
        MessageBoxIcon.Error);
    }
  }

  private void PrintCurrentCard()
  {
    if (currentIndex < 0)
    {
      return;
    }

    var card = cards[currentIndex];
    using var document = new PrintDocument
    {
      DocumentName = card.Title
    };
    document.PrintPage += (_, eventArgs) =>
    {
      var graphics = eventArgs.Graphics;
      if (graphics is null)
      {
        return;
      }

      using var titleFont = new Font(Font, FontStyle.Bold);
      graphics.DrawString(card.Title, titleFont, Brushes.Black, eventArgs.MarginBounds.Left, eventArgs.MarginBounds.Top);
      var bodyBounds = new RectangleF(
        eventArgs.MarginBounds.Left,
        eventArgs.MarginBounds.Top + titleFont.GetHeight(graphics) + 12,
        eventArgs.MarginBounds.Width,
        eventArgs.MarginBounds.Height - titleFont.GetHeight(graphics) - 12);
      graphics.DrawString(card.Body, cardPane.Font, Brushes.Black, bodyBounds);
    };

    using var dialog = new PrintDialog
    {
      Document = document,
      UseEXDialog = true
    };
    if (dialog.ShowDialog(this) == DialogResult.OK)
    {
      document.Print();
    }
  }

  private void ShowProperties()
  {
    if (currentIndex < 0)
    {
      return;
    }

    var card = cards[currentIndex];
    var folderPath = Path.TrimEndingDirectorySeparator(cardRoot);
    MessageBox.Show(
      this,
      $"Title: {card.Title}\nCard: {currentIndex + 1} of {cards.Count}\nFolder: {folderPath}",
      "Prompt properties",
      MessageBoxButtons.OK,
      MessageBoxIcon.Information);
  }

  private void ShowHelp()
  {
    if (helpWindow is null || helpWindow.IsDisposed)
    {
      helpWindow = new HelpWindow();
      helpWindow.FormClosed += (_, _) => helpWindow = null;
      helpWindow.Show(this);
      return;
    }

    if (helpWindow.WindowState == FormWindowState.Minimized)
    {
      helpWindow.WindowState = FormWindowState.Normal;
    }

    helpWindow.BringToFront();
    helpWindow.Activate();
  }

  private void ShowSanitizer()
  {
    var hasCurrentCard = currentIndex >= 0 && currentIndex < cards.Count;
    using var dialog = new SanitizerDialog(
      hasCurrentCard ? cardPane.PromptText : null,
      hasCurrentCard ? GetCardFileName(cards[currentIndex]) : null, loadSanitizerRules, saveSanitizerRules);
    dialog.ShowDialog(this);
  }

  private void QuickSanitizeCurrentPrompt()
  {
    successFeedback.Hide();
    var input = CurrentCard is not null ? cardPane.PromptText : null;
    if (!PromptFlipper.Sanitizer.TryQuickSanitize(input, loadSanitizerRules,
      Clipboard.SetText, out var message, out var sanitized, out var spans))
    {
      fileStatusLabel.Text = message;
      return;
    }

    // No saved secret occurs in the card: nothing is protected, so there is nothing to
    // de-sanitize and no success to show. The unchanged text is still on the clipboard.
    if (spans.Count == 0)
    {
      fileStatusLabel.Text = "No saved secret found in this card. Copied unchanged.";
      return;
    }

    if (!cardPane.ApplySanitized(sanitized!, spans))
    {
      fileStatusLabel.Text = "Sanitized and copied. The editor could not show the sanitized view.";
      return;
    }

    fileStatusLabel.Text = message;
    successFeedback.Show("sanitized_message");
  }

  private void DesanitizeCurrentPrompt()
  {
    successFeedback.Hide();
    if (!cardPane.Desanitize())
    {
      fileStatusLabel.Text = "Nothing to restore. The prompt is not sanitized.";
      return;
    }

    fileStatusLabel.Text = "Original values restored";
    successFeedback.Show("de_sanitized_message");
  }

  private void ResetCopyFeedback()
  {
    copyButton.Text = string.Empty;
    copyButton.Image = copyArtwork;
    chromeToolTip.SetToolTip(copyButton, "Copy");
  }

  private Image? LoadQuickIcon(string artwork, int size) =>
    ToolbarArtwork.LoadQuickActionIcon(artwork, size, DeviceDpi);

  protected override void OnFormClosing(FormClosingEventArgs eventArgs)
  {
    if (!TrySaveCurrentCard())
    {
      eventArgs.Cancel = true;
      return;
    }

    settingsStore.SaveSession(cardRoot, cardTabs.Paths, CurrentCard?.FilePath);
    base.OnFormClosing(eventArgs);
  }

  protected override void OnFormClosed(FormClosedEventArgs eventArgs)
  {
    cardSearch.Dispose();
    successFeedback.Dispose();
    copyArtwork?.Dispose();
    cardWatcher.Dispose();
    chromeToolTip.Dispose();
    sanitizeArtwork?.Dispose();
    deSanitizeArtwork?.Dispose();
    base.OnFormClosed(eventArgs);
  }

  private bool TrySaveCurrentCard()
  {
    var currentCard = CurrentCard;
    if (currentCard is null || !IsCurrentCardDirty)
    {
      return true;
    }

    var promptText = cardPane.PromptText;

    try
    {
      // The card always receives the restored originals; the sanitized view is shown again afterwards.
      var view = cardPane.CaptureView();
      cardRepository.Save(
        cardRoot,
        currentCard.FilePath,
        CardRepository.BuildStoredContent(currentCard.Content, promptText));
      ReloadCards(currentCard.FilePath);
      ShowCurrentCard();
      if (CurrentCard is { } savedCard &&
          string.Equals(savedCard.FilePath, currentCard.FilePath, StringComparison.OrdinalIgnoreCase))
      {
        cardPane.RestoreView(view);
      }

      return true;
    }
    catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
    {
      ShowError("The prompt card could not be saved.", exception);
      cardPane.Focus();
      return false;
    }
  }

  private Card? CurrentCard => currentIndex >= 0 && currentIndex < cards.Count ? cards[currentIndex] : null;

  private bool IsCurrentCardDirty => CurrentCard is { } card
    && !string.Equals(cardPane.PromptText, CardRepository.GetEditableContent(card.Content), StringComparison.Ordinal);

  private void ShowError(string message, Exception exception) => MessageBox.Show(
    this,
    $"{message}\n\n{exception.Message}",
    "Prompt Flipper",
    MessageBoxButtons.OK,
    MessageBoxIcon.Error);

  private void ReloadCards(string selectedFile)
  {
    cards = cardRepository.Load(cardRoot);
    currentIndex = cards
      .Select((card, index) => new { card.FilePath, Index = index })
      .First(item => string.Equals(
        item.FilePath,
        selectedFile,
        StringComparison.OrdinalIgnoreCase))
      .Index;
  }

  private void WatchCardRoot()
  {
    pendingSelectedPath = null;
    cardWatcher.Watch(cardRoot);
  }

  private void RefreshCardsFromDisk()
  {
    if (cardNameEditor.Visible)
    {
      cardWatcher.RequestRefresh();
      return;
    }

    var previousIndex = currentIndex;
    var previousCard = CurrentCard;
    var selectedPath = pendingSelectedPath ?? previousCard?.FilePath;
    pendingSelectedPath = null;
    var editorText = cardPane.PromptText;
    var editorView = cardPane.CaptureView();
    var selectionStart = cardPane.SelectionStart;
    var selectionLength = cardPane.SelectionLength;
    var hasUnsavedEdit = IsCurrentCardDirty;

    try
    {
      var loadedCards = cardRepository.Load(cardRoot);
      var selectedIndex = selectedPath is null ? -1 : FindCardIndex(loadedCards, selectedPath);

      if (selectedIndex < 0 && hasUnsavedEdit && previousCard is not null)
      {
        var preservedCards = loadedCards.ToList();
        selectedIndex = Math.Min(Math.Max(previousIndex, 0), preservedCards.Count);
        preservedCards.Insert(selectedIndex, previousCard);
        loadedCards = preservedCards;
      }

      cards = loadedCards;
      cardSearch.SetCards(cards);
      currentIndex = selectedIndex >= 0
        ? selectedIndex
        : cards.Count == 0 ? -1 : Math.Min(Math.Max(previousIndex, 0), cards.Count - 1);
      SynchronizeOpenCardTabs();
      ShowCurrentCard();
      var sameCard = previousCard is not null && currentIndex >= 0 && string.Equals(
        cards[currentIndex].FilePath, previousCard.FilePath, StringComparison.OrdinalIgnoreCase);

      if (hasUnsavedEdit && currentIndex >= 0)
      {
        cardPane.Text = editorText;
      }

      if (sameCard)
      {
        cardPane.RestoreView(editorView);
      }

      if (hasUnsavedEdit && currentIndex >= 0)
      {
        cardPane.SelectionStart = Math.Min(selectionStart, cardPane.TextLength);
        cardPane.SelectionLength = Math.Min(
          selectionLength,
          cardPane.TextLength - cardPane.SelectionStart);
        UpdateStatusStrip();
      }
    }
    catch (Exception exception) when (exception is IOException
      or UnauthorizedAccessException
      or ArgumentException
      or NotSupportedException)
    {
      fileStatusLabel.Text = $"The prompt cards could not be refreshed. {exception.Message}";
    }
  }

  private static string GetCardFileName(Card card)
  {
    return Path.GetFileName(card.FilePath);
  }

  private static string BuildCardLocationCue(string rootPath, string cardPath)
  {
    var relativePath = Path.GetRelativePath(rootPath, cardPath);
    var pathSegments = relativePath.Split(
      [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
      StringSplitOptions.RemoveEmptyEntries);
    return string.Join(" > ", pathSegments);
  }

  private static string BuildCollectionProgressCue(int index, int count)
  {
    return count == 0 ? "Card 0 / 0" : $"Card {index + 1} / {count}";
  }

  private void ShowAbout()
  {
    using var dialog = new AboutDialog();
    dialog.ShowDialog(this);
  }
}
