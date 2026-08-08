using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using GrumpyGit.Core.LocalModel;
using GrumpyGit.Core.Models;

namespace GrumpyGit.App.ViewModels;

/// <summary>
/// One line inside a notebook cell.
///
/// A plain object rather than the <see cref="DiffLine"/> it comes from, because the view
/// needs booleans to switch style classes on and a <c>Type</c> enum cannot do that in a
/// binding without a converter per state.
/// </summary>
public sealed class NotebookLineViewModel
{
    public required string Content { get; init; }

    /// <summary>Blank rather than 0 for a removed line, which has no place in the new file.</summary>
    public required string Number { get; init; }

    public required string Marker { get; init; }

    public bool IsAdded { get; init; }
    public bool IsRemoved { get; init; }

    /// <summary>An issue was anchored here — drawn with a wash, the way the editor does it.</summary>
    public bool IsFlagged { get; init; }
}

/// <summary>
/// One section of the AI diff view: what the model said about a single change, then that
/// change — nothing else. Ten changes in a file make ten of these.
/// </summary>
public sealed partial class NotebookCellViewModel : ObservableObject
{
    public required ReviewedChange Change { get; init; }
    public required IReadOnlyList<NotebookLineViewModel> Lines { get; init; }

    /// <summary>
    /// True while the model is still working on this file.
    ///
    /// Without it every section reads "No reading for this change" for the minute or two
    /// the review takes, which is a statement that the model looked and had nothing to say
    /// — the opposite of the truth, and indistinguishable from the finished state.
    /// </summary>
    [ObservableProperty] private bool _isReviewRunning;

    partial void OnIsReviewRunningChanged(bool value) => OnPropertyChanged(nameof(NoteOrPlaceholder));

    public int Number => Change.Number;
    public string HeaderLine => Change.HeaderLine;
    public bool HasNote => Change.HasNote;
    public bool HasIssues => Change.HasIssues;

    public string Title => $"Change {Change.Number}";

    public string ChurnLabel => $"+{Change.Added} −{Change.Removed}";

    /// <summary>The badge beside the heading: FILES, NETWORK, PROCESS, CREDENTIALS, DATA LOSS.</summary>
    public bool HasConcern => Change.HasConcern;

    public string ConcernLabel => Change.Concern?.Label ?? string.Empty;

    /// <summary>Credentials and data loss are drawn in the danger colour; the rest warn.</summary>
    public bool IsSevereConcern => Change.Concern?.IsSevere == true;

    /// <summary>What it reaches, in the model's words, beside the category.</summary>
    public string ConcernDetail => Change.Concern?.Text ?? string.Empty;

    public bool HasConcernDetail => ConcernDetail.Length > 0;

    /// <summary>
    /// What the model said, or why there is nothing there yet. Three states, because they
    /// mean different things and a single blank line would conflate all of them: still
    /// working, finished with nothing to say, and never asked.
    /// </summary>
    public string NoteOrPlaceholder => this switch
    {
        { HasNote: true } => Change.Note,
        { IsReviewRunning: true } => "reading…",
        { Change.WasSentToModel: false } => "Not shown to the model — this file is past the review budget.",
        _ => "No reading for this change.",
    };

    public string IssueText => string.Join("\n", Change.Issues.Select(i =>
        i.IsAnchored ? $"line {i.SourceLine}: {i.Text}" : i.Text));

    /// <summary>
    /// Builds every section for a diff. Lines are projected here rather than in
    /// <see cref="DiffNotebook"/> so that transform stays free of view concerns.
    /// </summary>
    public static IReadOnlyList<NotebookCellViewModel> Build(
        ParsedDiff? diff,
        IReadOnlyList<ChangeNote>? notes,
        IReadOnlyList<ReviewIssue>? issues,
        IReadOnlyList<ChangeConcern>? concerns = null)
    {
        var flagged = (issues ?? [])
            .Where(i => i.IsAnchored)
            .Select(i => i.RenderedLine)
            .ToHashSet();

        return DiffNotebook.Build(diff, notes, issues, concerns)
            .Select(change => new NotebookCellViewModel
            {
                Change = change,
                Lines = change.Lines.Select(line => new NotebookLineViewModel
                {
                    Content = line.Content,
                    Number = line.NewLineNumber > 0
                        ? line.NewLineNumber.ToString()
                        : string.Empty,
                    Marker = line.Type == DiffLineType.Added ? "+" : "−",
                    IsAdded = line.Type == DiffLineType.Added,
                    IsRemoved = line.Type == DiffLineType.Removed,
                    IsFlagged = line.RenderedLineNumber > 0 && flagged.Contains(line.RenderedLineNumber),
                }).ToList(),
            })
            .ToList();
    }
}
