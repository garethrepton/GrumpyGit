using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace GrumpyGit.App.Controls;

/// <summary>
/// The AI reading of a diff, arranged as sections: the model's line about a hunk, then the
/// hunk.
///
/// Deliberately not AvaloniaEdit. The editor is one document with one set of renderers, and
/// a notebook is N independent blocks with prose between them — reproducing that inside a
/// single editor would mean faking block boundaries with padding and drawing the notes as
/// overlays positioned by line number, which is what the callouts in side-by-side mode
/// already do and the reason this view exists at all.
///
/// The cost is honest and worth stating: no syntax highlighting here, because that lives in
/// TextMate inside the editor. This mode is for reading what changed and why, and the other
/// two modes are one click away for reading the code itself.
/// </summary>
public partial class NotebookDiffViewer : UserControl
{
    public NotebookDiffViewer()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
