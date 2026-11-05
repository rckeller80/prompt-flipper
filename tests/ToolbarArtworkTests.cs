using PromptFlipper.Tests;
using static PromptFlipper.Tests.MainFormTestHarness;
using System.Drawing;
using Xunit;

namespace PromptNavigator.Tests;

public sealed class ToolbarArtworkTests
{
  [Theory]
  [InlineData("Assets.promptflipper_logo.png", 24, 96)]
  [InlineData("Assets.folder.png", 20, 192)]
  [InlineData("QuickIcons.sanitize_button_64.png", 24, 144)]
  public void CatalogRendersLikeTheSourceAtTheRequestedDpi(string resource, int logicalSize, int dpi)
  {
    using var stream = AppAssembly.GetManifestResourceStream(resource);
    Assert.NotNull(stream);
    using var source = Image.FromStream(stream);
    using var expected = ToolbarArtwork.Render(source, ToolbarArtwork.Scale(logicalSize, dpi));
    using var actual = LoadArtwork(resource, logicalSize, dpi);
    Assert.NotNull(actual);
    AssertPixelsEqual(expected, actual);
  }

  [Fact]
  public void CatalogImagesAreIndependentAndPreserveNativeCanvas()
  {
    const string resource = "Assets.copy_24.png";
    using var stream = AppAssembly.GetManifestResourceStream(resource);
    using var decoded = new Bitmap(stream!);
    using var source = new Bitmap(decoded);
    using var first = LoadArtwork(resource);
    using var second = LoadArtwork(resource);
    Assert.NotNull(first);
    Assert.NotNull(second);
    Assert.NotSame(first, second);
    AssertPixelsEqual(source, first);
    first.SetPixel(0, 0, Color.Magenta);
    first.Dispose();
    AssertPixelsEqual(source, second);
  }

  [Theory]
  [InlineData("PromptFlipper.ico")]
  [InlineData("Assets.sanitize_32.png")]
  public void CatalogIconsRemainUsableAfterAnotherLoadIsDisposed(string resource)
  {
    using var first = LoadArtworkIcon(resource);
    using var second = LoadArtworkIcon(resource);
    Assert.NotNull(first);
    Assert.NotNull(second);
    first.Dispose();
    using var bitmap = second.ToBitmap();
    Assert.False(VisibleBounds(bitmap).IsEmpty);
  }

  [Fact]
  public void MissingOptionalArtworkReturnsNull()
  {
    Assert.Null(LoadArtwork("Assets.missing.png"));
    Assert.Null(LoadArtworkIcon("missing.ico"));
    Assert.Null(LoadArtworkIcon("Assets.missing.png"));
  }

  [Theory]
  [InlineData("sanitize_button", 96)]
  [InlineData("sanitize_button", 144)]
  [InlineData("sanitize_button", 192)]
  [InlineData("de_sanitize_button", 96)]
  [InlineData("de_sanitize_button", 144)]
  [InlineData("de_sanitize_button", 192)]
  public void QuickActionsUseExactDisplaySizePixelsWithoutCropping(string artwork, int dpi)
  {
    var pixels = ToolbarArtwork.Scale(20, dpi);
    var variant = new[] { 16, 20, 24, 32, 48, 64 }.First(size => size >= pixels);
    using var native = LoadArtwork($"Assets.copy_{variant}.png");
    using var expected = variant == pixels ? null : ToolbarArtwork.Render(native!, pixels);
    using var command = (Bitmap)LoadCatalogCommand("Copy", 20, dpi)!;
    AssertPixelsEqual(expected ?? native!, command);
    var quickPixels = ToolbarArtwork.Scale(32, dpi);
    using var quickExpected = LoadArtwork($"QuickIcons.{artwork}_{quickPixels}.png");
    using var quick = (Bitmap)LoadCatalogQuick(artwork, 32, dpi)!;
    AssertPixelsEqual(quickExpected!, quick);
    using var support = LoadCatalogSupport(20, dpi);
    Assert.NotNull(support);
    Assert.Equal(new Size(variant, variant), support.Size);
  }

