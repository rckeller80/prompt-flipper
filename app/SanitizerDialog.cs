using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using PromptFlipper;

namespace PromptNavigator;

internal sealed class SanitizerDialog : Form
{
  private readonly string? sourceText;
  private readonly string? cardName;
  private readonly List<SanitizerRule> rules;
  private readonly ListBox ruleNames = new();
  private readonly TextBox ruleSearch;
  private readonly CheckBox privateOnly = new();
  private readonly TextBox valueEditor = new();
  private readonly CheckBox privateSecret = new();
  private readonly Label status = new();
  private readonly Button removeButton = new();
  private readonly CheckBox piiDetection = new();
  private readonly Button piiReviewButton = new();
  private readonly TextBox reviewText = new();
  private readonly Label reviewStatus = new();
  private readonly ToolTip tooltips = new();
  private readonly Action<IEnumerable<SanitizerRule>> persistRules;
  private bool refreshingList;

  public SanitizerDialog(string? currentCardText, string? currentCardName)
    : this(currentCardText, currentCardName, new ProtectedRuleStore().LoadRules, new ProtectedRuleStore().Save)
  {
  }

  internal SanitizerDialog(string? currentCardText, string? currentCardName,
    Func<IEnumerable<SanitizerRule>> loadRules, Action<IEnumerable<SanitizerRule>> persistRules)
  {
    this.persistRules = persistRules;
    sourceText = currentCardText;
    cardName = currentCardName;
    var loaded = Sanitizer.TryLoadRules(loadRules, out var loadedRules);
    rules = loadedRules.ToList();

    Text = "Settings";
    Icon = LoadSanitizerIcon();
    Disposed += (_, _) =>
    {
      Icon?.Dispose();
      tooltips.Dispose();
    };
    StartPosition = FormStartPosition.CenterParent;
    FormBorderStyle = FormBorderStyle.Sizable;
    ShowInTaskbar = false;
    ClientSize = new Size(760, 340);
    MinimumSize = new Size(720, 300);
    Font = ToolbarArtwork.UiFont;

    // Configure step: rule list on the left, the selected rule's detail on the right.
    // Tab order follows the order controls are added, so each label precedes its field.
    ruleNames.AccessibleName = "Placeholders";
    ruleNames.Dock = DockStyle.Fill;
    ruleNames.IntegralHeight = false;
    ruleNames.FormattingEnabled = true;
    ruleNames.SelectionMode = SelectionMode.MultiExtended;
    ruleNames.HorizontalScrollbar = true;
    ruleNames.Format += (_, e) =>
    {
      if (e.ListItem is string name)
        e.Value = rules.FirstOrDefault(rule => rule.Name == name)?.ListText ?? name;
    };
    ruleNames.SelectedIndexChanged += (_, _) =>
    {
      if (!refreshingList) UpdateSelectedRule();
    };

    var importRulesButton = new Button
    {
      AutoSize = true,
      Anchor = AnchorStyles.Left,
      Text = "&Import..."
    };
    importRulesButton.Click += (_, _) => ImportRules();
    ToolbarArtwork.Apply(importRulesButton, "folder");

    removeButton.AutoSize = true;
    removeButton.Anchor = AnchorStyles.None;
    removeButton.Enabled = false;
    removeButton.Text = "Re&move";
    removeButton.Click += (_, _) => RemoveSelectedRule();
    ToolbarArtwork.Apply(removeButton, "delete_64");

    piiDetection.AutoSize = true;
    piiDetection.Text = "PII &detection";
    piiDetection.AccessibleName = "Enable PII detection";
    piiDetection.CheckedChanged += (_, _) => InvalidateReview();

    piiReviewButton.AutoSize = true;
    piiReviewButton.Enabled = sourceText is not null;
    piiReviewButton.Text = "Re&view";
    piiReviewButton.Click += (_, _) => ShowReview();

    var piiRow = new FlowLayoutPanel
    {
      AutoSize = true,
      Dock = DockStyle.Fill,
      FlowDirection = FlowDirection.LeftToRight,
      Margin = Padding.Empty,
      WrapContents = false
    };
    piiRow.Controls.Add(piiDetection);

    var listPanel = new TableLayoutPanel
    {
      ColumnCount = 1,
      RowCount = 6,
      Dock = DockStyle.Fill,
      Margin = new Padding(0, 0, ToolbarArtwork.Spacing(ToolbarArtwork.SpacingStep.M), 0)
    };
    listPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    listPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    listPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
    listPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    listPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    listPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    listPanel.Controls.Add(new Label { AutoSize = true, Text = "P&laceholders" }, 0, 0);
    ruleSearch = SearchField.Create(SearchProfile.Load("sanitizer"), out var searchFrame);
    ruleNames.BorderStyle = BorderStyle.None;
    var listFrame = FrameField(ruleNames);
    // The list filter is independent of the secret's own Private checkbox and applies at once.
    privateOnly.Text = "&Hide private";
    privateOnly.AccessibleName = "Hide private entries";
    privateOnly.AutoSize = true;
    ruleSearch.TextChanged += (_, _) => RefreshRuleList(ruleNames.SelectedItem as string, reloadEditor: false);
    privateOnly.CheckedChanged += (_, _) => RefreshRuleList(ruleNames.SelectedItem as string, reloadEditor: false);
    listPanel.Controls.Add(searchFrame, 0, 1);
    listPanel.Controls.Add(listFrame, 0, 2);
    listPanel.Controls.Add(privateOnly, 0, 4);
    listPanel.Controls.Add(piiRow, 0, 5);

    valueEditor.AccessibleName = "Secret";
    valueEditor.Dock = DockStyle.Fill;
    valueEditor.BorderStyle = BorderStyle.None;
    valueEditor.UseSystemPasswordChar = true;

    var valueEditorFrame = FrameField(valueEditor, singleLine: true);

    privateSecret.AutoSize = true;
    privateSecret.Text = "&Private";
    privateSecret.AccessibleName = "Keep this secret private";
    privateSecret.Checked = true;
    privateSecret.CheckedChanged += (_, _) => valueEditor.UseSystemPasswordChar = privateSecret.Checked;

    var saveRuleButton = new Button
    {
      AutoSize = true,
      Anchor = AnchorStyles.Right,
      Text = "&Save"
    };
    saveRuleButton.Click += (_, _) => SaveEditedRule();
    ToolbarArtwork.Apply(saveRuleButton, "save");

    var detailPanel = new TableLayoutPanel
    {
      ColumnCount = 1,
      RowCount = 4,
      Dock = DockStyle.Fill,
      Margin = Padding.Empty
    };
    for (var row = 0; row < 3; row++)
    {
      detailPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    }
    detailPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
    detailPanel.Controls.Add(new Label { AutoSize = true, Text = "&Add / Edit" }, 0, 0);
    detailPanel.Controls.Add(valueEditorFrame, 0, 1);
    detailPanel.Controls.Add(privateSecret, 0, 2);
    reviewText.Multiline = true;
    reviewText.ReadOnly = true;
    reviewText.ScrollBars = ScrollBars.Both;
    reviewText.WordWrap = false;
    reviewText.AccessibleName = "Sanitized review";
    reviewText.PlaceholderText = "Select Review to see the sanitized current card.";
    reviewText.BorderStyle = BorderStyle.None;
    reviewText.BackColor = SystemColors.Window;
    reviewText.MinimumSize = new Size(0, Font.Height * 4);
    detailPanel.Controls.Add(FrameField(reviewText), 0, 3);

    // Rule actions stay together immediately below their placeholder list.
    var ruleActionsRow = new TableLayoutPanel
    {
      ColumnCount = 3,
      RowCount = 1,
      AutoSize = true,
      AutoSizeMode = AutoSizeMode.GrowAndShrink,
      Dock = DockStyle.Top,
      Margin = new Padding(0, ToolbarArtwork.Spacing(ToolbarArtwork.SpacingStep.S), 0, 0)
    };
    ruleActionsRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
    ruleActionsRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
    ruleActionsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
    ruleActionsRow.Controls.Add(importRulesButton, 0, 0);
    ruleActionsRow.Controls.Add(removeButton, 1, 0);
    ruleActionsRow.Controls.Add(saveRuleButton, 2, 0);
    listPanel.Controls.Add(ruleActionsRow, 0, 3);

    var bottomButtons = new FlowLayoutPanel
    {
      AutoSize = true,
      Dock = DockStyle.Fill,
      FlowDirection = FlowDirection.RightToLeft,
      WrapContents = false
    };
    bottomButtons.Controls.Add(piiReviewButton);

    var layout = new TableLayoutPanel
    {
      ColumnCount = 2,
      RowCount = 3,
      Dock = DockStyle.Fill,
      Padding = new Padding(ToolbarArtwork.Spacing(ToolbarArtwork.SpacingStep.M))
    };
    layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 270f));
    layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
    layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
    layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

    WrapStatus(status, listPanel);
    layout.Controls.Add(listPanel, 0, 0);
    layout.Controls.Add(detailPanel, 1, 0);
    layout.Controls.Add(status, 0, 1);
    WrapStatus(reviewStatus, detailPanel);
    layout.Controls.Add(reviewStatus, 1, 1);
    layout.Controls.Add(bottomButtons, 1, 2);

    Controls.Add(layout);

    // Each button's tooltip is its own text with the mnemonic marker and trailing ellipsis
    // dropped, so none of them needs to be maintained by hand.
    foreach (var button in new[]
             { importRulesButton, removeButton, saveRuleButton, piiReviewButton })
    {
      tooltips.SetToolTip(button, button.Text.Replace("&", string.Empty).TrimEnd('.'));
      button.MinimumSize = button.PreferredSize;
    }

    // Sized once the editor has inherited its Font from the form, so the frame hugs a
    // single line instead of stretching to fill its row.
    valueEditorFrame.Height = valueEditor.PreferredHeight + valueEditorFrame.Padding.Vertical + 2;

    RefreshRuleList();
    StartNewRule();
    status.Text = loaded ? DisabledOrReadyStatus() : ProtectedRuleStore.LoadError;
    // Keep the auto-sized action rows from consuming the list's space when resized.
    var minimumClientWidth = ClientSize.Width - (Width - MinimumSize.Width);
    ruleNames.MinimumSize = new Size(0, ruleNames.ItemHeight * 4 + 4);
    listFrame.MinimumSize = new Size(0, ruleNames.MinimumSize.Height + listFrame.Padding.Vertical);
    listPanel.MinimumSize = listPanel.GetPreferredSize(new Size(270, 0));
    var minimumContent = layout.GetPreferredSize(new Size(minimumClientWidth, 0));
    MinimumSize = SizeFromClientSize(new Size(minimumClientWidth, minimumContent.Height));
  }

  protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
  {
    if (keyData == Keys.Escape)
    {
      DialogResult = DialogResult.Cancel;
      Close();
      return true;
    }
    if (keyData == Keys.Enter && valueEditor.Focused)
    {
      SaveEditedRule();
      return true;
    }

    return base.ProcessCmdKey(ref msg, keyData);
  }

  private static Icon? LoadSanitizerIcon() => ToolbarArtwork.LoadIcon("PromptFlipper.ico");

  private void UpdateSelectedRule()
  {
    var selected = SingleSelectedName();
    var rule = rules.FirstOrDefault(rule => rule.Name == selected);
    valueEditor.UseSystemPasswordChar = true;
    valueEditor.Text = rule?.Value ?? string.Empty;
    privateSecret.Checked = rule?.Private ?? true;
    valueEditor.UseSystemPasswordChar = privateSecret.Checked;
    UpdateSelectionControls();
  }

  private void UpdateSelectionControls()
  {
    valueEditor.Enabled = privateSecret.Enabled = ruleNames.SelectedItems.Count <= 1;
    removeButton.Enabled = ruleNames.SelectedItems.Count > 0;
  }

  private string? SingleSelectedName() =>
    ruleNames.SelectedItems.Count == 1 ? ruleNames.SelectedItem as string : null;

  private void StartNewRule()
  {
    ruleNames.ClearSelected();
    valueEditor.Enabled = privateSecret.Enabled = true;
    valueEditor.Clear();
    privateSecret.Checked = true;
    valueEditor.Focus();
  }

  private void SaveEditedRule()
  {
    if (ruleNames.SelectedItems.Count > 1)
    {
      ShowRuleChange("Select one placeholder to edit, or choose New.");
      return;
    }

    var name = ruleNames.SelectedItem as string ?? Sanitizer.NextRuleName(rules);
    var value = valueEditor.Text;
    if (value.Length == 0)
    {
      ShowRuleChange("Enter the exact secret to replace.");
      return;
    }

    var candidate = rules
      .Where(rule => !string.Equals(rule.Name, name, StringComparison.Ordinal))
      .ToList();
    if (candidate.Any(rule => string.Equals(rule.Value, value, StringComparison.Ordinal)))
    {
      ShowRuleChange("Each placeholder must use a different secret. Nothing was saved.");
      return;
    }

    candidate.Add(new SanitizerRule(name, value, privateSecret.Checked));
    if (!Sanitizer.TrySanitize(string.Empty, candidate, out _))
    {
      ShowRuleChange("Placeholder labels must produce different non-empty normalized names. Nothing was saved.");
      return;
    }

    if (!TryPersist(candidate))
    {
      return;
    }

    // The value and its Private flag were persisted together; the list now shows the saved
    // privacy and drops the row when the current search or Hide private excludes it.
    ReplaceRules(candidate);
    RefreshRuleList(name);
    var savedRule = candidate[^1];
    ShowRuleChange(ruleNames.Items.Contains(name)
      ? $"Saved '{name}' as {savedRule.Placeholder}."
      : $"Saved '{name}' as {savedRule.Placeholder}. The current filter hides it.");
  }

  private void ImportRules()
  {
    using var dialog = new OpenFileDialog
    {
      CheckFileExists = true,
      Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
      Multiselect = false,
      Title = "Import sanitizer secrets"
    };
    if (dialog.ShowDialog(this) != DialogResult.OK)
    {
      return;
    }

    var previousCount = rules.Count;
    if (!Sanitizer.TryImportRulesAndWrite(
        () => File.ReadLines(dialog.FileName),
        rules,
        persistRules,
        out var importedRules))
    {
      ShowRuleChange(
        "The secrets could not be imported. Check the file and existing placeholders. Nothing changed.");
      return;
    }

    ReplaceRules(importedRules);
    RefreshRuleList();
    StartNewRule();
    ShowRuleChange(
      $"{rules.Count - previousCount} secrets were imported with deterministic protected labels.");
  }

  private void InvalidateReview()
  {
    reviewText.Clear();
    reviewStatus.Text = string.Empty;
  }

  private void ShowReview()
  {
    InvalidateReview();
    if (sourceText is null || rules.Count == 0 || !Sanitizer.TrySanitize(sourceText, rules, out var sanitized))
    {
      reviewStatus.Text = sourceText is null ? "Open a card to review." :
        rules.Count == 0 ? "Add a secret before reviewing." : "Review unavailable. Check the configured secrets.";
      return;
    }
    reviewText.Text = sanitized;
    if (!piiDetection.Checked) return;
    var findings = PiiDetector.Detect(sourceText);
    var summary = findings
      .GroupBy(finding => finding.Category, StringComparer.Ordinal)
      .OrderBy(group => group.Key, StringComparer.Ordinal)
      .Select(group => $"{group.Key}: {group.Count()}");
    reviewStatus.Text = findings.Count == 0 ? "No PII detected in the current card." :
      "PII detected: " + string.Join("; ", summary);
  }

  private void RemoveSelectedRule()
  {
    var selectedNames = ruleNames.SelectedItems.Cast<string>().ToHashSet(StringComparer.Ordinal);
    if (selectedNames.Count == 0)
    {
      return;
    }

    var candidate = rules
      .Where(rule => !selectedNames.Contains(rule.Name))
      .ToList();
    if (!TryPersist(candidate))
    {
      return;
    }

    ReplaceRules(candidate);
    RefreshRuleList();
    StartNewRule();
    ShowRuleChange($"{selectedNames.Count} placeholder(s) were removed.");
  }

  private bool TryPersist(List<SanitizerRule> candidate)
  {
    try
    {
      persistRules(candidate);
      return true;
    }
    catch (Exception exception) when (exception is IOException
        or UnauthorizedAccessException
        or CryptographicException
        or Win32Exception)
    {
      ShowRuleChange($"The placeholders could not be saved. Nothing changed. {exception.Message}");
      return false;
    }
  }

  private void ShowRuleChange(string message)
  {
    status.Text = privateOnly.Checked ? $"{message} {DisabledOrReadyStatus()}" : message;
  }

  private void ReplaceRules(IEnumerable<SanitizerRule> replacement)
  {
    InvalidateReview();
    rules.Clear();
    rules.AddRange(replacement);
  }

  // A filter change keeps the editor (a draft and its Private checkbox) unless the edited
  // row itself leaves the list. A rule change always reloads the editor from saved state.
  private void RefreshRuleList(string? selectedName = null, bool reloadEditor = true)
  {
    var editedBefore = SingleSelectedName();
    refreshingList = true;
    try
    {
      ruleNames.BeginUpdate();
      ruleNames.Items.Clear();
      foreach (var rule in Sanitizer.FilterRules(rules, ruleSearch.Text, privateOnly.Checked)
        .OrderBy(rule => rule.Name, StringComparer.Ordinal))
      {
        ruleNames.Items.Add(rule.Name);
      }
      ruleNames.EndUpdate();

      if (selectedName is not null)
      {
        ruleNames.SelectedItem = selectedName;
      }
    }
    finally
    {
      refreshingList = false;
    }

    if (reloadEditor || !string.Equals(editedBefore, SingleSelectedName(), StringComparison.Ordinal))
    {
      UpdateSelectedRule();
    }
    else
    {
      UpdateSelectionControls();
    }
    status.Text = DisabledOrReadyStatus();
  }

  private string DisabledOrReadyStatus()
  {
    if (rules.Count == 0)
    {
      return "No placeholders yet. Secrets are encrypted for your Windows account.";
    }

    var stored = $"{rules.Count} secret{(rules.Count == 1 ? string.Empty : "s")} stored.";
    if (!privateOnly.Checked) return stored;
    var hidden = rules.Count(rule => rule.Private);
    return $"{stored} {hidden} private entr{(hidden == 1 ? "y" : "ies")} hidden.";
  }

  internal static Panel FrameField(Control field, bool singleLine = false)
  {
    var frame = new Panel
    {
      Dock = singleLine ? DockStyle.Top : DockStyle.Fill,
      BackColor = SystemColors.Window,
      Padding = new Padding(ToolbarArtwork.Spacing(ToolbarArtwork.SpacingStep.XXS)),
      Margin = new Padding(3)
    };
    field.Dock = DockStyle.Fill;
    frame.Controls.Add(field);
    if (singleLine) frame.Height = field.PreferredSize.Height + frame.Padding.Vertical + 2;
    frame.Paint += (_, args) =>
    {
      using var pen = new Pen(field.ContainsFocus ? ToolbarArtwork.Accent : SystemColors.ControlDark,
        field.ContainsFocus ? 2 : 1);
      args.Graphics.DrawRectangle(pen, 0, 0, frame.Width - 1, frame.Height - 1);
    };
    field.Enter += (_, _) => frame.Invalidate();
    field.Leave += (_, _) => frame.Invalidate();
    return frame;
  }

  // A status line wraps to the dialog width so a long message is never clipped to blank.
  private static void WrapStatus(Label label, Control host)
  {
    label.AutoSize = true;
    label.Dock = DockStyle.Fill;
    label.Padding = new Padding(0, ToolbarArtwork.Spacing(ToolbarArtwork.SpacingStep.S), 0,
      ToolbarArtwork.Spacing(ToolbarArtwork.SpacingStep.XXS));
    void Fit() => label.MaximumSize = new Size(
      Math.Max(1, host.ClientSize.Width - host.Padding.Horizontal - label.Margin.Horizontal), 0);
    host.Resize += (_, _) => Fit();
    Fit();
  }
}
