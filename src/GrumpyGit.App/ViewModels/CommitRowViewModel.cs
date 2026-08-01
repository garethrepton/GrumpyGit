using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using GrumpyGit.Core.Graph;

namespace GrumpyGit.App.ViewModels;

public partial class CommitRowViewModel : ObservableObject
{
    /// <summary>Sentinel hash used for the working-tree / uncommitted-changes row.</summary>
    public const string WorkingTreeHash = "WORKING_TREE";

    public string Hash { get; init; } = string.Empty;
    public string ShortHash => Hash.Length >= 7 ? Hash[..7] : Hash;
    public string Subject { get; init; } = string.Empty;
    public string AuthorName { get; init; } = string.Empty;
    public DateTimeOffset AuthorDate { get; init; }
    public string[] RefNames { get; init; } = [];
    public int Lane { get; init; }

    /// <summary>Graph segments for this row, used by CommitGraphCell to render lane lines.</summary>
    public IReadOnlyList<GraphSegment> Segments { get; init; } = [];

    /// <summary>Total number of active lanes across the whole graph (used to size the graph column).</summary>
    public int TotalLanes { get; init; } = 1;

    public bool IsWorkingTree => Hash == WorkingTreeHash;

    /// <summary>True when this commit has more than one parent (a merge commit).</summary>
    public bool IsMergeCommit { get; init; }

    // ── AI attribution ────────────────────────────────────────────────────────

    /// <summary>Display name of the AI agent that wrote this commit; empty if human-written.</summary>
    public string AiAgentName { get; init; } = string.Empty;

    /// <summary>The raw evidence (trailer/identity) behind the attribution, shown as a tooltip.</summary>
    public string AiEvidenceDetail { get; init; } = string.Empty;

    public bool IsAiAuthored => !string.IsNullOrEmpty(AiAgentName);

    /// <summary>Tooltip explaining why this commit is flagged as AI-authored.</summary>
    public string AiTooltip => IsAiAuthored
        ? $"Written by {AiAgentName}\n{AiEvidenceDetail}"
        : string.Empty;

    public string FormattedDate => IsWorkingTree
        ? string.Empty
        : AuthorDate.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string RefLabel => RefNames.Length > 0 ? string.Join(", ", RefNames) : string.Empty;

    public string DisplayText => IsWorkingTree
        ? Subject   // e.g. "  Working Changes (3 files)"
        : string.IsNullOrEmpty(RefLabel)
            ? $"{ShortHash}  {Subject}"
            : $"{ShortHash}  [{RefLabel}]  {Subject}";
}
