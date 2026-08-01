using Avalonia.Controls;

namespace GrumpyGit.App.Controls;

/// <summary>
/// Before/after preview for image files. Presentation only — the bitmaps and the
/// change summary come from <see cref="ViewModels.ImageDiffViewModel"/>.
/// </summary>
public partial class ImageDiffViewer : UserControl
{
    public ImageDiffViewer()
    {
        InitializeComponent();
    }
}
