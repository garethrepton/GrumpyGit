using System;
using System.Collections.Generic;
using System.Linq;

namespace GrumpyGit.App.ViewModels;

public class PullRequestViewModel
{
    public int Number { get; init; }
    public string Title { get; init; } = string.Empty;
    public string AuthorLogin { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
    public string HeadBranch { get; init; } = string.Empty;
    public string BaseBranch { get; init; } = string.Empty;
    public bool IsDraft { get; init; }
    public string ReviewState { get; init; } = string.Empty;
    public string Labels { get; init; } = string.Empty;

    public string DisplayTitle => $"#{Number}  {Title}";
    public string BranchInfo => $"{BaseBranch} \u2190 {HeadBranch}";
    public string AuthorInfo => $"by {AuthorLogin} on {CreatedAt:yyyy-MM-dd}";
    public string DraftBadge => IsDraft ? "DRAFT" : string.Empty;
    public bool ShowDraftBadge => IsDraft;
}
