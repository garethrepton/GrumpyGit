using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace GrumpyGit.App.Controls;

public partial class KeyboardShortcutsPanel : UserControl
{
    public event EventHandler? CloseRequested;

    public KeyboardShortcutsPanel()
    {
        InitializeComponent();
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
