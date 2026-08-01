using CommunityToolkit.Mvvm.ComponentModel;

namespace GrumpyGit.App.ViewModels;

/// <summary>
/// Represents a single open repository tab.
/// </summary>
public partial class RepoTabViewModel : ObservableObject
{
    [ObservableProperty] private string _path = string.Empty;
    [ObservableProperty] private bool _isActive;

    /// <summary>Short display name — last folder name.</summary>
    public string DisplayName => string.IsNullOrEmpty(Path)
        ? "New Tab"
        : System.IO.Path.GetFileName(Path.TrimEnd(
            System.IO.Path.DirectorySeparatorChar,
            System.IO.Path.AltDirectorySeparatorChar));
}
