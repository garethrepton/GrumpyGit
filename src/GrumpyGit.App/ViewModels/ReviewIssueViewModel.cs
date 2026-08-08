using CommunityToolkit.Mvvm.ComponentModel;
using GrumpyGit.Core.LocalModel;

namespace GrumpyGit.App.ViewModels;

/// <summary>
/// One problem the local model reported, as a row in the review panel.
///
/// Thin by design — the record underneath is the truth, and this exists only to give the
/// row a label and a jump target, the same shape as <see cref="SymbolChangeViewModel"/>.
/// </summary>
public partial class ReviewIssueViewModel : ObservableObject
{
    public ReviewIssue Model { get; init; } = null!;

    /// <summary>Line badge, or a dash when the model cited a line the diff never showed.</summary>
    public string LineLabel => Model.IsAnchored ? Model.SourceLine.ToString() : "—";

    public string Text => Model.Text;

    /// <summary>Only an anchored issue can be jumped to, so only that one looks clickable.</summary>
    public bool CanNavigate => Model.IsAnchored;
}
