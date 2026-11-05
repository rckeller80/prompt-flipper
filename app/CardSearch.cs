namespace PromptNavigator;

internal sealed class CardChosenEventArgs(string filePath) : EventArgs
{
  internal string FilePath { get; } = filePath;
}

internal sealed class CardSearch : IDisposable
{
  private readonly CardRepository repository = new();
  internal ToolStripComboBox Input { get; } = new();
  internal ToolStripDropDown Menu { get; } = new();
  private IReadOnlyList<Card> cards = [];
  private IReadOnlyList<Card> matches = [];
  private string query = string.Empty;
  private bool updating;

  internal event EventHandler<CardChosenEventArgs>? CardChosen;
  internal event EventHandler? Dismissed;

  internal CardSearch(int dpi)
  {
    Input.AutoSize = false;
    Input.DropDownStyle = ComboBoxStyle.DropDown;
    SearchField.Apply(SearchProfile.Load("toolbar"), Input, dpi);
    Input.DropDownWidth = Input.Width;
    Input.MaxDropDownItems = 10;
    Input.TextUpdate += (_, _) =>
    {
      if (updating) return;
      query = Input.Text;
      RefreshResults(openDropDown: true);
    };
    Input.SelectedIndexChanged += (_, _) =>
    {
      if (updating || Input.SelectedIndex < 0 || Input.SelectedIndex >= matches.Count) return;
      var path = matches[Input.SelectedIndex].FilePath;
      Input.SelectedIndex = -1;
      Input.Text = query;
      Choose(path);
    };
    Input.KeyDown += (_, args) => HandleKey(args);
    Menu.Items.Add(Input);
    Menu.Closed += (_, _) => Input.DroppedDown = false;
  }

  internal void SetCards(IReadOnlyList<Card> value)
  {
    cards = value;
    RefreshResults();
  }

  private void RefreshResults(bool openDropDown = false)
  {
    matches = repository.Search(cards, query);
    var caret = Input.SelectionStart;
    updating = true;
    Input.ComboBox.BeginUpdate();
    try
    {
      Input.Items.Clear();
      foreach (var card in matches)
      {
        var fileName = Path.GetFileName(card.FilePath);
        Input.Items.Add(string.IsNullOrEmpty(card.RelativeDirectory)
          ? fileName : $"{card.RelativeDirectory}\\{fileName}");
      }
      Input.Text = query;
      Input.SelectionStart = Math.Min(caret, Input.Text.Length);
      Input.SelectionLength = 0;
      Input.DroppedDown = openDropDown && Menu.Visible && query.Length > 0 && matches.Count > 0;
    }
    finally
    {
      Input.ComboBox.EndUpdate();
      updating = false;
    }
  }

  internal void Show(ToolStripItem? anchor, bool selectAll = false)
  {
    if (anchor?.Owner is not { Visible: true } toolbar) return;
    if (!Menu.Visible) Menu.Show(toolbar, new Point(anchor.Bounds.Left, anchor.Bounds.Bottom));
    Input.ComboBox.Focus();
    Input.DroppedDown = matches.Count > 0;
    if (selectAll) Input.SelectAll();
  }

  internal void Clear()
  {
    query = string.Empty;
    RefreshResults();
  }

  internal bool Dismiss()
  {
    if (!Menu.Visible && query.Length == 0) return false;
    Clear();
    Close();
    return true;
  }

  internal void HandleKey(KeyEventArgs args)
  {
    if (args.KeyCode == Keys.Enter && matches.Count > 0) Choose(matches[0].FilePath);
    else if (args.KeyCode == Keys.Escape)
    {
      Clear();
      Close();
    }
    else return;
    args.Handled = true;
    args.SuppressKeyPress = true;
  }

  private void Choose(string path)
  {
    CardChosen?.Invoke(this, new CardChosenEventArgs(path));
    Close();
  }

  private void Close()
  {
    Menu.Close();
    Dismissed?.Invoke(this, EventArgs.Empty);
  }

  public void Dispose() => Menu.Dispose();
}
