using System.Reflection;
using System.Text.Json;
using Xunit;

namespace PromptFlipper.Tests;

public sealed class SettingsStoreTests
{
    [Fact]
    public void MissingSettingsReturnNullWithoutCreatingAFile()
    {
        using var fixture = new SettingsFixture();
        Assert.Null(fixture.Load());
        Assert.False(File.Exists(fixture.PathName));
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"cardRoot\":42}")]
    public void InvalidOrEmptySettingsReturnNullWithoutChangingTheFile(string json)
    {
        using var fixture = new SettingsFixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.PathName)!);
        File.WriteAllText(fixture.PathName, json);
        Assert.Null(fixture.Load());
        Assert.Equal(json, File.ReadAllText(fixture.PathName));
    }

    [Fact]
    public void SaveCreatesDirectoriesAndRoundTripsNormalizedRoots()
    {
        using var fixture = new SettingsFixture();
        var cardRoot = Path.Combine(fixture.Root, "cards", "..", "selected cards");
        fixture.Save(cardRoot);
        var expected = Path.GetFullPath(cardRoot);
        Assert.Equal(expected, fixture.Load());
        using var json = JsonDocument.Parse(File.ReadAllText(fixture.PathName));
        Assert.Equal(expected, json.RootElement.GetProperty("cardRoot").GetString());
        Assert.Single(json.RootElement.EnumerateObject());
        fixture.Save("relative-cards");
        Assert.Equal(Path.GetFullPath("relative-cards"), fixture.Load());
    }

    [Fact]
    public void LockedSettingsReturnNullAndSwallowWriteFailure()
    {
        using var fixture = new SettingsFixture();
        fixture.Save(fixture.Root);
        var original = File.ReadAllBytes(fixture.PathName);
        using (var locked = new FileStream(fixture.PathName, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Null(fixture.Load());
            Assert.Null(Record.Exception(() => fixture.Save("another-root")));
        }
        Assert.Equal(original, File.ReadAllBytes(fixture.PathName));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectoryOrReadOnlyDestinationDoesNotInterruptSaving(bool readOnly)
    {
        using var fixture = new SettingsFixture();
        if (readOnly)
        {
            fixture.Save(fixture.Root);
            File.SetAttributes(fixture.PathName, FileAttributes.ReadOnly);
        }
        else Directory.CreateDirectory(fixture.PathName);
        try
        {
            Assert.Null(Record.Exception(() => fixture.Save("another-root")));
            if (readOnly) Assert.Equal(fixture.Root, fixture.Load());
            else Assert.Empty(Directory.GetFileSystemEntries(fixture.PathName));
        }
        finally
        {
            if (readOnly) File.SetAttributes(fixture.PathName, FileAttributes.Normal);
        }
    }

    [Fact]
    public void InvalidCardRootDoesNotReplaceSavedSettings()
    {
        using var fixture = new SettingsFixture();
        fixture.Save(fixture.Root);
        Assert.Null(Record.Exception(() => fixture.Save("invalid\0root")));
        Assert.Equal(fixture.Root, fixture.Load());
    }

    [Fact]
    public void SessionIsKeptForTheSameFolderAndDroppedForAnother()
    {
        using var fixture = new SettingsFixture();
        var card = Path.Combine(fixture.Root, "a.md");
        var other = Path.Combine(fixture.Root, "other");
        fixture.Invoke("SaveSession", [fixture.Root, new[] { card }, card]);
        fixture.Save(fixture.Root);
        Assert.NotNull(fixture.Invoke("LoadSession", [fixture.Root]));
        Assert.Null(fixture.Invoke("LoadSession", [other]));
        fixture.Save(other);
        Assert.Null(fixture.Invoke("LoadSession", [other]));
    }

    private sealed class SettingsFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "settings-test-" + Guid.NewGuid());
        internal string PathName => Path.Combine(Root, "nested", "settings.json");
        internal Func<string?> Load { get; }
        internal Action<string> Save { get; }

        internal SettingsFixture()
        {
            Directory.CreateDirectory(Root);
            var type = Assembly.Load("PromptFlipper").GetType("PromptNavigator.SettingsStore", true)!;
            var store = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic,
                null, [PathName], null)!;
            Load = type.GetMethod("LoadCardRoot")!.CreateDelegate<Func<string?>>(store);
            Save = type.GetMethod("SaveCardRoot")!.CreateDelegate<Action<string>>(store);
            Invoke = (name, arguments) => type.GetMethod(name)!.Invoke(store, arguments);
        }

        internal Func<string, object?[], object?> Invoke { get; }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