  [Theory]
  [InlineData("sanitize_button", 120, 40)]
  [InlineData("de_sanitize_button", 168, 56)]
  public void QuickActionsFitTheirDisplaySizeAtIntermediateDpi(string artwork, int dpi, int pixels)
  {
    using var image = LoadCatalogQuick(artwork, 32, dpi);
    Assert.NotNull(image);
    Assert.Equal(new Size(pixels, pixels), image.Size);
    using var bitmap = new Bitmap(image);
    Assert.False(VisibleBounds(bitmap).IsEmpty);
  }

  [Fact]
  public void CanvasFitPreservesTransparentSpacingAndSourceProportions()
  {
    using var source = new Bitmap(200, 100);
    using (var graphics = Graphics.FromImage(source))
      graphics.FillRectangle(Brushes.Blue, 80, 30, 40, 40);
    using var result = ToolbarArtwork.FitCanvas(source, 40);
    var visible = VisibleBounds(result);
    Assert.InRange(visible.Width, 8, 10);
    Assert.InRange(visible.Height, 8, 10);
    Assert.Equal(visible.Left, visible.Top);
    Assert.Equal(0, result.GetPixel(0, 0).A);
  }

  private static void AssertPixelsEqual(Bitmap expected, Bitmap actual)
  {
    Assert.Equal(expected.Size, actual.Size);
    for (var y = 0; y < expected.Height; y++)
      for (var x = 0; x < expected.Width; x++)
        Assert.Equal(expected.GetPixel(x, y), actual.GetPixel(x, y));
  }

  [Theory]
  [InlineData(24)]
  [InlineData(48)]
  public void TransparentPaddingDoesNotChangeVisibleSizeOrProportions(int pixels)
  {
    using var tight = new Bitmap(80, 40);
    using (var graphics = Graphics.FromImage(tight)) graphics.Clear(Color.CornflowerBlue);
    using var padded = new Bitmap(320, 240);
    using (var graphics = Graphics.FromImage(padded)) graphics.DrawImageUnscaled(tight, 150, 70);
    using var tightResult = ToolbarArtwork.Render(tight, pixels);
    using var paddedResult = ToolbarArtwork.Render(padded, pixels);
    Assert.Equal(new Size(pixels, pixels), paddedResult.Size);
    var visible = VisibleBounds(paddedResult);
    Assert.Equal(VisibleBounds(tightResult), visible);
    Assert.Equal(tightResult.GetPixel(pixels / 2, pixels / 2),
      paddedResult.GetPixel(pixels / 2, pixels / 2));
    Assert.InRange(visible.Width, pixels * 5 / 6 - 2, pixels * 5 / 6 + 2);
    Assert.InRange((float)visible.Width / visible.Height, 1.7f, 2.3f);
    Assert.Equal(0, paddedResult.GetPixel(0, 0).A);
    Assert.InRange(Math.Abs(visible.Left - (pixels - visible.Right)), 0, 1);
  }

  [Fact]
  public void TransparentSourceStaysTransparent()
  {
    using var source = new Bitmap(100, 100);
    using var result = ToolbarArtwork.Render(source, 24);
    Assert.True(VisibleBounds(result).IsEmpty);
  }

  [Theory]
  [InlineData(24)]
  [InlineData(48)]
  public void SuppliedToolbarArtworkFitsAtNormalAndDoubleScale(int pixels)
  {
    var assembly = System.Reflection.Assembly.Load("PromptFlipper");
    foreach (var name in new[] { "folder", "save", "search", "text-size", "sanitize", "add", "previous", "next", "promptflipper_logo" })
    {
      using var stream = assembly.GetManifestResourceStream($"Assets.{name}.png");
      Assert.NotNull(stream);
      using var source = Image.FromStream(stream);
      using var result = ToolbarArtwork.Render(source, pixels);
      var visible = VisibleBounds(result);
      Assert.False(visible.IsEmpty);
      Assert.InRange(Math.Max(visible.Width, visible.Height), pixels * 5 / 6 - 2, pixels * 5 / 6 + 2);
      Assert.True(visible.Left > 0 && visible.Top > 0);
      Assert.True(visible.Right < pixels && visible.Bottom < pixels);
    }
  }

