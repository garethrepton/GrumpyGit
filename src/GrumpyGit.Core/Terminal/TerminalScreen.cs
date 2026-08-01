using System;
using System.Collections.Generic;

namespace GrumpyGit.Core.Terminal;

/// <summary>
/// A line-oriented VT interpreter: feed it the raw bytes coming out of a shell and it
/// maintains the scrollback the UI draws.
///
/// <para>
/// This is deliberately *not* a full VT100 screen. A real emulator keeps a fixed grid and
/// lets the cursor roam it in two dimensions; here the model is an append-only list of
/// finished rows plus one row still being edited. That covers everything a shell actually
/// does to a scrolling transcript — write text, return to column 0 and rewrite it, erase to
/// end of line, jump to an absolute column — which is exactly the vocabulary PSReadLine
/// uses to redraw the prompt as you type. Sequences that move between rows are parsed and
/// ignored rather than mis-applied, on the grounds that a full-screen program (vim, less)
/// belongs in a real terminal, not in a git client's output pane.
/// </para>
/// <para>
/// The parser is a resumable state machine, because output arrives in arbitrary chunks and
/// an escape sequence is very likely to be split across two reads. State persists across
/// <see cref="Write"/> calls; a half-received <c>ESC[3</c> simply waits for its final byte.
/// </para>
/// <para>
/// Not thread-safe. Drive it from a single thread — in the app that is the UI thread, which
/// also removes any tearing between the reader producing output and the renderer drawing it.
/// </para>
/// </summary>
public sealed class TerminalScreen
{
    /// <summary>Rows kept before the oldest are discarded.</summary>
    public const int DefaultMaxScrollbackLines = 5000;

    // Trimming one row per row once at capacity would memmove the whole list on every
    // newline. Overshooting by a batch and then trimming in one go makes that amortised.
    private const int TrimBatchLines = 256;

    private const int TabStop = 8;

    private readonly List<TerminalLine> _lines = new();
    private readonly List<Cell> _cells = new();

    private TerminalStyle _style = TerminalStyle.Default;
    private int _cursor;

    /// <summary>
    /// Index into <see cref="_lines"/> of the row being edited. Usually the last row, but
    /// not always: PSReadLine redraws a wrapped or recalled command by moving UP and
    /// rewriting, so the cursor has to be able to leave the bottom. Treating every write
    /// as an append — which is what this class did originally — turns each of those
    /// redraws into duplicated text rather than a correction, which is the single largest
    /// source of a garbled prompt.
    /// </summary>
    private int _row;

    // Resumable escape-sequence state.
    private ParseState _state = ParseState.Text;
    private readonly List<char> _sequence = new();

    public TerminalScreen(int maxScrollbackLines = DefaultMaxScrollbackLines)
    {
        MaxScrollbackLines = Math.Max(1, maxScrollbackLines);
        _lines.Add(TerminalLine.Empty);
    }

    public int MaxScrollbackLines { get; }

    /// <summary>
    /// Every row, oldest first. The last entry is the row currently being written and is
    /// replaced with a fresh instance on each <see cref="Write"/>; earlier entries never
    /// change once produced.
    /// </summary>
    public IReadOnlyList<TerminalLine> Lines => _lines;

    /// <summary>Column the cursor sits at within the last row.</summary>
    public int CursorColumn => _cursor;

    /// <summary>
    /// How many rows have ever been discarded, by scrollback trimming or by a clear.
    /// Monotonic, so a consumer that mirrors <see cref="Lines"/> by index can work out
    /// what disappeared without diffing.
    /// </summary>
    public long DroppedLineCount { get; private set; }

    /// <summary>Seeds the active style. Only useful before any output has been written.</summary>
    public void SetStyle(TerminalStyle style) => _style = style;

    /// <summary>Drops all scrollback and returns to a single empty row.</summary>
    public void Clear()
    {
        DroppedLineCount += _lines.Count;
        _lines.Clear();
        _cells.Clear();
        _cursor = 0;
        _row = 0;
        _lines.Add(TerminalLine.Empty);
    }

    /// <summary>Consumes a chunk of terminal output. Chunk boundaries are not significant.</summary>
    public void Write(string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return;

        foreach (var c in chunk)
            Consume(c);

        // The in-progress row is only materialised once per chunk rather than per
        // character: a 4 KB read can touch the same row thousands of times, and each
        // materialisation allocates a line plus its spans.
        _lines[_row] = Materialise();
    }

