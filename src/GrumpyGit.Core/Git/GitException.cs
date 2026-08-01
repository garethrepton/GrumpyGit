namespace GrumpyGit.Core.Git;

public class GitException : Exception
{
    public int ExitCode { get; }
    public string GitOutput { get; }

    public GitException(string message, int exitCode, string gitOutput, Exception? innerException = null)
        : base(message, innerException)
    {
        ExitCode = exitCode;
        GitOutput = gitOutput;
    }
}
