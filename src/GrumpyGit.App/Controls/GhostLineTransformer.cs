using System.Collections.Generic;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;

namespace GrumpyGit.App.Controls;

/// <summary>
/// Draws ghost lines — content the change removed — as dimmed and struck through.
///
/// A background tint alone is not enough here. In the single-column view a ghost sits
/// directly above the line that replaced it, so the two must be distinguishable at a
/// glance even mid-scroll: the strike says "this is gone" and the dimming pushes it
/// behind the live code without hiding it. Colour carries none of that meaning on its
/// own, which keeps the view readable in greyscale and for colour-blind readers.
/// </summary>
public sealed class GhostLineTransformer : DocumentColorizingTransformer
{
    private readonly HashSet<int> _ghostLines;
    private readonly IBrush _foreground;

    public GhostLineTransformer(IEnumerable<int> ghostLines, IBrush foreground)
    {
        _ghostLines = new HashSet<int>(ghostLines);
        _foreground = foreground;
    }

    protected override void ColorizeLine(DocumentLine line)
    {
        if (!_ghostLines.Contains(line.LineNumber)) return;

        // An empty line has no character range to colour, and asking for one throws.
        if (line.Length == 0) return;

        ChangeLinePart(line.Offset, line.EndOffset, element =>
        {
            element.TextRunProperties.SetForegroundBrush(_foreground);
            element.TextRunProperties.SetTextDecorations(TextDecorations.Strikethrough);
        });
    }
}
