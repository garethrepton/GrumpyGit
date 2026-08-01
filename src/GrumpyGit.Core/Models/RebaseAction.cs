namespace GrumpyGit.Core.Models;

public enum RebaseActionType
{
    Pick,
    Reword,
    Squash,
    Fixup,
    Drop,
    Edit
}

public record RebaseAction(RebaseActionType Type, string Hash, string Subject);
