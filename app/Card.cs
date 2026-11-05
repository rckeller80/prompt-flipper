namespace PromptNavigator;

internal sealed record Card(
    string Title,
    string FilePath,
    string RelativeDirectory,
    string Body,
    string Content);
