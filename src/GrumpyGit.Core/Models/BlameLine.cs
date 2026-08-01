namespace GrumpyGit.Core.Models;

public record BlameLine(int LineNumber, string Text, string CommitHash, string AuthorName, DateTimeOffset AuthorDate, string Summary);
