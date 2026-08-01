using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace GrumpyGit.App.Controls;

public enum ToastSeverity { Info, Success, Warning, Error }

public partial class ToastNotification : UserControl
{
    private CancellationTokenSource? _autoDismissCts;

    public event EventHandler? Dismissed;

    public ToastNotification()
    {
        InitializeComponent();
    }

    public void Show(string message, ToastSeverity severity, int autoCloseMs = 4000)
    {
        var messageText = this.FindControl<TextBlock>("MessageText")!;
        var severityIcon = this.FindControl<Avalonia.Controls.Shapes.Path>("SeverityIcon")!;
        var severityRail = this.FindControl<Border>("SeverityRail")!;
        var border = this.FindControl<Border>("ToastBorder")!;

        messageText.Text = message;

        // Icon geometry and colour both come from the design tokens, so a toast
        // is styled by the same ramp as the rest of the app rather than by a
        // per-severity hex picked here.
        (string iconKey, string fgKey, string borderKey) = severity switch
        {
            ToastSeverity.Success => ("IconCheckCircle", "AddFgBrush", "AddBorderBrush"),
            ToastSeverity.Error   => ("IconXCircle", "DangerFgBrush", "DangerBorderBrush"),
            ToastSeverity.Warning => ("IconAlert", "WarnFgBrush", "WarnBorderBrush"),
            _                     => ("IconInfo", "InfoFgBrush", "AccentBorderBrush"),
        };

        var accent = ThemeTokens.Brush(fgKey, Brushes.Gray);
        severityIcon.Data = ThemeTokens.Icon(iconKey);
        severityIcon.Stroke = accent;
        severityRail.Background = accent;
        border.BorderBrush = ThemeTokens.Brush(borderKey, Brushes.Gray);

        // Fade in
        border.Opacity = 0;
        var fadeIn = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(200),
            Easing = new CubicEaseOut(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 0.0) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 1.0) } }
            }
        };
        fadeIn.RunAsync(border);

        // Auto-dismiss
        _autoDismissCts?.Cancel();
        _autoDismissCts = new CancellationTokenSource();
        var token = _autoDismissCts.Token;

        _ = Task.Delay(autoCloseMs, token).ContinueWith(_ =>
        {
            if (!token.IsCancellationRequested)
                Dispatcher.UIThread.Post(() => Dismiss());
        }, TaskScheduler.Default);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Dismiss();

    private async void Dismiss()
    {
        _autoDismissCts?.Cancel();

        var border = this.FindControl<Border>("ToastBorder");
        if (border != null)
        {
            var fadeOut = new Animation
            {
                Duration = TimeSpan.FromMilliseconds(150),
                Easing = new CubicEaseIn(),
                FillMode = FillMode.Forward,
                Children =
                {
                    new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 1.0) } },
                    new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 0.0) } }
                }
            };
            await fadeOut.RunAsync(border);
        }

        Dismissed?.Invoke(this, EventArgs.Empty);
    }
}
