using Avalonia.Controls;

namespace GrumpyGit.App.Controls;

public partial class ToastHost : UserControl
{
    public ToastHost()
    {
        InitializeComponent();
    }

    public void ShowToast(string message, ToastSeverity severity = ToastSeverity.Info, int autoCloseMs = 4000)
    {
        var stack = this.FindControl<StackPanel>("ToastStack")!;
        var toast = new ToastNotification();
        stack.Children.Add(toast);
        toast.Dismissed += (_, _) => stack.Children.Remove(toast);
        toast.Show(message, severity, autoCloseMs);
    }
}