    /// <summary>The whole scrollback as plain text, for clipboard copy.</summary>
    public string GetText()
    {
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < _lines.Count; i++)
        {
            if (i > 0) builder.Append('\n');
            builder.Append(_lines[i].Text);
        }
        return builder.ToString();
    }

    // ── Parser ────────────────────────────────────────────────────────────────

    private enum ParseState
    {
        Text,
        Escape,
        Csi,
        /// <summary>Operating System Command — window titles and the like. Skipped.</summary>
        Osc,
        /// <summary>Saw ESC inside a string sequence; a following '\' ends it (ST).</summary>
        StringEscape,
        /// <summary>DCS / SOS / PM / APC payloads. Skipped.</summary>
        StringSequence,
        /// <summary>Character-set designator; consumes exactly one more byte.</summary>
        Charset,
    }

    private void Consume(char c)
    {
        switch (_state)
        {
            case ParseState.Text:
                ConsumeText(c);
                return;

            case ParseState.Escape:
                ConsumeEscape(c);
                return;

            case ParseState.Csi:
                // Parameter and intermediate bytes accumulate; the first byte in 0x40-0x7E
                // is the final byte that identifies the command.
                if (c >= '\x20' && c <= '\x3F')
                {
                    _sequence.Add(c);
                    return;
                }
                if (c >= '\x40' && c <= '\x7E')
                {
                    DispatchCsi(c);
                    _state = ParseState.Text;
                    return;
                }
                // Anything else aborts the sequence rather than swallowing the rest of the
                // stream — a stray control byte mid-CSI means the sequence was malformed.
                _state = ParseState.Text;
                Consume(c);
                return;

            case ParseState.Osc:
                if (c == '\a') { _state = ParseState.Text; return; }
                if (c == '\x1B') { _state = ParseState.StringEscape; return; }
                return;

            case ParseState.StringSequence:
                if (c == '\x1B') { _state = ParseState.StringEscape; return; }
                return;

            case ParseState.StringEscape:
                // ESC '\' is the String Terminator. Anything else was a literal ESC inside
                // the payload, so stay in the string.
                _state = c == '\\' ? ParseState.Text : ParseState.Osc;
                return;

            case ParseState.Charset:
                _state = ParseState.Text;
                return;
        }
    }

    private void ConsumeText(char c)
    {
        switch (c)
        {
            case '\x1B':
                _sequence.Clear();
                _state = ParseState.Escape;
                return;

            case '\n':
                NewLine();
                return;

            case '\r':
                _cursor = 0;
                return;

            case '\b':
                if (_cursor > 0) _cursor--;
                return;

            case '\t':
                // Tabs are expanded on the way in. Keeping a literal tab would make the
                // renderer's column arithmetic — which is what keeps a redrawn prompt
                // aligned — depend on tab stops it cannot see.
                var next = (_cursor / TabStop + 1) * TabStop;
                while (_cursor < next) Put(' ');
                return;

            case '\a':
            case '\v':
            case '\f':
            case '\x7F':
                return;

            default:
                if (c < ' ') return;
                Put(c);
                return;
        }
    }

    private void ConsumeEscape(char c)
    {
        switch (c)
        {
            case '[':
                _sequence.Clear();
                _state = ParseState.Csi;
                return;

            case ']':
                _state = ParseState.Osc;
                return;

            case 'P':   // DCS
            case 'X':   // SOS
            case '^':   // PM
            case '_':   // APC
                _state = ParseState.StringSequence;
                return;

            case '(':
            case ')':
            case '*':
            case '+':
                _state = ParseState.Charset;
                return;

            case 'c':
                Clear();
                _style = TerminalStyle.Default;
                _state = ParseState.Text;
                return;

            case 'E':   // NEL — next line
                NewLine();
                _state = ParseState.Text;
                return;

            default:
                // Keypad modes, save/restore cursor, index/reverse-index: all row-level or
                // irrelevant to a scrolling transcript.
                _state = ParseState.Text;
                return;
        }
    }

    private void DispatchCsi(char final)
    {
        var parameters = new string(_sequence.ToArray());
        _sequence.Clear();

        // Private sequences (ESC[?…) are mode toggles — cursor visibility, bracketed
        // paste, alternate screen. None of them change what text is on the row.
        if (parameters.Length > 0 && (parameters[0] == '?' || parameters[0] == '<'
                                      || parameters[0] == '>' || parameters[0] == '='))
            return;

        switch (final)
        {
            case 'm':
                _style = AnsiSgrParser.Apply(_style, parameters);
                return;

            case 'K':   // EL — erase in line
                EraseInLine(FirstParameter(parameters, 0));
                return;

            case 'J':   // ED — erase in display
                EraseInDisplay(FirstParameter(parameters, 0));
                return;

            case 'G':   // CHA — cursor horizontal absolute (1-based)
            case '`':   // HPA — same thing
                MoveCursorTo(FirstParameter(parameters, 1) - 1);
                return;

            case 'H':   // CUP / HVP. Only the column is actionable here; the row is ignored
            case 'f':   // rather than guessed at, since there is no fixed grid to move in.
                MoveCursorTo(SecondParameter(parameters, 1) - 1);
                return;

            case 'C':   // CUF — cursor forward
                MoveCursorTo(_cursor + Math.Max(1, FirstParameter(parameters, 1)));
                return;

            case 'D':   // CUB — cursor back
                MoveCursorTo(_cursor - Math.Max(1, FirstParameter(parameters, 1)));
                return;

            case 'X':   // ECH — erase n characters at the cursor, without moving it
                EraseCharacters(Math.Max(1, FirstParameter(parameters, 1)));
                return;

            case 'P':   // DCH — delete n characters, pulling the rest of the row left
                DeleteCharacters(Math.Max(1, FirstParameter(parameters, 1)));
                return;

            case '@':   // ICH — insert n blanks at the cursor
                InsertBlanks(Math.Max(1, FirstParameter(parameters, 1)));
                return;

            case 'A':   // CUU — cursor up
                MoveToRow(_row - Math.Max(1, FirstParameter(parameters, 1)));
                return;

            case 'B':   // CUD — cursor down
                MoveToRow(_row + Math.Max(1, FirstParameter(parameters, 1)));
                return;

            case 'E':   // CNL — down n rows, to column 0
                MoveToRow(_row + Math.Max(1, FirstParameter(parameters, 1)));
                MoveCursorTo(0);
                return;

            case 'F':   // CPL — up n rows, to column 0
                MoveToRow(_row - Math.Max(1, FirstParameter(parameters, 1)));
                MoveCursorTo(0);
                return;

            // Scroll regions and insert/delete-line belong to a fixed-height grid this
            // class deliberately does not model, so they stay ignored.
        }
    }

    /// <summary>
    /// Moves the edit cursor to another row, materialising the row being left and loading
    /// the target back into the cell buffer so it can be overwritten in place.
    ///
    /// Rows below the bottom are created: a shell that moves down past the end expects
    /// blank rows to exist there, and refusing to create them would silently drop the
    /// output that follows.
    /// </summary>
    private void MoveToRow(int row)
    {
        if (row < 0) row = 0;

        _lines[_row] = Materialise();

        while (row >= _lines.Count)
        {
            _lines.Add(TerminalLine.Empty);
            TrimScrollback();
        }

        _row = Math.Min(row, _lines.Count - 1);
        LoadCells(_lines[_row]);
    }

    /// <summary>
    /// Expands a materialised row back into editable cells. The inverse of
    /// <see cref="Materialise"/>, needed because a redraw reopens a row that was already
    /// finished.
    /// </summary>
    private void LoadCells(TerminalLine line)
    {
        _cells.Clear();
        foreach (var span in line.Spans)
        {
            foreach (var c in span.Text)
                _cells.Add(new Cell(c, span.Style));
        }

        if (_cursor > _cells.Count) PadTo(_cursor);
    }

    private static int FirstParameter(string parameters, int fallback)
    {
        var separator = parameters.IndexOf(';');
        var slice = separator < 0 ? parameters.AsSpan() : parameters.AsSpan(0, separator);
        return slice.IsEmpty || !int.TryParse(slice, out var value) ? fallback : value;
    }

    private static int SecondParameter(string parameters, int fallback)
    {
        var separator = parameters.IndexOf(';');
        if (separator < 0) return fallback;
        var slice = parameters.AsSpan(separator + 1);
        var end = slice.IndexOf(';');
        if (end >= 0) slice = slice[..end];
        return slice.IsEmpty || !int.TryParse(slice, out var value) ? fallback : value;
    }

    // ── Row editing ───────────────────────────────────────────────────────────

    private readonly record struct Cell(char Character, TerminalStyle Style);

    private void Put(char c)
    {
        PadTo(_cursor);
        if (_cursor < _cells.Count)
            _cells[_cursor] = new Cell(c, _style);
        else
            _cells.Add(new Cell(c, _style));
        _cursor++;
    }

    /// <summary>
    /// Grows the row with blanks so the cursor can sit past the current end. Padding is
    /// deliberately unstyled: a cursor jump over empty space must not paint the gap with
    /// whatever background happened to be active.
    /// </summary>
    private void PadTo(int column)
    {
        while (_cells.Count < column)
            _cells.Add(new Cell(' ', TerminalStyle.Default));
    }

    private void MoveCursorTo(int column)
    {
        _cursor = Math.Max(0, column);
        PadTo(_cursor);
    }

    private void NewLine()
    {
        _lines[_row] = Materialise();
        _cursor = 0;

        // Move DOWN rather than append. After a redraw has walked the cursor up, the rows
        // beneath it still exist and belong to the same command; appending here would push
        // a duplicate copy below the original instead of stepping back onto it.
        if (_row < _lines.Count - 1)
        {
            _row++;
            LoadCells(_lines[_row]);
            return;
        }

        _cells.Clear();
        _lines.Add(TerminalLine.Empty);
        _row = _lines.Count - 1;
        TrimScrollback();
    }

    private void TrimScrollback()
    {
        if (_lines.Count <= MaxScrollbackLines + TrimBatchLines) return;
        var excess = _lines.Count - MaxScrollbackLines;
        _lines.RemoveRange(0, excess);
        DroppedLineCount += excess;

        // The row cursor is an absolute index, so dropping rows from the front slides it.
        // Without this the cursor would point at whatever text happened to shift into its
        // old slot and the next redraw would overwrite unrelated scrollback.
        _row = Math.Max(0, _row - excess);
    }

    private void EraseInLine(int mode)
    {
        switch (mode)
        {
            case 0:     // cursor to end of row
                if (_cursor < _cells.Count)
                    _cells.RemoveRange(_cursor, _cells.Count - _cursor);
                return;
            case 1:     // start of row through the cursor, inclusive
                for (var i = 0; i <= _cursor && i < _cells.Count; i++)
                    _cells[i] = new Cell(' ', TerminalStyle.Default);
                return;
            case 2:
                _cells.Clear();
                return;
        }
    }

    private void EraseInDisplay(int mode)
    {
        switch (mode)
        {
            case 0:
                // Cursor to end of display. Dropping the rows below matters: PSReadLine
                // erases downward before rewriting a command that shrank, and leaving them
                // in place strands the tail of the previous, longer version on screen.
                EraseInLine(0);
                if (_row < _lines.Count - 1)
                    _lines.RemoveRange(_row + 1, _lines.Count - _row - 1);
                return;

            case 1:
                EraseInLine(1);
                for (var i = 0; i < _row; i++)
                    _lines[i] = TerminalLine.Empty;
                return;

            case 2:
            case 3:
                Clear();
                return;
        }
    }

    private void EraseCharacters(int count)
    {
        for (var i = _cursor; i < _cursor + count && i < _cells.Count; i++)
            _cells[i] = new Cell(' ', TerminalStyle.Default);
    }

    private void DeleteCharacters(int count)
    {
        if (_cursor >= _cells.Count) return;
        _cells.RemoveRange(_cursor, Math.Min(count, _cells.Count - _cursor));
    }

    private void InsertBlanks(int count)
    {
        PadTo(_cursor);
        for (var i = 0; i < count; i++)
            _cells.Insert(_cursor, new Cell(' ', TerminalStyle.Default));
    }

    /// <summary>
    /// Coalesces the current cell buffer into styled spans.
    ///
    /// Trailing unstyled blanks are dropped so that cursor padding and
    /// erase-to-end-of-line do not leave the renderer measuring — and the clipboard
    /// carrying — a tail of spaces. Blanks with a background colour are kept, because
    /// there they are the visible artefact.
    /// </summary>
    private TerminalLine Materialise()
    {
        var end = _cells.Count;
        while (end > 0 && _cells[end - 1].Character == ' '
                       && _cells[end - 1].Style == TerminalStyle.Default)
            end--;

        if (end == 0) return TerminalLine.Empty;

        var spans = new List<TerminalSpan>();
        var buffer = new char[end];
        var runStart = 0;

        for (var i = 0; i < end; i++)
            buffer[i] = _cells[i].Character;

        for (var i = 1; i <= end; i++)
        {
            if (i < end && _cells[i].Style == _cells[runStart].Style)
                continue;

            spans.Add(new TerminalSpan(new string(buffer, runStart, i - runStart), _cells[runStart].Style));
            runStart = i;
        }

        return new TerminalLine(spans);
    }
}
