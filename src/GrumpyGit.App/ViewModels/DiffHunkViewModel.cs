using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using GrumpyGit.Core.Models;

namespace GrumpyGit.App.ViewModels;

public partial class DiffHunkViewModel : ObservableObject
{
    public DiffHunk Hunk { get; init; } = null!;

    /// <summary>1-based line number in the rendered editor where this hunk's @@ header appears.</summary>
    public int RenderedLineNumber { get; init; }

    /// <summary>True when the hunk is from a staged diff (show "Unstage" button), false for unstaged (show "Stage" button).</summary>
    public bool IsStaged { get; init; }

    /// <summary>Command to stage this hunk.</summary>
    public ICommand? StageHunkCommand { get; init; }

    /// <summary>Command to unstage this hunk.</summary>
    public ICommand? UnstageHunkCommand { get; init; }

    /// <summary>Display text for the button: "Stage" or "Unstage".</summary>
    public string ButtonText => IsStaged ? "Unstage" : "Stage";

    /// <summary>Index label: "Hunk 1 of 4", etc.</summary>
    public string HunkLabel { get; init; } = string.Empty;
}
