using System.Text;

namespace PromptNavigator;

internal sealed class CardRepository
{
  private const string TagsPrefix = "tags:";

  public static string GetEditableContent(string storedContent)
  {
    if (!TryReadTagsLine(storedContent, out _, out var firstLineEnd))
    {
      return storedContent;
    }

    var contentStart = firstLineEnd + 1;
    if (contentStart < storedContent.Length && storedContent[contentStart] == '\r')
    {
      contentStart++;
    }
    if (contentStart < storedContent.Length && storedContent[contentStart] == '\n')
    {
      contentStart++;
    }

    return storedContent[contentStart..];
  }

  // A card file is the prompt text only. A legacy "tags:" first line is hidden on load and
  // dropped on the next save.
  public static string BuildStoredContent(string existingContent, string editableContent) => editableContent;

  private static bool TryReadTagsLine(string content, out string tagsLine, out int firstLineEnd)
  {
    firstLineEnd = content.IndexOf('\n');
    tagsLine = firstLineEnd < 0 ? string.Empty : content[..firstLineEnd].TrimEnd('\r');
    return firstLineEnd >= 0 && tagsLine.StartsWith(TagsPrefix, StringComparison.Ordinal);
  }

  // Inaccessible entries are skipped instead of ending the walk, and reparse points are
  // excluded so a card root is not steered outside itself by a junction or symbolic link.
  private static readonly EnumerationOptions CardEnumeration = new()
  {
    RecurseSubdirectories = true,
    IgnoreInaccessible = true,
    AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System | FileAttributes.Hidden
  };

  public IReadOnlyList<Card> Load(string cardRoot)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(cardRoot);

    var fullRoot = Path.GetFullPath(cardRoot);
    var cards = new List<Card>();
    foreach (var path in Directory.EnumerateFiles(fullRoot, "*.md", CardEnumeration))
    {
      // A markdown file that is not a card is skipped. The card root is a folder the user
      // picks, so foreign .md files are expected there and must not make the folder unusable.
      if (TryLoadCard(fullRoot, path, out var card))
      {
        cards.Add(card);
      }
    }

