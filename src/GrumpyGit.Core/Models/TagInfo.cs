namespace GrumpyGit.Core.Models;

public record TagInfo(string Name, string ShortHash, DateTimeOffset CreatedDate, string Message);
