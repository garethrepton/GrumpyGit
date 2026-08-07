using Avalonia.Controls;
using GrumpyGit.App.ViewModels;

namespace GrumpyGit.App.Controls;

public partial class PullRequestPanel : UserControl
{
    public PullRequestPanel()
    {
        InitializeComponent();

        // The clipboard hangs off the TopLevel, which a viewmodel has no business
        // reaching for. The viewmodel produces the text; this puts it somewhere.
        var copyButton = this.FindControl<Button>("CopySummaryButton");
        if (copyButton is not null)
            copyButton.Click += async (_, _) => await CopySummaryAsync();
    }

    private async System.Threading.Tasks.Task CopySummaryAsync()
    {
        if (DataContext is not MainWindowViewModel vm) return;

        var summary = vm.BuildPullRequestSummary();
        if (string.IsNullOrEmpty(summary)) return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            vm.StatusMessage = "No clipboard available";
            return;
        }

        await clipboard.SetTextAsync(summary);
        vm.StatusMessage = "Review summary copied to the clipboard";
    }
}
