namespace GrumpyGit.Core.Graph;

/// <summary>
/// Assigns each branch a colour slot.
///
/// The graph used to colour by lane index, which is fine for telling adjacent lines
/// apart but useless as a key: a branch's lane changes as other branches open and close,
/// so the same branch would be several colours down the same graph. Colouring by branch
/// instead makes the key mean something — one colour, one branch, top to bottom.
/// </summary>
public static class BranchPalette
{
    /// <summary>Number of distinct colour slots. Matches the Lane0..Lane7 theme tokens.</summary>
    public const int Size = 8;

    /// <summary>
    /// Maps branch label to colour slot, assigned in order of first appearance.
    ///
    /// Order of appearance rather than a hash of the name: it guarantees that the first
    /// eight branches — the ones actually on screen near the top — are all different,
    /// where a hash would happily collide two of them. It is stable for a given history,
    /// which is what matters; it is not stable across repositories, which does not.
    /// </summary>
    public static IReadOnlyDictionary<string, int> Assign(IEnumerable<string?> labelsInOrder)
    {
        var slots = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var label in labelsInOrder)
        {
            if (string.IsNullOrEmpty(label)) continue;
            if (slots.ContainsKey(label)) continue;

            slots[label] = slots.Count % Size;
        }

        return slots;
    }
}
