using System.Security;

namespace PromptNavigator;

internal static class Program
{
  [STAThread]
  private static void Main(string[] args)
  {
    Application.EnableVisualStyles();
    Application.SetCompatibleTextRenderingDefault(false);

    // Without these, any escaped exception reaches the runtime's crash dialog. The build
    // publishes without symbols, so that dialog tells the user nothing and loses the message.
    Application.ThreadException += (_, eventArgs) => ReportFatal(eventArgs.Exception);
    AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        ReportFatal(eventArgs.ExceptionObject as Exception);

    var settings = new SettingsStore();
    var cardRoot = ResolveCardRoot(args, settings);
    if (cardRoot is null)
    {
      return;
    }

    settings.SaveCardRoot(cardRoot);

    try
    {
      var cards = new CardRepository().Load(cardRoot);
      Application.Run(new MainForm(cardRoot, cards));
    }
    catch (Exception exception)
    {
      MessageBox.Show(
          $"The card folder could not be loaded.\n\n{exception.Message}",
          "Prompt Flipper",
          MessageBoxButtons.OK,
          MessageBoxIcon.Error);
    }
  }

  private static void ReportFatal(Exception? exception)
  {
    MessageBox.Show(
        $"Prompt Flipper stopped unexpectedly.\n\n{exception?.Message ?? "No details are available."}",
        "Prompt Flipper",
        MessageBoxButtons.OK,
        MessageBoxIcon.Error);
  }

  private static string? ResolveCardRoot(string[] args, SettingsStore settings)
  {
    if (args.Length > 0)
    {
      // A path handed in on the command line is arbitrary text: GetFullPath rejects an
      // invalid or over-long one by throwing, which would end the process before any window.
      string? argumentRoot = null;
      try
      {
        argumentRoot = Path.GetFullPath(args[0]);
      }
      catch (Exception exception) when (exception is ArgumentException
          or NotSupportedException
          or PathTooLongException
          or SecurityException)
      {
        argumentRoot = null;
      }

      if (argumentRoot is not null && Directory.Exists(argumentRoot))
      {
        return argumentRoot;
      }

      MessageBox.Show(
          $"The card folder does not exist:\n{argumentRoot ?? args[0]}",
          "Prompt Flipper",
          MessageBoxButtons.OK,
          MessageBoxIcon.Warning);
    }

    var savedRoot = settings.LoadCardRoot();
    if (savedRoot is not null && Directory.Exists(savedRoot))
    {
      return savedRoot;
    }

    var suggestedRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "Prompt Flipper",
        "Cards");

    using var dialog = new FolderBrowserDialog
    {
      Description = "Choose or create the folder that contains your prompt cards.",
      ShowNewFolderButton = true,
      UseDescriptionForTitle = true,
      SelectedPath = Directory.Exists(suggestedRoot)
            ? suggestedRoot
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
    };

    return dialog.ShowDialog() == DialogResult.OK
        ? Path.GetFullPath(dialog.SelectedPath)
        : null;
  }
}
