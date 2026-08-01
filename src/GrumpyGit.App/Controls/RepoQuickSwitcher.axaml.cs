using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using GrumpyGit.App.ViewModels;

namespace GrumpyGit.App.Controls;

/// <summary>
/// Ctrl+P repository palette.
///
/// Keyboard handling lives here rather than in the window so the arrow keys move the
/// result selection while the caret stays in the search box — the behaviour every
/// command palette has, and the reason this is a control rather than plain markup.
/// </summary>
public partial class RepoQuickSwitcher : UserControl
{
    public RepoQuickSwitcher()
    {
        InitializeComponent();

        AddHandler(KeyDownEvent, OnPaletteKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>Focus the input whenever the palette is shown, so it is type-ready.</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsVisibleProperty && change.GetNewValue<bool>())
            FocusInput();
    }

    private void FocusInput() =>
        Dispatcher.UIThread.Post(() =>
        {
            var box = this.FindControl<TextBox>("QuickSwitchInput");
            box?.Focus();
            box?.SelectAll();
        }, DispatcherPriority.Input);

    private void OnPaletteKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        switch (e.Key)
        {
            case Key.Down:
                vm.MoveQuickSwitchSelectionCommand.Execute("down");
                e.Handled = true;
                break;

            case Key.Up:
                vm.MoveQuickSwitchSelectionCommand.Execute("up");
                e.Handled = true;
                break;

            case Key.Enter:
                vm.ActivateQuickSwitchEntryCommand.Execute(vm.SelectedQuickSwitchEntry);
                e.Handled = true;
                break;

            case Key.Escape:
                vm.CloseQuickSwitchCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }
}
