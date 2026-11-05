using System.Diagnostics;
using System.Reflection;

namespace PromptNavigator;

internal static class SupportLink
{
  internal const string Url = "https://ko-fi.com/rok1907";

  internal static void Open(IWin32Window owner, string caption)
  {
    try
    {
      Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true });
    }
    catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
    {
      MessageBox.Show(
        owner,
        $"The donation page could not be opened.\n\n{Url}",
        caption,
        MessageBoxButtons.OK,
        MessageBoxIcon.Warning);
    }
  }
}

/// <summary>
/// Renders the repository About content contract for this app. The field list and the source of
/// each value are in docs/about-panel.md. [uses: about-panel]
/// </summary>
internal sealed class AboutDialog : Form
{
  private const string Tagline = "Flipping prompts like a deck of cards.";
  private const string Author = "Rok";
  private const string License = "MIT";
  private const string SourceRepository = "Not yet published";
  internal const string HelpRoute = "Help menu, View Help (F1)";

  public AboutDialog()
  {
    var product = ReadProduct();

    Text = $"About {product}";
    StartPosition = FormStartPosition.CenterParent;
    FormBorderStyle = FormBorderStyle.FixedDialog;
    MaximizeBox = false;
    MinimizeBox = false;
    ShowInTaskbar = false;
    ClientSize = new Size(420, 380);
    Font = ToolbarArtwork.UiFont;

    var closeButton = new Button
    {
      Text = "Close",
      DialogResult = DialogResult.OK,
      AutoSize = true
    };
    AcceptButton = closeButton;
    CancelButton = closeButton;

    var heading = new Label
    {
      Text = product,
      AutoSize = true,
      Font = new Font(Font.FontFamily, Font.Size + 2f, FontStyle.Bold),
      Margin = new Padding(0, 0, 0, ToolbarArtwork.Spacing(ToolbarArtwork.SpacingStep.XXS))
    };
    var tagline = new Label
    {
      Text = Tagline,
      AutoSize = true,
      Margin = new Padding(0, 0, 0, ToolbarArtwork.Spacing(ToolbarArtwork.SpacingStep.S))
    };

    var donationLink = new LinkLabel
    {
      Text = SupportLink.Url,
      AutoSize = true,
      Anchor = AnchorStyles.Left
    };
    donationLink.LinkClicked += (_, _) => SupportLink.Open(this, Text);

    var fields = new TableLayoutPanel
    {
      ColumnCount = 2,
      Dock = DockStyle.Top,
      AutoSize = true,
      Padding = new Padding(ToolbarArtwork.Spacing(ToolbarArtwork.SpacingStep.M))
    };
    fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
    fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

    fields.Controls.Add(heading, 0, 0);
    fields.SetColumnSpan(heading, 2);
    fields.Controls.Add(tagline, 0, 1);
    fields.SetColumnSpan(tagline, 2);

    AddField(fields, 2, "Version:", ReadVersion());
    AddField(fields, 3, "Author:", Author);
    AddField(fields, 4, "Licence:", License);
    AddField(fields, 5, "Source:", SourceRepository);
    AddField(fields, 6, "Help:", HelpRoute);

    fields.Controls.Add(NewLabel("Support:"), 0, 7);
    fields.Controls.Add(donationLink, 1, 7);

    var buttons = new FlowLayoutPanel
    {
      Dock = DockStyle.Bottom,
      Height = 46,
      FlowDirection = FlowDirection.RightToLeft,
      Padding = new Padding(ToolbarArtwork.Spacing(ToolbarArtwork.SpacingStep.S))
    };
    buttons.Controls.Add(closeButton);

    var artwork = new PictureBox
    {
      Image = ToolbarArtwork.LoadImage("Assets.promptflipper_logo.png", 64)
        ?? throw new InvalidOperationException("Missing About artwork"),
      SizeMode = PictureBoxSizeMode.CenterImage,
      Dock = DockStyle.Top,
      Height = 64,
      AccessibleName = "Prompt Flipper logo"
    };
    artwork.Disposed += (_, _) => artwork.Image?.Dispose();
    Controls.Add(fields);
    Controls.Add(artwork);
    Controls.Add(buttons);
  }

  private static void AddField(TableLayoutPanel fields, int row, string caption, string value)
  {
    fields.Controls.Add(NewLabel(caption), 0, row);
    fields.Controls.Add(NewLabel(value), 1, row);
  }

  private static Label NewLabel(string text) => new()
  {
    Text = text,
    AutoSize = true,
    Anchor = AnchorStyles.Left,
    Margin = new Padding(
      ToolbarArtwork.Spacing(ToolbarArtwork.SpacingStep.XXS),
      ToolbarArtwork.Spacing(ToolbarArtwork.SpacingStep.XXS),
      ToolbarArtwork.Spacing(ToolbarArtwork.SpacingStep.M),
      ToolbarArtwork.Spacing(ToolbarArtwork.SpacingStep.XXS))
  };

  /// <summary>
  /// Reads the product name from the assembly, so the dialog never holds a second copy of it.
  /// </summary>
  internal static string ReadProduct()
  {
    var assembly = Assembly.GetExecutingAssembly();
    var product = assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product;
    return string.IsNullOrWhiteSpace(product) ? assembly.GetName().Name ?? "Prompt Flipper" : product;
  }

  /// <summary>
  /// Reads the version from the running assembly. The contract forbids a literal version string,
  /// because a literal drifts from the build on the first release that forgets it.
  /// </summary>
  // The About content as plain lines, shown under About in Help.
  internal static string HelpText() => $"""
    {ReadProduct()} {ReadVersion()}
    {Tagline}

    Author: {Author}
    Licence: {License}
    Source: {SourceRepository}
    Support: {SupportLink.Url}
    """;

  private static string ReadVersion()
  {
    var assembly = Assembly.GetExecutingAssembly();
    var informational = assembly
      .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
      .InformationalVersion;

    if (!string.IsNullOrWhiteSpace(informational))
    {
      var buildMetadata = informational.IndexOf('+');
      return buildMetadata < 0 ? informational : informational[..buildMetadata];
    }

    return assembly.GetName().Version?.ToString() ?? "unknown";
  }
}
