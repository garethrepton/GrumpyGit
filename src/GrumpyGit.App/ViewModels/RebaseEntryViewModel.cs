using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using GrumpyGit.Core.Models;

namespace GrumpyGit.App.ViewModels;

public partial class RebaseEntryViewModel : ObservableObject
{
    public string Hash { get; init; } = string.Empty;
    public string ShortHash => Hash.Length >= 7 ? Hash[..7] : Hash;
    public string Subject { get; init; } = string.Empty;

    [ObservableProperty] private RebaseActionType _selectedAction = RebaseActionType.Pick;

    public IReadOnlyList<RebaseActionType> AvailableActions { get; } = new[]
    {
        RebaseActionType.Pick,
        RebaseActionType.Reword,
        RebaseActionType.Squash,
        RebaseActionType.Fixup,
        RebaseActionType.Drop,
        RebaseActionType.Edit
    };

    public string DisplayText => $"{ShortHash}  {Subject}";
}
