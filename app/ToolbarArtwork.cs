using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace PromptNavigator;

internal static class ToolbarArtwork
{
  // Quick actions beside the card name, in logical (96 DPI) units. The 32 px artwork
  // leaves a 4 px frame inside the button at every display scale.
  internal const int QuickIconSize = 32;
  internal static readonly Size QuickButtonSize = new(40, 40);
  internal const int QuickButtonMargin = 2;

  // Main toolbar artwork in logical units; the Wrap toggle image is slightly smaller
  // so its pastel-green active background stays visible around it.
  internal const int ToolbarIconSize = 20;
  internal const int ToolbarToggleIconSize = 18;

  // Subtle pastel green behind an image toggle while its option is active.
  internal static readonly Color ActiveToggleBackColor = Color.FromArgb(216, 240, 214);

  // Shared chrome style - docs/ui-style-guide.md is the source of record for these.
  // #0078D4 is Windows' own default system accent color / Fluent 2 brand token, not an
  // invented value - see the style guide's "Why SystemColors stays" section.
  internal static readonly Color Accent = Color.FromArgb(0x00, 0x78, 0xD4);
  internal static readonly Font UiFont = new("Segoe UI", 9f);

  // Fluent 2's spacing ramp: XXS=2, XS=4, S=8, M=12, L=16, XL=20, XXL=24.
  internal enum SpacingStep { XXS = 2, XS = 4, S = 8, M = 12, L = 16, XL = 20, XXL = 24 }

  internal static int Spacing(SpacingStep step) => (int)step;

  internal static int Scale(int logical, int dpi) => Math.Max(1, (int)Math.Round(logical * dpi / 96.0));

  internal static Size Scale(Size logical, int dpi) => new(Scale(logical.Width, dpi), Scale(logical.Height, dpi));

  private static readonly int[] CommandIconSizes = [16, 20, 24, 32, 40, 48, 64];
  private static readonly int[] SupportIconSizes = [16, 20, 24, 32, 48, 64];
  private static readonly int[] QuickIconSizes = [16, 24, 32, 48, 64, 128, 256];

  // The catalog returns a fresh, caller-owned image. No stream or shared bitmap
  // survives a load, so disposing one window cannot invalidate another's artwork.
  // Omit logicalSize to preserve a resource's native canvas and dimensions.
  internal static Bitmap? LoadImage(string resourceName, int? logicalSize = null, int dpi = 96)
  {
    using var stream = typeof(ToolbarArtwork).Assembly.GetManifestResourceStream(resourceName);
    if (stream is null) return null;
    using var source = Image.FromStream(stream);
    return logicalSize is int size ? Render(source, Scale(size, dpi)) : new Bitmap(source);
  }

  internal static Image? LoadCommandIcon(string command, int logicalSize, int dpi)
  {
    var master = command switch
    {
      "Open" => "folder", "Save" => "save", "Search" => "search",
      "TextSize" => "text-size", "Sanitize" => "sanitize",
      "About" => "promptflipper_logo", "Previous" => "previous", "Next" => "next",
      _ => null
    };
    if (master is not null) return LoadRenderedImage($"Assets.{master}.png", logicalSize, dpi);
    var asset = command switch
    {
      "Print" => "print", "Help" => "help", "Copy" => "copy",
      "Delete" => "delete", "Settings" => "settings", _ => null
    };
    if (asset is null) return LoadDpiIcon(command, logicalSize, dpi, CommandIconSizes);
    var wanted = Scale(logicalSize, dpi);
    var size = PickIconSize(wanted, SupportIconSizes);
    // Native variants retain their canvas; intermediate sizes use visible bounds.
    return size == wanted ? LoadImage($"Assets.{asset}_{size}.png")
      : LoadRenderedImage($"Assets.{asset}_{size}.png", logicalSize, dpi);
  }

  internal static Image? LoadSupportIcon(int logicalSize, int dpi) =>
    LoadDpiIcon("Support", logicalSize, dpi, SupportIconSizes);

  private static Image? LoadDpiIcon(string command, int logicalSize, int dpi, IReadOnlyList<int> sizes) =>
    LoadImage($"Toolbar.{PickIconSize(Scale(logicalSize, dpi), sizes)}.{command}.png");

  private static int PickIconSize(int wanted, IReadOnlyList<int> sizes) =>
    sizes.FirstOrDefault(candidate => candidate >= wanted, sizes[^1]);

  internal static Image? LoadQuickIcon(string artwork, int logicalSize, int dpi = 96) =>
    LoadRenderedImage($"QuickIcons.{artwork}_{PickIconSize(Scale(logicalSize, dpi), QuickIconSizes)}.png", logicalSize, dpi);

  // Header actions retain the native canvas and pixels at supported display sizes.
  internal static Image? LoadQuickActionIcon(string artwork, int logicalSize, int dpi = 96)
  {
    var pixels = Scale(logicalSize, dpi);
    var source = LoadImage($"QuickIcons.{artwork}_{PickIconSize(pixels, QuickIconSizes)}.png");
    if (source is null || source.Size == new Size(pixels, pixels)) return source;
    using (source) return FitCanvas(source, pixels);
  }

  internal static Bitmap FitCanvas(Image source, int pixelSize)
  {
    var result = new Bitmap(pixelSize, pixelSize, PixelFormat.Format32bppArgb);
    var scale = Math.Min((float)pixelSize / source.Width, (float)pixelSize / source.Height);
    var width = source.Width * scale;
    var height = source.Height * scale;
    using var graphics = Graphics.FromImage(result);
    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
    graphics.DrawImage(source,
      new RectangleF((pixelSize - width) / 2, (pixelSize - height) / 2, width, height),
      new RectangleF(0, 0, source.Width, source.Height), GraphicsUnit.Pixel);
    return result;
  }

