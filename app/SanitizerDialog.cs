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
  private readonly CheckBox hideAll = new();
  private readonly CheckBox sortByValue = new();
  private readonly TextBox valueEditor = new();
  private readonly Label status = new();
  private readonly Button removeButton = new();
  private readonly Button piiReviewButton = new();
  private readonly ListBox piiFindings = new();
  private readonly Button addFindingButton = new();
  private readonly Button addAllFindingsButton = new();
  private readonly TableLayoutPanel findingsPanel = new();
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
    // The item text is "value (name)" for search, accessibility and GetItemText; the drawn
    // row shows the same text with the placeholder name in a secondary colour.
    ruleNames.Format += (_, e) =>
    {
      if (e.ListItem is string name)
        e.Value = RuleNamed(name)?.ListText(hideAll.Checked) ?? name;
    };
    ruleNames.DrawMode = DrawMode.OwnerDrawFixed;
    ruleNames.DrawItem += (_, e) => DrawRule(e);
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

    piiReviewButton.AutoSize = true;
    piiReviewButton.Enabled = sourceText is not null;
    piiReviewButton.Text = "Re&view";
    piiReviewButton.Click += (_, _) => ShowReview();

    // Personal details Review found that Sanitize would still leave in the output.
    piiFindings.AccessibleName = "Personal details not sanitized";
    piiFindings.IntegralHeight = false;
    piiFindings.SelectionMode = SelectionMode.MultiExtended;
    piiFindings.HorizontalScrollbar = true;
    piiFindings.BorderStyle = BorderStyle.None;
    piiFindings.Height = piiFindings.ItemHeight * 3 + 4;
    piiFindings.FormattingEnabled = true;
    piiFindings.Format += FormatFinding;
    piiFindings.DoubleClick += (_, _) => AddFindings(selectedOnly: true);
    addFindingButton.AutoSize = true;
    addFindingButton.Text = "Add as secre&t";
    addFindingButton.Click += (_, _) => AddFindings(selectedOnly: true);
    addAllFindingsButton.AutoSize = true;
    addAllFindingsButton.Text = "A&dd all";
    addAllFindingsButton.Click += (_, _) => AddFindings(selectedOnly: false);
    var findingButtons = new FlowLayoutPanel
    {
      AutoSize = true,
      FlowDirection = FlowDirection.TopDown,
      Margin = Padding.Empty,
      WrapContents = false
    };
    findingButtons.Controls.Add(addFindingButton);
    findingButtons.Controls.Add(addAllFindingsButton);
    findingsPanel.ColumnCount = 2;
    findingsPanel.RowCount = 1;
    findingsPanel.AutoSize = true;
    findingsPanel.Dock = DockStyle.Fill;
    findingsPanel.Margin = Padding.Empty;
    findingsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
    findingsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
    var findingsFrame = FrameField(piiFindings, singleLine: true);
    findingsFrame.Height = piiFindings.Height + findingsFrame.Padding.Vertical + 2;
    findingsPanel.Controls.Add(findingsFrame, 0, 0);
    findingsPanel.Controls.Add(findingButtons, 1, 0);

    var listPanel = new TableLayoutPanel
    {
      ColumnCount = 1,
      RowCount = 5,
      Dock = DockStyle.Fill,
      Margin = new Padding(0, 0, ToolbarArtwork.Spacing(ToolbarArtwork.SpacingStep.M), 0)
    };
    listPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    listPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    listPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
    listPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    listPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    listPanel.Controls.Add(new Label { AutoSize = true, Text = "P&laceholders" }, 0, 0);
    ruleSearch = SearchField.Create(SearchProfile.Load("sanitizer"), out var searchFrame);
    ruleNames.BorderStyle = BorderStyle.None;
    var listFrame = FrameField(ruleNames);
    // Hide all is on-screen protection only: it masks every value in the list and the editor
    // and never changes what is stored, sanitized or restored. It is not persisted.
    hideAll.Text = "&Hide all";
    hideAll.AccessibleName = "Hide all secret values";
    hideAll.AutoSize = true;
    hideAll.CheckedChanged += (_, _) => ApplyHideAll();
    sortByValue.Text = "S&ort A-Z";
    sortByValue.AccessibleName = "Sort by secret value A-Z";
    sortByValue.Appearance = Appearance.Button;
    sortByValue.AutoSize = true;
    ruleSearch.TextChanged += (_, _) => RefreshRuleList(ruleNames.SelectedItem as string, reloadEditor: false);
    sortByValue.CheckedChanged += (_, _) => RefreshRuleList(ruleNames.SelectedItem as string, reloadEditor: false);
    var listOptionsRow = new FlowLayoutPanel
    {
      AutoSize = true,
      Dock = DockStyle.Fill,
      FlowDirection = FlowDirection.LeftToRight,
      Margin = Padding.Empty,
      WrapContents = false
    };
    listOptionsRow.Controls.Add(hideAll);
    listOptionsRow.Controls.Add(sortByValue);
    listPanel.Controls.Add(searchFrame, 0, 1);
    listPanel.Controls.Add(listFrame, 0, 2);
    listPanel.Controls.Add(listOptionsRow, 0, 4);

    valueEditor.AccessibleName = "Secret";
    valueEditor.Dock = DockStyle.Fill;
    valueEditor.BorderStyle = BorderStyle.None;

    var valueEditorFrame = FrameField(valueEditor, singleLine: true);

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
    for (var row = 0; row < 2; row++)
    {
      detailPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    }
    detailPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
    detailPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
    detailPanel.Controls.Add(new Label { AutoSize = true, Text = "&Add / Edit" }, 0, 0);
    detailPanel.Controls.Add(valueEditorFrame, 0, 1);
    reviewText.Multiline = true;
    reviewText.ReadOnly = true;
    reviewText.ScrollBars = ScrollBars.Both;
    reviewText.WordWrap = false;
    reviewText.AccessibleName = "Sanitized review";
    reviewText.PlaceholderText = "Select Review to see the sanitized current card.";
    reviewText.BorderStyle = BorderStyle.None;
    reviewText.BackColor = SystemColors.Window;
    reviewText.MinimumSize = new Size(0, Font.Height * 4);
    detailPanel.Controls.Add(FrameField(reviewText), 0, 2);
    detailPanel.Controls.Add(findingsPanel, 0, 3);

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
             { importRulesButton, removeButton, saveRuleButton, piiReviewButton, addFindingButton, addAllFindingsButton })
    {
      tooltips.SetToolTip(button, button.Text.Replace("&", string.Empty).TrimEnd('.'));
      button.MinimumSize = button.PreferredSize;
    }
    tooltips.SetToolTip(sortByValue, sortByValue.AccessibleName);

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
    // The minimum size makes room for the findings row, which shows only after a Review finds something.
    // The table's own preferred height drops its percent row below the panels' needs, so the
    // rows are summed from what each panel needs.
    findingsPanel.Visible = true;
    var tableHeight = layout.GetPreferredSize(new Size(minimumClientWidth, 0)).Height;
    var contentRow = Math.Max(listPanel.MinimumSize.Height + listPanel.Margin.Vertical,
      detailPanel.GetPreferredSize(new Size(minimumClientWidth - 270, 0)).Height + detailPanel.Margin.Vertical);
    var statusRow = Math.Max(status.GetPreferredSize(new Size(270, 0)).Height,
      reviewStatus.GetPreferredSize(new Size(minimumClientWidth - 270, 0)).Height) + status.Margin.Vertical;
    var buttonRow = bottomButtons.GetPreferredSize(Size.Empty).Height + bottomButtons.Margin.Vertical;
    var minimumHeight = Math.Max(tableHeight, contentRow + statusRow + buttonRow + layout.Padding.Vertical);
    findingsPanel.Visible = false;
    MinimumSize = SizeFromClientSize(new Size(minimumClientWidth, minimumHeight));
    if (ClientSize.Height < minimumHeight) ClientSize = new Size(ClientSize.Width, minimumHeight);
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

  private const TextFormatFlags ListTextFlags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine |
    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding;

  private SanitizerRule? RuleNamed(string? name) =>
    name is null ? null : rules.FirstOrDefault(rule => rule.Name == name);

  private void DrawRule(DrawItemEventArgs e)
  {
    e.DrawBackground();
    if (e.Index >= 0 && e.Index < ruleNames.Items.Count && ruleNames.Items[e.Index] is string name)
    {
      var rule = RuleNamed(name);
      var shown = rule?.ShownValue(hideAll.Checked) ?? name;
      var selected = (e.State & DrawItemState.Selected) != 0;
      var font = e.Font ?? ruleNames.Font;
      var valueWidth = TextRenderer.MeasureText(e.Graphics, shown, font, Size.Empty, ListTextFlags).Width;
      TextRenderer.DrawText(e.Graphics, shown, font, e.Bounds, e.ForeColor, ListTextFlags);
      if (rule is not null)
      {
        var nameBounds = new Rectangle(e.Bounds.X + valueWidth, e.Bounds.Y,
          Math.Max(0, e.Bounds.Width - valueWidth), e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, " (" + rule.Name + ")", font, nameBounds,
          selected ? e.ForeColor : SystemColors.GrayText, ListTextFlags);
      }
    }
    e.DrawFocusRectangle();
  }

  // The list scrolls sideways to the widest row so a long value is never cut off.
  private void UpdateListExtent()
  {
    var widest = 0;
    foreach (var item in ruleNames.Items)
    {
      widest = Math.Max(widest,
        TextRenderer.MeasureText(ruleNames.GetItemText(item), ruleNames.Font, Size.Empty, ListTextFlags).Width);
    }
    ruleNames.HorizontalExtent = widest + 4;
  }

  private void ApplyHideAll()
  {
    valueEditor.UseSystemPasswordChar = hideAll.Checked;
    UpdateListExtent();
    ruleNames.Invalidate();
    // Re-adding the findings re-runs their formatting with the new masking.
    var findings = piiFindings.Items.Cast<object>().ToArray();
    var selected = piiFindings.SelectedIndices.Cast<int>().ToArray();
    piiFindings.BeginUpdate();
    piiFindings.Items.Clear();
    piiFindings.Items.AddRange(findings);
    foreach (var index in selected) piiFindings.SetSelected(index, true);
    piiFindings.EndUpdate();
  }

  private void UpdateSelectedRule()
  {
    var rule = RuleNamed(SingleSelectedName());
    valueEditor.UseSystemPasswordChar = hideAll.Checked;
    valueEditor.Text = rule?.Value ?? string.Empty;
    UpdateSelectionControls();
  }

  private void UpdateSelectionControls()
  {
    valueEditor.Enabled = ruleNames.SelectedItems.Count <= 1;
    removeButton.Enabled = ruleNames.SelectedItems.Count > 0;
  }

  private string? SingleSelectedName() =>
    ruleNames.SelectedItems.Count == 1 ? ruleNames.SelectedItem as string : null;

  private void StartNewRule()
  {
    ruleNames.ClearSelected();
    valueEditor.Enabled = true;
    valueEditor.Clear();
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

    candidate.Add(new SanitizerRule(name, value));
    if (!Sanitizer.TrySanitize(string.Empty, candidate, out _))
    {
      ShowRuleChange("Placeholder labels must produce different non-empty normalized names. Nothing was saved.");
      return;
    }

    if (!TryPersist(candidate))
    {
      return;
    }

    // The list drops the saved row when the current search excludes it.
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

    ImportFrom(() => File.ReadLines(dialog.FileName));
  }

  internal void ImportFrom(Func<IEnumerable<string>> readLines)
  {
    var previousCount = rules.Count;
    if (!Sanitizer.TryImportRulesAndWrite(
        readLines,
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
    piiFindings.Items.Clear();
    findingsPanel.Visible = false;
  }

  // Review always checks the sanitized result for personal details that would still be shared,
  // and offers to add each one as a secret.
  private void ShowReview()
  {
    InvalidateReview();
    if (sourceText is null || rules.Count == 0 || !Sanitizer.TrySanitize(sourceText, rules, out var sanitized) ||
        sanitized is null)
    {
      reviewStatus.Text = sourceText is null ? "Open a card to review." :
        rules.Count == 0 ? "Add a secret before reviewing." : "Review unavailable. Check the configured secrets.";
      return;
    }
    reviewText.Text = sanitized;
    var findings = PiiDetector.Detect(sanitized)
      .DistinctBy(finding => finding.Value, StringComparer.Ordinal)
      .ToList();
    if (findings.Count == 0)
    {
      reviewStatus.Text = "No personal details found.";
      return;
    }
    foreach (var finding in findings) piiFindings.Items.Add(finding);
    piiFindings.SelectedIndex = 0;
    findingsPanel.Visible = true;
    reviewStatus.Text = findings.Count == 1
      ? "1 personal detail is not sanitized yet. Select Add as secret to replace it."
      : $"{findings.Count} personal details are not sanitized yet. Select Add as secret or Add all to replace them.";
  }

  private void FormatFinding(object? sender, ListControlConvertEventArgs e)
  {
    if (e.ListItem is PiiFinding finding)
      e.Value = $"{finding.Category}: {(hideAll.Checked ? SanitizerRule.HiddenValue : finding.Value)}";
  }

  private void AddFindings(bool selectedOnly)
  {
    var values = (selectedOnly ? piiFindings.SelectedItems.Cast<PiiFinding>() : piiFindings.Items.Cast<PiiFinding>())
      .Select(finding => finding.Value)
      .ToList();
    if (values.Count == 0) return;

    var candidate = rules.ToList();
    foreach (var value in values.Where(value => !candidate.Any(rule => rule.Value == value)))
    {
      candidate.Add(new SanitizerRule(Sanitizer.NextRuleName(candidate), value));
    }
    if (!Sanitizer.TrySanitize(string.Empty, candidate, out _))
    {
      ShowRuleChange("These details could not be added as secrets. Nothing was saved.");
      return;
    }
    if (!TryPersist(candidate)) return;

    var added = candidate.Count - rules.Count;
    ReplaceRules(candidate);
    RefreshRuleList();
    ShowRuleChange(added == 1 ? "1 personal detail was added as a secret." : $"{added} personal details were added as secrets.");
    ShowReview();
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
    status.Text = message;
  }

  private void ReplaceRules(IEnumerable<SanitizerRule> replacement)
  {
    InvalidateReview();
    rules.Clear();
    rules.AddRange(replacement);
  }

  // A search or sort change keeps the editor draft unless the edited row itself leaves the
  // list. A rule change always reloads the editor from saved state.
  private void RefreshRuleList(string? selectedName = null, bool reloadEditor = true)
  {
    var editedBefore = SingleSelectedName();
    refreshingList = true;
    try
    {
      ruleNames.BeginUpdate();
      ruleNames.Items.Clear();
      var shown = Sanitizer.FilterRules(rules, ruleSearch.Text);
      foreach (var rule in sortByValue.Checked
        ? Sanitizer.SortByValue(shown)
        : shown.OrderBy(rule => rule.Name, StringComparer.Ordinal))
      {
        ruleNames.Items.Add(rule.Name);
      }
      UpdateListExtent();
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

    return $"{rules.Count} secret{(rules.Count == 1 ? string.Empty : "s")} stored.";
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
