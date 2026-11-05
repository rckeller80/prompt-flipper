using System.Reflection;
using System.Text;
using Xunit;

namespace PromptFlipper.Tests;

public sealed class CardRepositoryFormatTests
{
  private static readonly Type RepositoryType = MainFormTestHarness.AppAssembly.GetType("PromptNavigator.CardRepository", true)!;
  private static readonly Func<string, string> GetEditableContent = RepositoryType
    .GetMethod("GetEditableContent")!.CreateDelegate<Func<string, string>>();
  private static readonly Func<string, string, string> BuildStoredContent = RepositoryType
    .GetMethod("BuildStoredContent")!.CreateDelegate<Func<string, string, string>>();

  [Theory]
  [InlineData("tags:\n\nBody", "Body")]
  [InlineData("tags: One, two\r\n\r\nBody\r\n", "Body\r\n")]
  [InlineData("tags:\n\n\nBody", "\nBody")]
  [InlineData("tags:\n\n", "")]
  [InlineData("tags:\n", "")]
  [InlineData("tags:\nBody", "Body")]
  [InlineData("tags: One", "tags: One")]
  [InlineData("Tags: One\n\nBody", "Tags: One\n\nBody")]
  [InlineData("plain\n\nBody", "plain\n\nBody")]
  [InlineData("", "")]
  public void EditableContentPreservesExistingHeaderHandling(string stored, string expected)
  {
    Assert.Equal(expected, GetEditableContent(stored));
  }

  [Theory]
  [InlineData("tags: One\n\nold", "new\n")]
  [InlineData("", "New prompt")]
  [InlineData("plain", "new\r\n")]
  public void StoredContentIsThePromptTextOnly(string stored, string editable)
  {
    Assert.Equal(editable, BuildStoredContent(stored, editable));
  }

  [Theory]
  [InlineData("\n")]
  [InlineData("\r\n")]
  public void SaveAndLoadPreserveBytes(string newline)
  {
    using var fixture = new MainFormTestHarness();
    var path = Path.Combine(fixture.Root, "format.md");
    var original = $"# Caf\u00e9{newline}{newline}Body{newline}";
    var repository = Activator.CreateInstance(RepositoryType)!;

    RepositoryType.GetMethod("Save")!.Invoke(repository, [fixture.Root, path, original]);
    Assert.Equal(new UTF8Encoding(false).GetBytes(original), File.ReadAllBytes(path));

    var loaded = Assert.Single(((System.Collections.IEnumerable)fixture.LoadCards()).Cast<object>());
    Assert.Equal(original, loaded.GetType().GetProperty("Content")!.GetValue(loaded));
    Assert.Equal(original, loaded.GetType().GetProperty("Body")!.GetValue(loaded));
  }

  [Fact]
  public void LegacyTagsLineIsHiddenOnLoad()
  {
    using var fixture = new MainFormTestHarness();
    File.WriteAllText(Path.Combine(fixture.Root, "legacy.md"), "tags: One\n\nBody");

    var loaded = Assert.Single(((System.Collections.IEnumerable)fixture.LoadCards()).Cast<object>());
    Assert.Equal("Body", loaded.GetType().GetProperty("Body")!.GetValue(loaded));
  }
}
