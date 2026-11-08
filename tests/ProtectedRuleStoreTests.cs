using System.Reflection;
using System.Text;
using System.Windows.Forms;
using PromptNavigator;
using Xunit;

namespace PromptFlipper.Tests;

[Collection("Quick sanitize Windows state")]
public sealed class ProtectedRuleStoreTests
{
    [Fact]
    public void MissingStoreIsEmptyAndDoesNotCreateAFile()
    {
        using var fixture = new SanitizerTestStore();
        var result = fixture.Store.Load();
        Assert.Null(result.Error);
        Assert.Empty(result.Rules);
        Assert.False(File.Exists(fixture.PathName));
        var missingParent = new ProtectedRuleStore(Path.Combine(fixture.PathName, "absent", "rules.dat"));
        Assert.Null(missingParent.Load().Error);
        Assert.Empty(missingParent.LoadRules());
    }

    [Fact]
    public void ProtectedRoundTripReplacesEncryptedFile()
    {
        using var fixture = new SanitizerTestStore();
        fixture.Store.Save([new SanitizerRule("old", "synthetic-old")]);
        var oldBytes = File.ReadAllBytes(fixture.PathName);
        fixture.Store.Save([new SanitizerRule("first", "synthetic-first"),
            new SanitizerRule("second", "synthetic-second")]);
        var bytes = File.ReadAllBytes(fixture.PathName);
        Assert.False(oldBytes.SequenceEqual(bytes));
        Assert.DoesNotContain("synthetic", Encoding.UTF8.GetString(bytes));
        var result = fixture.Store.Load();
        Assert.Null(result.Error);
        Assert.Equal(new[] { ("first", "synthetic-first"), ("second", "synthetic-second") },
            result.Rules.Select(rule => (rule.Name, rule.Value)));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(fixture.PathName)!));
    }

    [Theory]
    [InlineData("[{\"name\":\"legacy\",\"value\":\"synthetic-legacy\"}]", true)]
    [InlineData("[{\"name\":\"legacy\",\"value\":\"synthetic-legacy\",\"private\":true}]", true)]
    [InlineData("null", false)]
    [InlineData("[null]", false)]
    [InlineData("[{\"name\":\"invalid\"}]", false)]
    [InlineData("not JSON", false)]
    [InlineData("[{\"name\":\"invalid\",\"value\":\"\"}]", false)]
    public void EncryptedPayloadValidatesAndIgnoresLegacyPrivateField(string json, bool valid)
    {
        using var fixture = new SanitizerTestStore();
        var protect = typeof(ProtectedRuleStore).GetMethod("Protect", BindingFlags.NonPublic | BindingFlags.Static)!;
        File.WriteAllBytes(fixture.PathName, (byte[])protect.Invoke(null, [Encoding.UTF8.GetBytes(json)])!);
        var result = fixture.Store.Load();
        if (valid)
        {
            Assert.Null(result.Error);
            Assert.Equal("synthetic-legacy", Assert.Single(result.Rules).Value);
        }
        else AssertFailedLoad(fixture.Store);
    }

    [Fact]
    public void CorruptStoreFailsClosedWithoutChangingTheFile()
    {
        using var fixture = new SanitizerTestStore();
        byte[] corrupt = [1, 2, 3, 4];
        File.WriteAllBytes(fixture.PathName, corrupt);
        AssertFailedLoad(fixture.Store);
        Assert.Equal(corrupt, File.ReadAllBytes(fixture.PathName));
    }

    [Fact]
    public void UnreadableStoreFailsClosed()
    {
        using var fixture = new SanitizerTestStore();
        fixture.Store.Save([new SanitizerRule("token", "synthetic-secret")]);
        using var locked = new FileStream(fixture.PathName, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        AssertFailedLoad(fixture.Store);
    }

    [Fact]
    public void FailedReplacementPreservesOldRulesAndRemovesTemporaryFile()
    {
        using var fixture = new SanitizerTestStore();
        fixture.Store.Save([new SanitizerRule("old", "synthetic-old")]);
        var original = File.ReadAllBytes(fixture.PathName);
        using (var locked = new FileStream(fixture.PathName, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var failure = Record.Exception(() => fixture.Store.Save([new SanitizerRule("new", "synthetic-new")]));
            Assert.True(failure is IOException or UnauthorizedAccessException, $"Unexpected write result: {failure?.GetType().Name}");
        }
        Assert.Equal(original, File.ReadAllBytes(fixture.PathName));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(fixture.PathName)!));
        Assert.Equal("old", Assert.Single(fixture.Store.LoadRules()).Name);
    }

    [Fact]
    public void InvalidParentReportsWriteFailure()
    {
        using var fixture = new SanitizerTestStore();
        File.WriteAllText(fixture.PathName, "synthetic obstruction");
        var store = new ProtectedRuleStore(Path.Combine(fixture.PathName, "rules.dat"));
        Assert.ThrowsAny<IOException>(() => store.Save([new SanitizerRule("token", "synthetic-secret")]));
    }

    [Fact]
    public void DialogLoadsSyntheticRulesAndReportsUnavailableRules()
    {
        MainFormTestHarness.RunInSta(() =>
        {
            var calls = 0;
            using (var dialog = SanitizerTestRules.CreateDialog(load: () =>
            {
                calls++;
                return [new SanitizerRule("synthetic", "synthetic-value")];
            }))
            {
                Assert.Equal(1, calls);
                var list = Field<ListBox>(dialog, "ruleNames");
                Assert.Equal("synthetic", Assert.Single(list.Items.Cast<string>()));
            }
            using var unavailable = SanitizerTestRules.CreateDialog(load: () => throw new IOException("Synthetic read failure"));
            Assert.Empty(Field<ListBox>(unavailable, "ruleNames").Items);
            Assert.Equal("The protected secrets could not be loaded. Sanitizing remains disabled.",
                Field<Label>(unavailable, "status").Text);
        });
    }

    private static T Field<T>(Form dialog, string name) => (T)dialog.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;

    private static void AssertFailedLoad(ProtectedRuleStore store)
    {
        var result = store.Load();
        Assert.Empty(result.Rules);
        Assert.Equal("The protected secrets could not be loaded. Sanitizing remains disabled.", result.Error);
        Assert.False(Sanitizer.TryLoadRules(store.LoadRules, out var rules));
        Assert.Empty(rules);
    }
}
