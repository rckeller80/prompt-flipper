using System.Collections;
using System.Reflection;
using System.Windows.Forms;
using PromptNavigator;

namespace PromptFlipper.Tests;

// Persistence tests own a temporary path. UI tests use the in-memory delegates below.
internal sealed class SanitizerTestStore : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "sanitizer-test-" + Guid.NewGuid());
    internal ProtectedRuleStore Store { get; }
    internal string PathName => Store.StoragePath;

    internal SanitizerTestStore()
    {
        Directory.CreateDirectory(root);
        Store = new ProtectedRuleStore(Path.Combine(root, "rules.dat"));
    }

    internal Form CreateDialog() => SanitizerTestRules.CreateDialog(load: Store.LoadRules, save: Store.Save);
    public void Dispose() => Directory.Delete(root, recursive: true);
}

internal static class SanitizerTestRules
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Assembly App = Assembly.Load("PromptFlipper");
    private static readonly Type Rule = App.GetType("PromptFlipper.SanitizerRule", true)!;

    internal static Delegate Loader(Func<IEnumerable<SanitizerRule>>? load = null)
    {
        Func<IEnumerable> adapted = () => (load?.Invoke() ?? []).Select(rule =>
            Activator.CreateInstance(Rule, rule.Name, rule.Value)!).ToArray();
        return (Delegate)typeof(SanitizerTestRules).GetMethod(nameof(TypedLoader), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(Rule).Invoke(null, [adapted])!;
    }

    private static Func<IEnumerable<T>> TypedLoader<T>(Func<IEnumerable> load) => () => load().Cast<T>();

    internal static Delegate Saver(Action<IEnumerable<SanitizerRule>>? save = null)
    {
        Action<object> adapted = values => save?.Invoke(((IEnumerable)values).Cast<object>().Select(rule =>
            new SanitizerRule((string)Rule.GetProperty("Name")!.GetValue(rule)!,
                (string)Rule.GetProperty("Value")!.GetValue(rule)!)));
        return Delegate.CreateDelegate(typeof(Action<>).MakeGenericType(typeof(IEnumerable<>).MakeGenericType(Rule)),
            adapted.Target, adapted.Method);
    }

    internal static Form CreateDialog(string? source = null, string? name = null,
        Func<IEnumerable<SanitizerRule>>? load = null, Action<IEnumerable<SanitizerRule>>? save = null,
        Delegate? saveDelegate = null) => (Form)Activator.CreateInstance(
            App.GetType("PromptNavigator.SanitizerDialog", true)!, Instance, null,
            [source, name, Loader(load), saveDelegate ?? Saver(save)], null)!;

    internal static Form CreateMainForm(string root, object cards,
        Func<IEnumerable<SanitizerRule>>? load = null) => (Form)Activator.CreateInstance(
            App.GetType("PromptNavigator.MainForm", true)!, Instance, null,
            [root, cards, Loader(load ?? (() => [new SanitizerRule("token", "synthetic-secret")])), Saver(),
             MainFormTestHarness.SettingsPathFor(root)], null)!;
}