  [Fact]
  public void EmbeddedCommandsResolveWithoutSupersededPackImages()
  {
    var assembly = System.Reflection.Assembly.Load("PromptFlipper");
    var names = assembly.GetManifestResourceNames();
    Assert.DoesNotContain(names, name => name.StartsWith("Toolbar.")
      && !name.EndsWith(".Exit.png") && !name.EndsWith(".Support.png"));
    foreach (var asset in new[] { "print", "help", "copy", "delete" })
      foreach (var size in new[] { 16, 20, 24, 32, 48, 64 })
      {
        using var stream = assembly.GetManifestResourceStream($"Assets.{asset}_{size}.png");
        Assert.NotNull(stream);
        using var image = Image.FromStream(stream);
        Assert.Equal(new Size(size, size), image.Size);
      }
  }

  [Fact]
  public void MainCommandsAndAboutRenderWithSuppliedArtwork()
  {
    RunInSta(() =>
    {
      var assembly = System.Reflection.Assembly.Load("PromptFlipper");
      using var fixture = new MainFormTestHarness();
      using var main = fixture.CreateForm();
      foreach (var command in new[] { "Open", "Save", "Search", "TextSize", "Sanitize",
        "Print", "Help", "Copy", "Delete", "About", "Previous", "Next", "Exit" })
      {
        using var image = LoadCommandIcon(main, command, 24);
        Assert.NotNull(image);
        using var bitmap = new Bitmap(image);
        Assert.False(VisibleBounds(bitmap).IsEmpty);
      }
      using var about = (System.Windows.Forms.Form)Activator.CreateInstance(
        assembly.GetType("PromptNavigator.AboutDialog")!)!;
      foreach (var form in new[] { main, about })
      {
        form.Show();
        System.Windows.Forms.Application.DoEvents();
        if (form == main)
        {
          var quick = SanitizeToggle(main);
          Assert.Equal(ToolbarArtwork.Scale(new Size(40, 40), main.DeviceDpi), quick.Size);
          Assert.NotNull(quick.Image);
          Assert.Equal(ToolbarArtwork.Scale(new Size(32, 32), main.DeviceDpi), quick.Image.Size);
          Assert.True(quick.Parent!.ClientRectangle.Contains(quick.Bounds), "Quick action is clipped by its container");
        }
        if (form == about)
        {
          var fields = about.Controls.OfType<System.Windows.Forms.TableLayoutPanel>().Single();
          var buttons = about.Controls.OfType<System.Windows.Forms.FlowLayoutPanel>().Single();
          Assert.True(fields.Bottom <= buttons.Top, "About fields overlap the Close button row");
        }
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(Path.Combine(Path.GetTempPath(), form == main
          ? "prompt-flipper-task088-main.png" : "prompt-flipper-task088-about.png"));
        form.Hide();
      }
    }, timeoutMessage: "UI artwork rendering timed out");
  }

  private static Rectangle VisibleBounds(Bitmap bitmap)
  {
    var bounds = Rectangle.Empty;
    for (var y = 0; y < bitmap.Height; y++)
      for (var x = 0; x < bitmap.Width; x++)
        if (bitmap.GetPixel(x, y).A > 8)
          bounds = bounds.IsEmpty ? new Rectangle(x, y, 1, 1)
            : Rectangle.Union(bounds, new Rectangle(x, y, 1, 1));
    return bounds;
  }
}
