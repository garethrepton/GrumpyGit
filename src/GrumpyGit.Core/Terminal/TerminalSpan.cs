namespace GrumpyGit.Core.Terminal;

/// <summary>A run of characters sharing one <see cref="TerminalStyle"/>.</summary>
public readonly record struct TerminalSpan(string Text, TerminalStyle Style);