    return cards
        .OrderBy(card => card.RelativeDirectory, StringComparer.OrdinalIgnoreCase)
        .ThenBy(card => Path.GetFileName(card.FilePath), StringComparer.OrdinalIgnoreCase)
        .ToArray();
  }

  public IReadOnlyList<Card> Search(IReadOnlyList<Card> cards, string query)
  {
    ArgumentNullException.ThrowIfNull(cards);

    if (PromptFlipper.TextSearch.Terms(query).Length == 0) return cards;
    return PromptFlipper.TextSearch
      .Filter(cards, query, card => [Path.GetFileName(card.FilePath), card.Title, card.Body])
      .ToArray();
  }

  public Card Rename(string cardRoot, Card card, string fileName)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(cardRoot);
    ArgumentNullException.ThrowIfNull(card);
    ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

    if (Path.IsPathFullyQualified(fileName)
        || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)
        || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
        || fileName.EndsWith(' ')
        || fileName.EndsWith('.'))
    {
      throw new ArgumentException($"'{fileName}' is not a valid file name.", nameof(fileName));
    }

    if (string.IsNullOrEmpty(Path.GetExtension(fileName)))
    {
      fileName += ".md";
    }

    if (!string.Equals(Path.GetExtension(fileName), ".md", StringComparison.OrdinalIgnoreCase))
    {
      throw new ArgumentException("A prompt card file name must end in .md.", nameof(fileName));
    }

    var fullRoot = Path.GetFullPath(cardRoot);
    var fullPath = Path.GetFullPath(card.FilePath);
    EnsureUnderRoot(fullRoot, fullPath);

    var targetPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(fullPath)!, fileName));
    EnsureUnderRoot(fullRoot, targetPath);
    return MoveCard(fullRoot, fullPath, targetPath);
  }

  public Card CreateNew(string cardRoot)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(cardRoot);
    return Save(cardRoot, GetUniqueCardPath(cardRoot, "new-prompt"), BuildStoredContent(string.Empty, "New prompt"));
  }

  // Adds a new card to the root with the template's content. The template file is only read.
  public Card CreateFromTemplate(string cardRoot, string templatePath)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(cardRoot);
    ArgumentException.ThrowIfNullOrWhiteSpace(templatePath);

    var content = File.ReadAllText(templatePath);
    var baseName = Path.GetFileNameWithoutExtension(templatePath);
    return Save(cardRoot, GetUniqueCardPath(cardRoot, string.IsNullOrWhiteSpace(baseName) ? "new-prompt" : baseName), content);
  }

  // The first free name of the form <baseName>.md, <baseName>-2.md, <baseName>-3.md, ...
  internal static string GetUniqueCardPath(string cardRoot, string baseName)
  {
    var number = 1;
    var filePath = Path.Combine(cardRoot, baseName + ".md");
    while (File.Exists(filePath))
    {
      number++;
      filePath = Path.Combine(cardRoot, $"{baseName}-{number}.md");
    }

    return filePath;
  }

  public void Delete(Card card)
  {
    ArgumentNullException.ThrowIfNull(card);
    File.Delete(card.FilePath);
  }

  public Card Save(string cardRoot, string filePath, string content)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(cardRoot);
    ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
    ArgumentNullException.ThrowIfNull(content);

    var fullRoot = Path.GetFullPath(cardRoot);
    var fullPath = Path.GetFullPath(filePath);
    if (!IsUnderRoot(fullRoot, fullPath))
    {
      throw new UnauthorizedAccessException(
          $"'{fullPath}' is outside the card folder '{fullRoot}' and was not written.");
    }

    var card = ParseCard(fullRoot, fullPath, content);
    File.WriteAllText(fullPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    return card;
  }

  private static bool IsUnderRoot(string fullRoot, string fullPath)
  {
    var prefix = Path.TrimEndingDirectorySeparator(fullRoot) + Path.DirectorySeparatorChar;
    return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
  }

  private static void EnsureUnderRoot(string fullRoot, string fullPath)
  {
    if (!IsUnderRoot(fullRoot, fullPath))
    {
      throw new UnauthorizedAccessException(
          $"'{fullPath}' is outside the card folder '{fullRoot}' and was not renamed.");
    }
  }

  private static Card MoveCard(string fullRoot, string fullPath, string targetPath)
  {
    if (string.Equals(fullPath, targetPath, StringComparison.Ordinal))
    {
      return LoadCard(fullRoot, fullPath);
    }

    if (File.Exists(targetPath)
        && !string.Equals(fullPath, targetPath, StringComparison.OrdinalIgnoreCase))
    {
      throw new IOException($"A card named '{Path.GetFileName(targetPath)}' already exists.");
    }

    File.Move(fullPath, targetPath);
    return LoadCard(fullRoot, targetPath);
  }

  private static bool TryLoadCard(string cardRoot, string filePath, out Card card)
  {
    try
    {
      card = LoadCard(cardRoot, filePath);
      return true;
    }
    catch (Exception exception) when (exception is InvalidDataException
        or IOException
        or UnauthorizedAccessException)
    {
      card = null!;
      return false;
    }
  }

  private static Card LoadCard(string cardRoot, string filePath)
  {
    var content = File.ReadAllText(filePath, Encoding.UTF8);
    return ParseCard(cardRoot, filePath, content);
  }

  private static Card ParseCard(string cardRoot, string filePath, string content)
  {
    var body = GetEditableContent(content);
    var relativeDirectory = Path.GetRelativePath(cardRoot, Path.GetDirectoryName(filePath)!);
    var fileName = Path.GetFileNameWithoutExtension(filePath);

    return new Card(
        fileName,
        filePath,
        relativeDirectory == "." ? string.Empty : relativeDirectory,
        body,
        content);
  }
}
