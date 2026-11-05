using System.Text.Json;

namespace PromptNavigator;

// The parameters of one search site, read from its embedded Search/<id>.json file.
// The control itself comes from SearchField, so every site shares one template.
internal sealed record SearchProfile(string AccessibleName, string Placeholder, string ToolTip, int Width)
{
  private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

  internal static SearchProfile Load(string id)
  {
    var resource = "Search." + id + ".json";
    using var stream = typeof(SearchProfile).Assembly.GetManifestResourceStream(resource)
      ?? throw new InvalidOperationException("Missing search parameter file " + resource);
    return JsonSerializer.Deserialize<SearchProfile>(stream, Options)
      ?? throw new InvalidOperationException("Empty search parameter file " + resource);
  }
}

internal static class SearchField
{
  // A single-line framed search box for a dialog, styled like the Sanitizer field.
  internal static TextBox Create(SearchProfile profile, out Panel frame)
  {
    var box = new TextBox
    {
      AccessibleName = profile.AccessibleName,
      PlaceholderText = profile.Placeholder,
      BorderStyle = BorderStyle.None,
      Dock = DockStyle.Fill
    };
    frame = SanitizerDialog.FrameField(box, singleLine: true);
    return box;
  }

  // The toolbar variant: the same parameters applied to a toolbar combo box.
  internal static void Apply(SearchProfile profile, ToolStripComboBox input, int dpi)
  {
    input.AccessibleName = profile.AccessibleName;
    input.ToolTipText = profile.ToolTip;
    input.Width = ToolbarArtwork.Scale(profile.Width, dpi);
  }
}
