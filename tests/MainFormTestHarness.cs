using System.Drawing;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using Xunit;

namespace PromptFlipper.Tests;

// Owns only the temporary directory it creates. Forms must be disposed before the fixture.
internal sealed class MainFormTestHarness : IDisposable
{
  internal static readonly Assembly AppAssembly = Assembly.Load("PromptFlipper");
  private static readonly Type MainFormType = AppAssembly.GetType("PromptNavigator.MainForm", true)!;
  private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

  internal string Root { get; } = Path.Combine(Path.GetTempPath(), "prompt-flipper-test-" + Guid.NewGuid().ToString("N"));

  internal MainFormTestHarness() => Directory.CreateDirectory(Root);

  // Settings live inside the temporary root so a test never writes the user's profile.
  internal static string SettingsPathFor(string root) => Path.Combine(root, ".test-settings", "settings.json");
  internal string SettingsPath => SettingsPathFor(Root);

  internal Form CreateForm(object? cards = null) => (Form)Activator.CreateInstance(
    MainFormType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
    binder: null, args: [Root, cards ?? LoadCards(), SettingsPath], culture: null)!;

  internal object LoadCards()
  {
    var type = AppAssembly.GetType("PromptNavigator.CardRepository", true)!;
    return type.GetMethod("Load")!.Invoke(Activator.CreateInstance(type), [Root])!;
  }

  internal sealed class FolderWatcherProbe : IDisposable
  {
    private readonly object watcher;
    private readonly Type type = AppAssembly.GetType("PromptNavigator.CardFolderWatcher", true)!;
    internal List<EventArgs> Changes { get; } = [];
    internal List<int> CallbackThreads { get; } = [];

    internal FolderWatcherProbe(Control owner)
    {
      watcher = Activator.CreateInstance(type, PrivateInstance, null, [owner], null)!;
      var changed = type.GetEvent("Changed", PrivateInstance)!;
      var callback = GetType().GetMethod(nameof(OnChanged), PrivateInstance)!;
      changed.GetAddMethod(true)!.Invoke(watcher,
        [Delegate.CreateDelegate(changed.EventHandlerType!, this, callback)]);
    }

    private void OnChanged(object? sender, EventArgs args)
    {
      Changes.Add(args);
      CallbackThreads.Add(Environment.CurrentManagedThreadId);
    }

    internal void Watch(string root) => type.GetMethod("Watch", PrivateInstance)!.Invoke(watcher, [root]);
    internal void RequestRefresh() => type.GetMethod("RequestRefresh", PrivateInstance)!.Invoke(watcher, null);
    internal string MapPath(int batch, string path) => (string)Changes[batch].GetType()
      .GetMethod("MapPath", PrivateInstance)!.Invoke(Changes[batch], [path])!;
    public void Dispose() => ((IDisposable)watcher).Dispose();
  }

  public void Dispose() => Directory.Delete(Root, recursive: true);

