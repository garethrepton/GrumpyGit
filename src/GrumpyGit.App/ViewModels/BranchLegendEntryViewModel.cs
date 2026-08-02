using CommunityToolkit.Mvvm.ComponentModel;

namespace GrumpyGit.App.ViewModels;

/// <summary>
/// One row in the graph's key: a branch, the colour it is drawn in, and whether it is
/// currently shown.
/// </summary>
public partial class BranchLegendEntryViewModel : ObservableObject
{
    public required string Name { get; init; }

    /// <summary>Palette slot, shared with the graph so the swatch always matches.</summary>
    public int ColorSlot { get; init; }

    /// <summary>Commits attributed to this branch in the unfiltered history.</summary>
    public int CommitCount { get; init; }

    /// <summary>Unticking hides this branch's commits from both graph and list.</summary>
    [ObservableProperty] private bool _isVisible = true;

    /// <summary>The branch currently isolated by branch mode.</summary>
    [ObservableProperty] private bool _isBranchModeTarget;
}
