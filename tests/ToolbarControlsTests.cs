using System.Drawing;
using System.Reflection;
using static PromptFlipper.Tests.MainFormTestHarness;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using PromptNavigator;
using Xunit;

namespace PromptFlipper.Tests;

[Collection("Organization UI")]
public sealed class ToolbarControlsTests
{
  private static readonly int[] DisplayScales = [96, 120, 144, 168, 192];
  private static readonly string[] QuickActions = ["Copy", "Sanitizer", "Trash", "Add Card"];

  [Fact]
  public void ToolbarTooltipsUseTheConciseCommandNames()
  {
    WithMainForm((form, _) =>
    {
      var items = Toolbar(form).Items.Cast<ToolStripItem>().ToArray();
      Assert.Equal("Save", Item(items, "Save").ToolTipText);
      Assert.Equal("Print", Item(items, "Print").ToolTipText);
      Assert.Equal("Search", Item(items, "Search").ToolTipText);
      var search = SearchBox(form);
      Assert.Equal("Search", search.ToolTipText);
      Assert.IsType<ToolStripDropDown>(search.Owner);
      var textSize = Assert.IsType<ToolStripDropDownButton>(Item(items, "Text Size"));
      Assert.Equal("Text Size", textSize.ToolTipText);
      Assert.Equal(ToolStripItemDisplayStyle.Image, textSize.DisplayStyle);
      Assert.Equal("Support", Item(items, "Support").ToolTipText);
      // Toolbar artwork renders at the shared toolbar size.
      var toolbarPixels = ToolbarArtwork.Scale(ToolbarArtwork.ToolbarIconSize, form.DeviceDpi);
      foreach (var name in new[] { "Save", "Print", "Search", "Text Size" })
        Assert.Equal(new Size(toolbarPixels, toolbarPixels), Item(items, name).Image!.Size);
      // Image-only variants keep a readable label for the overflow menu and screen readers.
      Assert.Equal(ToolStripItemDisplayStyle.Image, Item(items, "Save").DisplayStyle);
      Assert.Equal("Save", Item(items, "Save").Text);
      Assert.Equal("Print", Item(items, "Print").Text);
    });
  }

  // Tooltip style rule (TASK-141): a control's tooltip is its accessible name, in Title Case.
  [Fact]
  public void EveryToolbarTooltipIsTheControlName()
  {
    WithMainForm((form, _) =>
    {
      foreach (var item in Toolbar(form).Items.Cast<ToolStripItem>()
                 .Where(item => item is not ToolStripSeparator))
      {
        var name = item is ToolStripControlHost host ? host.Control.AccessibleName : item.AccessibleName;
        Assert.Equal(name, item.ToolTipText);
      }
    });
  }