  internal static void RunInSta(Action action, string timeoutMessage = "MainForm test timed out.")
  {
    Exception? failure = null;
    var thread = new Thread(() =>
    {
      try { action(); }
      catch (Exception exception) { failure = exception; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    Assert.True(thread.Join(TimeSpan.FromSeconds(30)), timeoutMessage);
    if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
  }

  private static FieldInfo Field(string name) => MainFormType.GetField(name, PrivateInstance)
    ?? throw new MissingFieldException(MainFormType.FullName, name);

  private static MethodInfo Method(string name) => MainFormType.GetMethod(name, PrivateInstance)
    ?? throw new MissingMethodException(MainFormType.FullName, name);

  internal static Button SanitizeToggle(Form form) => (Button)Field("sanitizeToggle").GetValue(form)!;
  private static object FeedbackComponent(Form form) => Field("successFeedback").GetValue(form)!;
  internal static PictureBox SuccessComic(Form form) => (PictureBox)FeedbackComponent(form)
    .GetType().GetProperty("Comic", PrivateInstance)!.GetValue(FeedbackComponent(form))!;
  internal static bool IsSuccessNudging(Form form) => (bool)FeedbackComponent(form)
    .GetType().GetProperty("IsNudging", PrivateInstance)!.GetValue(FeedbackComponent(form))!;
  internal static ToolStripStatusLabel FileStatusLabel(Form form) =>
    (ToolStripStatusLabel)Field("fileStatusLabel").GetValue(form)!;

  internal static FieldInfo CardPaneField => Field("cardPane");
  internal static RichTextBox CardPane(Form form) => (RichTextBox)CardPaneField.GetValue(form)!;
  private static FieldInfo CardTabsField => Field("cardTabs");
  internal static TabControl CardTabs(Form form) => (TabControl)CardTabsField.GetValue(form)!;
  internal static string? DraggedCardPath(Form form) =>
    (string?)CardTabs(form).GetType().GetField("draggedCardPath", PrivateInstance)!.GetValue(CardTabs(form));
  internal static bool TabDragStarted(Form form) =>
    (bool)CardTabs(form).GetType().GetField("tabDragStarted", PrivateInstance)!.GetValue(CardTabs(form))!;
  internal static IReadOnlyList<string> OpenCardPaths(Form form) =>
    (IReadOnlyList<string>)CardTabs(form).GetType().GetProperty("Paths", PrivateInstance)!.GetValue(CardTabs(form))!;
  internal static Rectangle GetTabCloseBounds(TabControl tabs, int index) =>
    (Rectangle)InvokeTab(tabs, "GetTabCloseBounds", index)!;
  internal static object? InvokeTab(TabControl tabs, string method, params object?[] arguments) =>
    tabs.GetType().GetMethod(method, PrivateInstance)!.Invoke(tabs, arguments);
  internal static TabControl CreateTabStrip() => (TabControl)Activator.CreateInstance(
    AppAssembly.GetType("PromptNavigator.CardTabStrip", true)!, nonPublic: true)!;
  internal static void SubscribeTabEvent(TabControl tabs, string name, Action<object?, EventArgs> handler)
  {
    var eventInfo = tabs.GetType().GetEvent(name, PrivateInstance)!;
    eventInfo.GetAddMethod(true)!.Invoke(tabs,
      [Delegate.CreateDelegate(eventInfo.EventHandlerType!, handler.Target, handler.Method)]);
  }
  private static object SearchComponent(Form form) => Field("cardSearch").GetValue(form)!;
  internal static ToolStripComboBox SearchBox(Form form) => (ToolStripComboBox)SearchComponent(form)
    .GetType().GetProperty("Input", PrivateInstance)!.GetValue(SearchComponent(form))!;
  internal static ToolStripDropDown SearchMenu(Form form) => (ToolStripDropDown)SearchComponent(form)
    .GetType().GetProperty("Menu", PrivateInstance)!.GetValue(SearchComponent(form))!;
  internal static void RefreshSearch(Form form) => SearchComponent(form).GetType()
    .GetMethod("SetCards", PrivateInstance)!.Invoke(SearchComponent(form), [CardsField.GetValue(form)]);
  internal static void SearchKey(Form form, KeyEventArgs args) => SearchComponent(form).GetType()
    .GetMethod("HandleKey", PrivateInstance)!.Invoke(SearchComponent(form), [args]);
  private static FieldInfo CardRootField => Field("cardRoot");
  internal static string CardRoot(Form form) => (string)CardRootField.GetValue(form)!;
  private static FieldInfo CopyButtonField => Field("copyButton");
  internal static Button CopyButton(Form form) => (Button)CopyButtonField.GetValue(form)!;
  private static FieldInfo PreviousButtonField => Field("previousButton");
  internal static Button PreviousButton(Form form) => (Button)PreviousButtonField.GetValue(form)!;
  private static FieldInfo NextButtonField => Field("nextButton");
  internal static Button NextButton(Form form) => (Button)NextButtonField.GetValue(form)!;
  internal static FieldInfo CurrentIndexField => Field("currentIndex");
  internal static FieldInfo CollectionStatusLabelField => Field("collectionStatusLabel");
  internal static FieldInfo CardsField => Field("cards");
  internal static FieldInfo HelpWindowField => Field("helpWindow");
  internal static MethodInfo BrowseAllCardsMethod => Method("BrowseAllCards");
  internal static MethodInfo CloseCardTabMethod => Method("CloseCardTab");
  private static MethodInfo LoadCommandIconMethod => Method("LoadCommandIcon");
  internal static MethodInfo MainFormOnKeyDownMethod => Method("MainFormOnKeyDown");
  internal static MethodInfo MoveCardMethod => Method("MoveCard");
  internal static MethodInfo ProcessCmdKeyMethod => Method("ProcessCmdKey");
  internal static MethodInfo RefreshCardsFromDiskMethod => Method("RefreshCardsFromDisk");
  internal static MethodInfo RemoveCurrentCardMethod => Method("RemoveCurrentCard");
  internal static MethodInfo SelectCardMethod => Method("SelectCard");
  internal static MethodInfo ShowCurrentCardMethod => Method("ShowCurrentCard");
  internal static MethodInfo ShowHelpMethod => Method("ShowHelp");
  internal static MethodInfo TrySaveCurrentCardMethod => Method("TrySaveCurrentCard");

  internal static object? ProcessFormKey(Form form, Keys key) =>
    form.GetType().GetMethod("ProcessCmdKey", PrivateInstance)!.Invoke(form, [new Message(), key]);

  internal static FieldInfo? CollectionListField => MainFormType.GetField("collectionList", PrivateInstance);
  internal static Image? LoadCommandIcon(Form form, string command, int pixels) =>
    (Image?)LoadCommandIconMethod.Invoke(form, [command, pixels]);
  internal static Bitmap? LoadArtwork(string resource, int? logicalSize = null, int dpi = 96) =>
    (Bitmap?)InvokeArtwork("LoadImage", resource, logicalSize!, dpi);
  internal static Icon? LoadArtworkIcon(string resource) => (Icon?)InvokeArtwork("LoadIcon", resource);
  internal static Image? LoadCatalogCommand(string command, int logicalSize, int dpi) =>
    (Image?)InvokeArtwork("LoadCommandIcon", command, logicalSize, dpi);
  internal static Image? LoadCatalogQuick(string artwork, int logicalSize, int dpi) =>
    (Image?)InvokeArtwork("LoadQuickActionIcon", artwork, logicalSize, dpi);
  internal static Image? LoadCatalogSupport(int logicalSize, int dpi) =>
    (Image?)InvokeArtwork("LoadSupportIcon", logicalSize, dpi);

  private static object? InvokeArtwork(string method, params object[] arguments) =>
    AppAssembly.GetType("PromptNavigator.ToolbarArtwork", true)!
      .GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, arguments);

  internal static void HideSuccessComic(Form form) => FeedbackComponent(form).GetType()
    .GetMethod("Hide", PrivateInstance)!.Invoke(FeedbackComponent(form), null);

  internal static string BuildLocationCue(string rootPath, string cardPath) =>
    InvokeFormatter("BuildCardLocationCue", rootPath, cardPath);
  internal static string BuildProgressCue(int index, int count) =>
    InvokeFormatter("BuildCollectionProgressCue", index, count);

  private static string InvokeFormatter(string name, params object[] arguments)
  {
    var method = MainFormType.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static);
    Assert.NotNull(method);
    return Assert.IsType<string>(method.Invoke(null, arguments));
  }
}
