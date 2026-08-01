using Avalonia.Controls;

namespace GrumpyGit.App.Controls;

/// <summary>
/// AI session review surface. All behaviour lives in
/// <see cref="ViewModels.MainWindowViewModel"/>; this is presentation only.
/// </summary>
public partial class AiReviewPanel : UserControl
{
    public AiReviewPanel()
    {
        InitializeComponent();
    }
}