  [Fact]
  public void SearchMenuOpensImmediatelyFiltersShortQueriesAndKeepsTheToolbarStill()
  {
    WithMainForm((form, editor) =>
    {
      const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
      var search = SearchBox(form);
      var popup = SearchMenu(form);
      var toolbar = Toolbar(form);
      var button = Assert.IsType<ToolStripButton>(Item(toolbar.Items.Cast<ToolStripItem>(), "Search"));
      var root = CardRoot(form);
      File.WriteAllText(Path.Combine(root, "other.md"), "tags:\n\nother prompt");
      RefreshCardsFromDiskMethod.Invoke(form, null);

      foreach (var width in new[] { form.Width, form.MinimumSize.Width })
      {
        form.Width = width;
        Application.DoEvents();
        var positions = toolbar.Items.Cast<ToolStripItem>().Select(item => item.Bounds).ToArray();
        button.PerformClick();
        Assert.True(popup.Visible);
        Assert.Equal(search.Width, search.DropDownWidth);
        Assert.True(search.Width >= TextRenderer.MeasureText("WWWW", search.Font).Width + 32);
        Assert.DoesNotContain(search, toolbar.Items.Cast<ToolStripItem>());
        Application.DoEvents();
        Assert.True(search.ComboBox.Focused);
        var inputWidth = search.Width;
        foreach (var query in new[] { "o", "ot", "oth", "othe" })
        {
          search.Text = query;
          search.SelectionStart = query.Length;
          typeof(ComboBox).GetMethod("OnTextUpdate", flags)!.Invoke(search.ComboBox, [EventArgs.Empty]);
          if (query.Length == 1) Assert.Equal(2, search.Items.Count);
          else Assert.Equal("other.md", Assert.Single(search.Items.Cast<string>()));
          Assert.Equal(inputWidth, search.Width);
          Assert.Equal(positions, toolbar.Items.Cast<ToolStripItem>().Select(item => item.Bounds));
        }
        search.Text = "zzzz";
        typeof(ComboBox).GetMethod("OnTextUpdate", flags)!.Invoke(search.ComboBox, [EventArgs.Empty]);
        Assert.Empty(search.Items.Cast<object>());
        Assert.True(popup.Visible);
        var escape = new KeyEventArgs(Keys.Escape);
        SearchKey(form, escape);
        Assert.True(escape.Handled);
        Assert.False(popup.Visible);
        Assert.Empty(search.Text);
      }

      Assert.True((bool)ProcessCmdKeyMethod.Invoke(form, [new Message(), Keys.Control | Keys.F])!);
      Assert.True(popup.Visible);
      search.Text = "oth";
      typeof(ComboBox).GetMethod("OnTextUpdate", flags)!.Invoke(search.ComboBox, [EventArgs.Empty]);
      search.SelectedIndex = 0;
      Assert.Contains("other prompt", editor.Text);
      Assert.False(popup.Visible);
      Assert.True((bool)ProcessCmdKeyMethod.Invoke(form, [new Message(), Keys.Control | Keys.F])!);
      Assert.True((bool)ProcessCmdKeyMethod.Invoke(form, [new Message(), Keys.Escape])!);
      Assert.False(popup.Visible);
      Assert.Empty(search.Text);

      button.PerformClick();
      search.Text = "zzzz";
      typeof(ComboBox).GetMethod("OnTextUpdate", flags)!.Invoke(search.ComboBox, [EventArgs.Empty]);
      var noMatchEnter = new KeyEventArgs(Keys.Enter);
      SearchKey(form, noMatchEnter);
      Assert.False(noMatchEnter.Handled);
      Assert.True(popup.Visible);
      Assert.Contains("other prompt", editor.Text);

      SearchKey(form, new KeyEventArgs(Keys.Escape));
      button.PerformClick();
      search.Text = "o";
      search.SelectionStart = 1;
      typeof(ComboBox).GetMethod("OnTextUpdate", flags)!.Invoke(search.ComboBox, [EventArgs.Empty]);
      Assert.Equal(1, search.SelectionStart);
      Assert.Equal(2, search.Items.Count);
      var expectedPath = Path.Combine(root, Assert.IsType<string>(search.Items[0]));
      var enter = new KeyEventArgs(Keys.Enter);
      SearchKey(form, enter);
      Assert.True(enter.Handled);
      Assert.True(enter.SuppressKeyPress);
      Assert.False(popup.Visible);
      Assert.Equal(File.ReadAllText(expectedPath).Split("\n\n", 2)[1], editor.Text);
      Assert.True(editor.Focused);
    });
  }

