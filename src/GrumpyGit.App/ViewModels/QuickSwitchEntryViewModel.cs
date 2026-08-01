using System;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace GrumpyGit.App.ViewModels;

/// <summary>
/// One row in the repository quick switcher.
/// </summary>
public partial class QuickSwitchEntryViewModel : ObservableObject
{
    public required string Path { get; init; }

    /// <summary>True when this repo is already an open tab, so it can be labelled as such.</summary>
    public bool IsOpen { get; init; }

    public bool IsActive { get; init; }

    public string DisplayName
    {
        get
        {
            var trimmed = Path.TrimEnd(
                System.IO.Path.DirectorySeparatorChar,
                System.IO.Path.AltDirectorySeparatorChar);
            var name = System.IO.Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? trimmed : name;
        }
    }

    /// <summary>
    /// Parent directory, shown dimmed beside the name so two repos with the same folder
    /// name stay distinguishable — which is common with worktrees and forks.
    /// </summary>
    public string ParentPath
    {
        get
        {
            try
            {
                var parent = Directory.GetParent(Path.TrimEnd(
                    System.IO.Path.DirectorySeparatorChar,
                    System.IO.Path.AltDirectorySeparatorChar));
                return parent?.FullName ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    public string StatusLabel => IsActive ? "current" : IsOpen ? "open" : string.Empty;

    public bool HasStatusLabel => !string.IsNullOrEmpty(StatusLabel);
}
