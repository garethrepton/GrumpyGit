namespace GrumpyGit.App.ViewModels;

public class IssueViewModel
{
    public int Number { get; init; }
    public string Title { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string Labels { get; init; } = string.Empty;

    public string DisplayText => $"#{Number}  {Title}";
}
