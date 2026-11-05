using System.Security;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PromptNavigator;

// The cards that were open when the app closed, for the folder they belong to.
internal sealed record CardSession(IReadOnlyList<string> OpenCards, string? ActiveCard);

internal sealed class SettingsStore
{
  private static readonly JsonSerializerOptions JsonOptions = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true
  };

  private readonly string settingsPath;

  public SettingsStore()
    : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PromptFlipper", "settings.json"))
  {
  }

  internal SettingsStore(string settingsPath)
  {
    this.settingsPath = settingsPath;
  }

  public string? LoadCardRoot() => Read()?.CardRoot;

  // Returns the saved session only when it was saved for this folder.
  public CardSession? LoadSession(string cardRoot)
  {
    var settings = Read();
    if (settings?.OpenCards is null || !IsSameRoot(settings.CardRoot, cardRoot))
    {
      return null;
    }

    return new CardSession(settings.OpenCards.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray(),
      settings.ActiveCard);
  }

  public void SaveCardRoot(string cardRoot)
  {
    // Saving the same folder again keeps its session; a different folder starts without one.
    var existing = Read();
    Write(() =>
    {
      var fullRoot = Path.GetFullPath(cardRoot);
      return existing is not null && IsSameRoot(existing.CardRoot, fullRoot)
        ? existing with { CardRoot = fullRoot }
        : new Settings(fullRoot);
    });
  }

  public void SaveSession(string cardRoot, IEnumerable<string> openCards, string? activeCard) =>
    Write(() => new Settings(Path.GetFullPath(cardRoot), openCards.ToArray(), activeCard));

  private Settings? Read()
  {
    if (!File.Exists(settingsPath))
    {
      return null;
    }

    // The settings file is on disk and can be corrupt, locked or unreadable. None of that is
    // worth ending the process for: the app falls back to asking for the folder.
    try
    {
      var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(settingsPath), JsonOptions);
      return string.IsNullOrWhiteSpace(settings?.CardRoot) ? null : settings;
    }
    catch (Exception exception) when (exception is JsonException
        or IOException
        or UnauthorizedAccessException
        or SecurityException)
    {
      return null;
    }
  }

  private void Write(Func<Settings> build)
  {
    // Remembering the folder and tabs is a convenience. A profile that cannot be written must
    // not stop the user from working with their cards.
    try
    {
      var json = JsonSerializer.Serialize(build(), JsonOptions);
      Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
      File.WriteAllText(settingsPath, json);
    }
    catch (Exception exception) when (exception is IOException
        or UnauthorizedAccessException
        or SecurityException
        or ArgumentException
        or NotSupportedException)
    {
    }
  }

  private static bool IsSameRoot(string savedRoot, string cardRoot)
  {
    try
    {
      return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(savedRoot)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(cardRoot)), StringComparison.OrdinalIgnoreCase);
    }
    catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
    {
      return false;
    }
  }

  private sealed record Settings(string CardRoot, string[]? OpenCards = null, string? ActiveCard = null);
}