  // Commands historically render a detached bitmap; retain its alpha conversion.
  private static Bitmap? LoadRenderedImage(string resourceName, int logicalSize, int dpi)
  {
    using var source = LoadImage(resourceName);
    return source is null ? null : Render(source, Scale(logicalSize, dpi));
  }

  internal static Icon? LoadIcon(string resourceName)
  {
    if (resourceName.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
    {
      using var stream = typeof(ToolbarArtwork).Assembly.GetManifestResourceStream(resourceName);
      if (stream is null) return null;
      using var source = new Icon(stream);
      return (Icon)source.Clone();
    }
    using var bitmap = LoadImage(resourceName);
    if (bitmap is null) return null;
    var handle = bitmap.GetHicon();
    try
    {
      using var borrowed = Icon.FromHandle(handle);
      return (Icon)borrowed.Clone();
    }
    finally { DestroyIcon(handle); }
  }

  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool DestroyIcon(IntPtr handle);

  // An image-only check button. It keeps the CheckBox keyboard behavior (Tab, Space)
  // and reports the CheckButton role and checked state to accessibility clients.
  internal static CheckBox CreateImageToggle(string asset, string accessibleName, bool initiallyChecked,
    int logicalSize = 20)
  {
    var toggle = new CheckBox
    {
      Appearance = Appearance.Button,
      AutoSize = false,
      FlatStyle = FlatStyle.Flat,
      Text = string.Empty,
      AccessibleName = accessibleName,
      Checked = initiallyChecked,
      Margin = new Padding(1, 0, 1, 0)
    };
    toggle.FlatAppearance.BorderSize = 0;
    toggle.FlatAppearance.CheckedBackColor = ActiveToggleBackColor;
    Apply(toggle, asset, logicalSize);
    toggle.TextImageRelation = TextImageRelation.Overlay;
    toggle.ImageAlign = ContentAlignment.MiddleCenter;
    void Resize() => toggle.Size = Scale(new Size(logicalSize + 8, logicalSize + 6), toggle.DeviceDpi);
    toggle.DpiChangedAfterParent += (_, _) => Resize();
    Resize();
    return toggle;
  }

  // Buttons keep their text and native keyboard behavior. Each owns its rendered
  // bitmap, which is replaced when Windows moves the control to another DPI.
  internal static void Apply(ButtonBase button, string asset, int logicalSize = 20)
  {
    button.TextImageRelation = TextImageRelation.ImageBeforeText;
    button.ImageAlign = ContentAlignment.MiddleLeft;
    button.TextAlign = ContentAlignment.MiddleCenter;
    void Refresh()
    {
      var replacement = LoadImage($"Assets.{asset}.png", logicalSize, button.DeviceDpi)
        ?? throw new InvalidOperationException($"Missing artwork: {asset}");
      var previous = button.Image;
      button.Image = replacement;
      previous?.Dispose();
    }
    button.HandleCreated += (_, _) => Refresh();
    button.DpiChangedAfterParent += (_, _) => Refresh();
    button.Disposed += (_, _) => { button.Image?.Dispose(); button.Image = null; };
    Refresh();
  }

  // Render the visible artwork rather than its transparent canvas. The inset leaves
  // the same breathing room at 100% and 200%, without distorting the source shape.
  internal static Bitmap Render(Image source, int pixelSize)
  {
    ArgumentOutOfRangeException.ThrowIfLessThan(pixelSize, 1);
    using var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
    using (var graphics = Graphics.FromImage(bitmap))
    {
      graphics.DrawImageUnscaled(source, 0, 0);
    }

    var canvas = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
    var data = bitmap.LockBits(canvas, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    var left = bitmap.Width;
    var top = bitmap.Height;
    var right = -1;
    var bottom = -1;
    try
    {
      var row = new byte[bitmap.Width * 4];
      for (var y = 0; y < bitmap.Height; y++)
      {
        Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
        for (var x = 0; x < bitmap.Width; x++)
        {
          if (row[x * 4 + 3] <= 8) continue;
          left = Math.Min(left, x);
          top = Math.Min(top, y);
          right = Math.Max(right, x);
          bottom = Math.Max(bottom, y);
        }
      }
    }
    finally
    {
      bitmap.UnlockBits(data);
    }

    var result = new Bitmap(pixelSize, pixelSize, PixelFormat.Format32bppArgb);
    if (right < left) return result;
    var bounds = Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    var contentSize = pixelSize * 5f / 6f;
    var scale = contentSize / Math.Max(bounds.Width, bounds.Height);
    var width = bounds.Width * scale;
    var height = bounds.Height * scale;
    using var output = Graphics.FromImage(result);
    output.InterpolationMode = InterpolationMode.HighQualityBicubic;
    output.PixelOffsetMode = PixelOffsetMode.HighQuality;
    output.CompositingQuality = CompositingQuality.HighQuality;
    using var attributes = new ImageAttributes();
    attributes.SetWrapMode(WrapMode.TileFlipXY);
    output.DrawImage(bitmap,
      Rectangle.Round(new RectangleF((pixelSize - width) / 2, (pixelSize - height) / 2, width, height)),
      bounds.X, bounds.Y, bounds.Width, bounds.Height, GraphicsUnit.Pixel, attributes);
    return result;
  }
}
