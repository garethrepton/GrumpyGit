namespace GrumpyGit.App.ViewModels;

public class TagViewModel
{
    public string Name { get; init; } = string.Empty;
    public string ShortHash { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string DisplayText => string.IsNullOrEmpty(Message)
        ? $"{Name}  ({ShortHash})"
        : $"{Name}  ({ShortHash}) — {Message}";
}