  [Fact]
  public void WrapIsAnImageToggleWithPastelGreenActiveStateAndUnchangedText()
  {
    WithMainForm((form, editor) =>
    {
      var host = Assert.Single(Toolbar(form).Items.OfType<ToolStripControlHost>(),
        item => item.Control is CheckBox);
      var wrap = (CheckBox)host.Control;
      Assert.Equal(string.Empty, wrap.Text);
      Assert.NotNull(wrap.Image);
      Assert.Equal(Appearance.Button, wrap.Appearance);
      Assert.Equal("Wrap", wrap.AccessibleName);
      Assert.Equal(AccessibleRole.CheckButton, wrap.AccessibilityObject.Role);
      Assert.True(wrap.TabStop);
      Assert.True(wrap.Checked);
      Assert.True(wrap.AccessibilityObject.State.HasFlag(AccessibleStates.Checked));
      Assert.Equal(ToolbarArtwork.ActiveToggleBackColor, wrap.FlatAppearance.CheckedBackColor);
      Assert.Equal(ToolbarArtwork.ActiveToggleBackColor, CornerPixel(wrap));
      Assert.True(wrap.Image!.Width < wrap.Width && wrap.Image.Height < wrap.Height);

      var content = editor.Text;
      wrap.Focus();
      PressSpace(wrap);
      Assert.False(wrap.Checked);
      Assert.False(editor.WordWrap);
      Assert.False(wrap.AccessibilityObject.State.HasFlag(AccessibleStates.Checked));
      Assert.NotEqual(ToolbarArtwork.ActiveToggleBackColor, CornerPixel(wrap));
      Assert.Equal(content, editor.Text);

      PressSpace(wrap);
      Assert.True(wrap.Checked);
      Assert.True(editor.WordWrap);
      Assert.Equal(content, editor.Text);
    });
  }

  [Fact]
  public void QuickButtonsShowWholeArtworkAtDefaultAndMinimumWindowSize()
  {
    WithMainForm((form, _) =>
    {
      var buttons = QuickButtons(form);
      var expected = ToolbarArtwork.Scale(ToolbarArtwork.QuickButtonSize, form.DeviceDpi);
      var iconPixels = ToolbarArtwork.Scale(ToolbarArtwork.QuickIconSize, form.DeviceDpi);
      foreach (var size in new[] { form.Size, form.MinimumSize })
      {
        form.Size = size;
        Application.DoEvents();
        var header = buttons[0].Parent!.Parent!;
        for (var index = 0; index < buttons.Length; index++)
        {
          var button = buttons[index];
          Assert.Equal(expected, button.Size);
          Assert.True(header.ClientRectangle.Contains(
            header.RectangleToClient(button.RectangleToScreen(button.ClientRectangle))),
            $"{button.AccessibleName} is clipped by the header at {size}");
          Assert.True(form.ClientRectangle.Contains(
            form.RectangleToClient(button.RectangleToScreen(button.ClientRectangle))));
          if (index > 0) Assert.True(buttons[index - 1].Right <= button.Left);
          if (button.Image is { } image)
          {
            Assert.Equal(new Size(iconPixels, iconPixels), image.Size);
            Assert.True(image.Width + 8 <= button.Width && image.Height + 8 <= button.Height,
              $"{button.AccessibleName} artwork has no frame inside its button");
          }
          else
          {
            var text = TextRenderer.MeasureText(button.Text, button.Font);
            Assert.True(text.Width <= button.ClientSize.Width && text.Height <= button.ClientSize.Height);
          }
        }
        // The Previous and Next buttons at the bottom share the quick-button size and stay inside the window.
        foreach (var nav in new[] { PreviousButton(form), NextButton(form) })
        {
          Assert.Equal(expected, nav.Size);
          Assert.Equal(new Size(iconPixels, iconPixels), nav.Image!.Size);
          Assert.True(nav.Parent!.Parent!.ClientRectangle.Contains(
            nav.Parent.Parent.RectangleToClient(nav.RectangleToScreen(nav.ClientRectangle))),
            $"{nav.Name} is clipped at {size}");
        }
        var nameArea = Descendants(header).OfType<Panel>().First(panel => panel.Dock == DockStyle.Fill
          && panel is not TableLayoutPanel && panel is not FlowLayoutPanel);
        Assert.True(nameArea.Width > 0, $"Card name area collapsed at {size}");
      }
    });
  }

