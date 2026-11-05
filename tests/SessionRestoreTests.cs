using System.Reflection;
using System.Windows.Forms;
using static PromptFlipper.Tests.MainFormTestHarness;
using Xunit;

namespace PromptFlipper.Tests;

[Collection("Organization UI")]
public sealed class SessionRestoreTests
{
  [Fact]
  public void RelaunchRestoresTheFolderAndTheOpenCards()
  {
    RunInSta(() =>
    {
      using var fixture = new MainFormTestHarness();
      var paths = new[] { "a.md", "b.md", "c.md" }.Select(name => Path.Combine(fixture.Root, name)).ToArray();
      foreach (var path in paths) File.WriteAllText(path, "Prompt " + Path.GetFileName(path));

      using (var first = fixture.CreateForm())
      {
        first.Show();
        SelectCardMethod.Invoke(first, [paths[1]]);
        SelectCardMethod.Invoke(first, [paths[2]]);
        CloseCardTabMethod.Invoke(first, [paths[0]]);
        Assert.Equal(new[] { paths[1], paths[2] }, OpenCardPaths(first));
        first.Close();
      }

      using var second = fixture.CreateForm();
      second.Show();
      Assert.Equal(fixture.Root, CardRoot(second));
      Assert.Equal(new[] { paths[1], paths[2] }, OpenCardPaths(second));
      Assert.Equal(2, (int)CurrentIndexField.GetValue(second)!);
      second.Close();
    });
  }

  [Fact]
  public void AddFromTemplateCreatesANewCardAndKeepsTheFolder()
  {
    RunInSta(() =>
    {
      using var fixture = new MainFormTestHarness();
      File.WriteAllText(Path.Combine(fixture.Root, "existing.md"), "Existing prompt");
      var templateFolder = Path.Combine(Path.GetTempPath(), "prompt-flipper-template-" + Guid.NewGuid().ToString("N"));
      Directory.CreateDirectory(templateFolder);
      try
      {
        var template = Path.Combine(templateFolder, "review.md");
        File.WriteAllText(template, "Review this change");
        using var form = fixture.CreateForm();
        form.Show();
        var addFromTemplate = form.GetType().GetMethod("AddCardFromTemplate", BindingFlags.Instance | BindingFlags.NonPublic)!;

        Assert.NotNull(addFromTemplate.Invoke(form, [template]));
        Assert.NotNull(addFromTemplate.Invoke(form, [template]));

        var created = Path.Combine(fixture.Root, "review.md");
        var clash = Path.Combine(fixture.Root, "review-2.md");
        Assert.Equal(fixture.Root, CardRoot(form));
        Assert.Equal("Review this change", File.ReadAllText(created));
        Assert.Equal("Review this change", File.ReadAllText(clash));
        Assert.Equal("Review this change", File.ReadAllText(template));
        Assert.Single(Directory.GetFiles(templateFolder));
        Assert.Contains(created, OpenCardPaths(form));
        Assert.Equal(clash, OpenCardPaths(form)[^1]);
        Assert.Contains("created from template", FileStatusLabel(form).Text);
        form.Close();
      }
      finally
      {
        Directory.Delete(templateFolder, recursive: true);
      }
    });
  }
}
