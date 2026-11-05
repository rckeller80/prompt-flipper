using static PromptFlipper.Tests.MainFormTestHarness;
using System.Windows.Forms;
using Xunit;

namespace PromptFlipper.Tests;

[Collection("Quick sanitize Windows state")]
public sealed class MainFormCopyTests
{
  [Theory]
  [InlineData(false)]
  [InlineData(true)]
  public void CopyUsesWholeCurrentEditorAndPreservesSelection(bool hasCard)
  {
    RunInSta(() =>
    {
      using var fixture = new MainFormTestHarness();
      var root = fixture.Root;
      var originalClipboard = Clipboard.GetDataObject();
      WindowsStateTestScope.RunWithCleanup(() =>
      {
        var path = Path.Combine(root, "card.md");
        if (hasCard) File.WriteAllText(path, "tags:\n\nOriginal prompt");
        using var form = fixture.CreateForm();
        form.Show();
        var button = CopyButton(form);
        var editor = CardPane(form);
        Assert.Equal(string.Empty, button.Text);
        Assert.Equal("Copy", button.AccessibleName);
        Assert.NotNull(button.Image);
        Assert.False(button.Font.Bold);
        Assert.Equal(PromptNavigator.ToolbarArtwork.Scale(
          PromptNavigator.ToolbarArtwork.QuickButtonSize, form.DeviceDpi), button.Size);
        var actions = button.Parent!.Controls.OfType<Button>().ToArray();
        Assert.Equal(new[] { "Copy", "Sanitizer", "Trash", "Add Card" },
          actions.Select(action => action.AccessibleName));
        Assert.All(actions.Take(3), action => { Assert.Equal(string.Empty, action.Text); Assert.NotNull(action.Image); });
        Assert.All(actions, action => Assert.Equal(button.Size, action.Size));
        Assert.Equal("+", actions[3].Text);
        for (var index = 1; index < actions.Length; index++)
          Assert.True(actions[index - 1].Right <= actions[index].Left);
        Assert.True(form.PointToClient(button.PointToScreen(System.Drawing.Point.Empty)).Y <
          form.PointToClient(editor.PointToScreen(System.Drawing.Point.Empty)).Y);
        Clipboard.SetText("clipboard sentinel");
        Assert.Equal(hasCard, button.Enabled);
        if (hasCard)
        {
          editor.Text = "Unsaved full prompt\r\nSecond line";
          editor.Select(2, 5);
          button.PerformClick();
          // RichEdit reports LF; the copy carries the card's own CRLF text.
          Assert.Equal("Unsaved full prompt\r\nSecond line", Clipboard.GetText());
          Assert.Equal(2, editor.SelectionStart);
          Assert.Equal(5, editor.SelectionLength);
          Assert.Equal("tags:\n\nOriginal prompt", File.ReadAllText(path));
          editor.Clear();
          Assert.False(button.Enabled);
          Clipboard.SetText("clipboard sentinel");
        }
        button.PerformClick();
        Assert.Equal("clipboard sentinel", Clipboard.GetText());
        form.Close();
      }, () => WindowsStateTestScope.RestoreClipboard(originalClipboard));
    });
  }
}
