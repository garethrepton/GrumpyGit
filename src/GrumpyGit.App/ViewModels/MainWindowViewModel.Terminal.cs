using System;
using System.IO;
using CommunityToolkit.Mvvm.Input;

namespace GrumpyGit.App.ViewModels;

/// <summary>Something the terminal panel should do, requested from the toolbar.</summary>
public enum TerminalAction
{
    /// <summary>Empty the scrollback, leaving the shell running.</summary>
    Clear,

    /// <summary>Kill the shell and start a fresh one in the current repository.</summary>
    Restart,

    /// <summary>Interrupt whatever command is running (Ctrl+C).</summary>
    Interrupt,

    /// <summary>Put the whole scrollback on the clipboard.</summary>
    CopyAll,
}

// Partial class — the embedded terminal panel.
//
// The shell process itself is owned by the panel, not by this viewmodel: it is bound to a
// visual tree and must die with it, and a viewmodel that outlives the window would keep it
// alive. So the toolbar commands here are requests, raised as an event that the panel
// listens to — the same shape as ScrollToDiffLineRequested, where the viewmodel decides
// *what* should happen and the view owns the thing it happens to.
public partial class MainWindowViewModel
{
    public event EventHandler<TerminalAction>? TerminalActionRequested;

    /// <summary>Absolute path the shell is rooted at, shown in the panel header.</summary>
    public string TerminalCwdLabel =>
        string.IsNullOrEmpty(RepoPath) ? "no repository open" : RepoPath;

    /// <summary>Repository folder name, for the compact header badge.</summary>
    public string TerminalRepoLabel =>
        string.IsNullOrEmpty(RepoPath)
            ? string.Empty
            : Path.GetFileName(RepoPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    /// <summary>
    /// Terminal text size, from Settings. Parsed here rather than in the panel so the
    /// stored string — which is user-editable and can be anything — has exactly one place
    /// where it is validated.
    /// </summary>
    public double TerminalFontSize =>
        double.TryParse(SettingsTerminalFontSize, out var size) && size is >= 6 and <= 40
            ? size
            : 13;

    partial void OnRepoPathChanged(string value)
    {
        OnPropertyChanged(nameof(TerminalCwdLabel));
        OnPropertyChanged(nameof(TerminalRepoLabel));
    }

    partial void OnSettingsTerminalFontSizeChanged(string value)
        => OnPropertyChanged(nameof(TerminalFontSize));

    [RelayCommand]
    private void ClearTerminal() => TerminalActionRequested?.Invoke(this, TerminalAction.Clear);

    [RelayCommand]
    private void RestartTerminal() => TerminalActionRequested?.Invoke(this, TerminalAction.Restart);

    [RelayCommand]
    private void InterruptTerminal() => TerminalActionRequested?.Invoke(this, TerminalAction.Interrupt);

    [RelayCommand]
    private void CopyTerminal() => TerminalActionRequested?.Invoke(this, TerminalAction.CopyAll);
}
