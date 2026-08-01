namespace GrumpyGit.Core.Models;

/// <summary>
/// A run of lines removed in one place and re-added in another — a move rather than a
/// rewrite.
/// </summary>
/// <param name="FromLine">1-based rendered row where the run was removed.</param>
/// <param name="ToLine">1-based rendered row where the run reappears.</param>
/// <param name="Length">Number of lines in the run.</param>
public sealed record MovedBlock(int FromLine, int ToLine, int Length);