  [Fact]
  public void QuickButtonGeometryFitsEveryDisplayScale()
  {
    foreach (var dpi in DisplayScales)
    {
      var button = ToolbarArtwork.Scale(ToolbarArtwork.QuickButtonSize, dpi);
      var icon = ToolbarArtwork.Scale(ToolbarArtwork.QuickIconSize, dpi);
      Assert.True(icon + 8 <= button.Width && icon + 8 <= button.Height, $"Icon clipped at {dpi} DPI");
      Assert.True(icon >= 24, $"Icon too small to identify at {dpi} DPI");
      // Five actions, their margins and the Editing label fit the minimum 560 px window.
      var actions = QuickActions.Length * (button.Width + 2 * ToolbarArtwork.QuickButtonMargin);
      Assert.True(actions + ToolbarArtwork.Scale(64, dpi) <= 560 - 2 * 6 - 16,
        $"Quick actions crowd out the card name at {dpi} DPI");
    }
  }

  [Fact]
  public void QuickAndWrapArtworkIsEmbeddedAtEveryLoadedSize()
  {
    var assembly = AppAssembly;
    using (var stream = assembly.GetManifestResourceStream("Assets.wrap.png"))
    {
      Assert.NotNull(stream);
      using var wrap = Image.FromStream(stream);
      using var rendered = ToolbarArtwork.Render(wrap, 20);
      Assert.Equal(new Size(20, 20), rendered.Size);
    }
    foreach (var artwork in new[] { "sanitize_button", "de_sanitize_button" })
      foreach (var size in new[] { 16, 24, 32, 48, 64, 128, 256 })
      {
        using var stream = assembly.GetManifestResourceStream($"QuickIcons.{artwork}_{size}.png");
        Assert.NotNull(stream);
      }
    Assert.DoesNotContain(assembly.GetManifestResourceNames(), name => name.EndsWith("_512.png"));
  }

  private static void WithMainForm(Action<Form, RichTextBox> body)
  {
    RunInSta(() =>
    {
      using var fixture = new MainFormTestHarness();
      var root = fixture.Root;
      File.WriteAllText(Path.Combine(root, "card.md"),
        "tags:\n\n" + string.Join(' ', Enumerable.Repeat("long line text", 40)));
      using var form = fixture.CreateForm();
      form.Show();
      Application.DoEvents();
      var editor = CardPane(form);
      body(form, editor);
      form.Close();
    }, timeoutMessage: "Toolbar UI test timed out.");
  }

  private static ToolStrip Toolbar(Form form) =>
    Descendants(form).OfType<ToolStrip>().Single(strip => strip is not MenuStrip && strip is not StatusStrip);

  private static ToolStripItem Item(IEnumerable<ToolStripItem> items, string accessibleName) =>
    Assert.Single(items, item => item.AccessibleName == accessibleName);

  private static Button[] QuickButtons(Form form)
  {
    var copy = CopyButton(form);
    var buttons = copy.Parent!.Controls.OfType<Button>().ToArray();
    Assert.Equal(QuickActions, buttons.Select(button => button.AccessibleName));
    return buttons;
  }

  private static Color CornerPixel(Control control)
  {
    control.Refresh();
    using var bitmap = new Bitmap(control.Width, control.Height);
    control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
    var pixel = bitmap.GetPixel(2, 2);
    return Color.FromArgb(pixel.R, pixel.G, pixel.B);
  }

  private static void PressSpace(Control control)
  {
    SendMessage(control.Handle, 0x0100, (IntPtr)0x20, IntPtr.Zero);
    SendMessage(control.Handle, 0x0101, (IntPtr)0x20, (IntPtr)unchecked((int)0xC0000001));
    Application.DoEvents();
  }

  [DllImport("user32.dll")]
  private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

  private static IEnumerable<Control> Descendants(Control parent) =>
    parent.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Descendants(child)));

}
