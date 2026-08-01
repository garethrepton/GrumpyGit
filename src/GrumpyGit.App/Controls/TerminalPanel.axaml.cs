using System;
using System.ComponentModel;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GrumpyGit.App.ViewModels;
using GrumpyGit.Core.Terminal;

namespace GrumpyGit.App.Controls;

/// <summary>
/// The docked terminal: a real shell rooted at the open repository, its output rendered
/// with ANSI colour, and keystrokes forwarded straight to it.
///
/// <para>
/// The panel owns the shell process rather than the viewmodel, because the process must
/// not outlive the visual tree — detaching from the tree is the one signal that reliably
/// arrives whether the panel was closed, the window shut, or the app torn down. The
/// viewmodel contributes the working directory, the font size and the toolbar commands,
/// which it raises as requests through <see cref="MainWindowViewModel.TerminalActionRequested"/>.
/// </para>
/// <para>
/// Input is forwarded key by key rather than collected in a text box and sent on Enter.
/// That is the difference between a terminal and a command runner: tab completion, Ctrl+R
/// history search and command-line editing all live in the shell, and they only work if the
/// shell sees each keystroke. It also means the shell's own echo is the single source of
/// truth for what is on screen, so there is no local buffer to drift out of sync with it.
/// </para>
/// </summary>
public partial class TerminalPanel : UserControl
{
    private readonly TerminalOutputView _output = new();
    private readonly TerminalScreen _screen = new();

    // Output arrives on the reader thread in whatever sizes the pipe hands over. It is
    // accumulated here and drained once per dispatcher turn: a build spewing thousands of
    // lines would otherwise re-render the panel once per read.
    private readonly StringBuilder _pending = new();
    private readonly object _pendingLock = new();
    private bool _flushScheduled;

    private MainWindowViewModel? _viewModel;
    private TerminalSession? _session;
    private string? _sessionRepoPath;

    private bool _attached;
    private bool _stickToBottom = true;
    private bool _scrollScheduled;
    private int _lastColumns;
    private int _lastRows;

    public TerminalPanel()
    {
        InitializeComponent();

        OutputScroller.Content = _output;
        OutputScroller.ScrollChanged += OnScrollChanged;

        _output.KeyDown += OnOutputKeyDown;
        _output.TextInput += OnOutputTextInput;

        // Clicking anywhere in the pane should give the shell the keyboard, the same way
        // clicking a terminal window does.
        AddHandler(PointerPressedEvent, OnPanelPointerPressed, RoutingStrategies.Tunnel);
    }

    // ── Wiring ────────────────────────────────────────────────────────────────

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.TerminalActionRequested -= OnTerminalActionRequested;
        }

        _viewModel = DataContext as MainWindowViewModel;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.TerminalActionRequested += OnTerminalActionRequested;
            _output.FontSize = _viewModel.TerminalFontSize;
        }

        EnsureStarted();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        EnsureStarted();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        StopSession();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == IsVisibleProperty && change.GetNewValue<bool>())
            EnsureStarted();
    }

    /// <summary>
    /// Starts the shell the first time the panel is genuinely on screen with a viewmodel
    /// behind it.
    ///
    /// <para>
    /// Lazy because a shell is a process: spawning one for a pane nobody has opened would
    /// cost a powershell.exe per launch. The three triggers — attached, data-context set,
    /// made visible — can arrive in any order, so all three funnel through here rather than
    /// each trying to guess whether the others have happened.
    /// </para>
    /// <para>
    /// Hiding the panel again deliberately leaves the shell running, so a half-typed
    /// command survives toggling the pane.
    /// </para>
    /// </summary>
    private void EnsureStarted()
    {
        if (_session is not null) return;
        if (_viewModel is null) return;
        if (!IsVisible || !_attached) return;

        StartSession();
        Dispatcher.UIThread.Post(() => _output.Focus(), DispatcherPriority.Input);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainWindowViewModel.RepoPath):
                // Follow the active repository tab. A running shell may be mid-command and
                // may have been `cd`'d elsewhere, so it is replaced rather than sent a `cd`
                // — that is the only way to guarantee the prompt matches the tab.
                if (!IsEffectivelyVisible) return;
                if (_session is null || !PathsMatch(_sessionRepoPath, _viewModel?.RepoPath))
                    RestartSession();
                return;

            case nameof(MainWindowViewModel.TerminalFontSize):
                _output.FontSize = _viewModel?.TerminalFontSize ?? 13;
                ResizeSessionToViewport(force: true);
                return;
        }
    }

    private static bool PathsMatch(string? left, string? right) =>
        string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    private void OnTerminalActionRequested(object? sender, TerminalAction action)
    {
        switch (action)
        {
            case TerminalAction.Clear: ClearScrollback(); return;
            case TerminalAction.Restart: RestartSession(); return;
            case TerminalAction.Interrupt: _session?.SendInterrupt(); return;
            case TerminalAction.CopyAll: _ = CopyAllAsync(); return;
        }
    }

    // ── Session lifetime ──────────────────────────────────────────────────────

    private void StartSession()
    {
        StopSession();

        var vm = _viewModel;
        if (vm is null) return;

        try
        {
            _session = TerminalSession.Start(vm.RepoPath, _output.VisibleColumns, _output.VisibleRows);
            _sessionRepoPath = vm.RepoPath;
            _lastColumns = _output.VisibleColumns;
            _lastRows = _output.VisibleRows;

            _session.OutputReceived += OnOutputReceived;
            _session.Exited += OnSessionExited;

            vm.TerminalStatus = string.Empty;
        }
        catch (Exception ex)
        {
            // Every failure here is one the user can act on — no repo open, the folder has
            // been deleted, an unsupported platform — so it belongs in the header rather
            // than in a log nobody reads.
            vm.TerminalStatus = ex.Message;
        }
    }

    private void RestartSession()
    {
        _screen.Clear();
        RefreshOutput();
        StartSession();
    }

    private void StopSession()
    {
        var session = _session;
        _session = null;
        _sessionRepoPath = null;
        if (session is null) return;

        session.OutputReceived -= OnOutputReceived;
        session.Exited -= OnSessionExited;
        session.Dispose();
    }

    private void OnSessionExited(object? sender, string reason)
    {
        // Raised on the reader thread.
        Dispatcher.UIThread.Post(() =>
        {
            if (_viewModel is not null)
                _viewModel.TerminalStatus = reason;
        });
    }

    // ── Output ────────────────────────────────────────────────────────────────

    private void OnOutputReceived(object? sender, string text)
    {
        bool schedule;
        lock (_pendingLock)
        {
            _pending.Append(text);
            schedule = !_flushScheduled;
            _flushScheduled = true;
        }

        if (schedule)
            Dispatcher.UIThread.Post(FlushPendingOutput, DispatcherPriority.Background);
    }

    private void FlushPendingOutput()
    {
        string chunk;
        lock (_pendingLock)
        {
            chunk = _pending.ToString();
            _pending.Clear();
            _flushScheduled = false;
        }

        if (chunk.Length == 0) return;

        // The screen is only ever written from the UI thread, which is what lets it stay
        // lock-free while the renderer reads its line list directly.
        _screen.Write(chunk);
        RefreshOutput();
    }

    private void RefreshOutput()
    {
        _output.Update(_screen.Lines, _screen.CursorColumn);

        if (!_stickToBottom || _scrollScheduled) return;

        // Deferred: the ScrollViewer cannot scroll to an extent it has not measured yet,
        // and the new lines were only just handed over. Guarded so that a burst of output
        // queues one scroll rather than one per chunk.
        _scrollScheduled = true;
        Dispatcher.UIThread.Post(
            () => { _scrollScheduled = false; OutputScroller.ScrollToEnd(); },
            DispatcherPriority.Loaded);
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // Only stay pinned to the bottom while the user is actually there — scrolling up to
        // read something must not be yanked back by the next line of output.
        var distanceFromBottom =
            OutputScroller.Extent.Height - OutputScroller.Viewport.Height - OutputScroller.Offset.Y;
        _stickToBottom = distanceFromBottom <= 4;

        ResizeSessionToViewport(force: false);
    }

    private void ClearScrollback()
    {
        _screen.Clear();
        RefreshOutput();
    }

    // ── Sizing ────────────────────────────────────────────────────────────────

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        ResizeSessionToViewport(force: false);
        return size;
    }

    /// <summary>
    /// Keeps the shell's idea of the window in step with ours. Without it the shell keeps
    /// wrapping at whatever width it was born with, and a resized panel breaks lines in
    /// visibly wrong places.
    /// </summary>
    private void ResizeSessionToViewport(bool force)
    {
        var session = _session;
        if (session is null) return;

        var columns = _output.VisibleColumns;
        var rows = _output.VisibleRows;
        if (!force && columns == _lastColumns && rows == _lastRows) return;

        _lastColumns = columns;
        _lastRows = rows;
        session.Resize(columns, rows);
    }

    // ── Input ─────────────────────────────────────────────────────────────────

    private void OnPanelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Buttons in the header keep their own focus behaviour; only clicks on the
        // scrollback move focus to the shell.
        if (e.Source is Visual source && source.FindAncestorOfType<Button>() is not null)
            return;

        _output.Focus();
    }

    private void OnOutputTextInput(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)) return;
        _session?.Send(e.Text);
        e.Handled = true;
    }

    private void OnOutputKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        // Ctrl+Shift+C/V are the terminal convention for copy/paste precisely because plain
        // Ctrl+C has to stay available as the interrupt.
        if (ctrl && shift && e.Key == Key.C) { _ = CopyAllAsync(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.V) { _ = PasteAsync(); e.Handled = true; return; }
        if (ctrl && !shift && e.Key == Key.C) { _session?.SendInterrupt(); e.Handled = true; return; }
        if (ctrl && !shift && e.Key == Key.L) { ClearScrollback(); e.Handled = true; return; }

        // Ctrl+Tab cycles repository tabs at the window level; a terminal has no use for it,
        // so it is the one control chord deliberately left to bubble.
        if (ctrl && e.Key == Key.Tab) return;

        // Page keys scroll the transcript rather than reaching the shell. Scrollback is ours,
        // not the shell's — it has no idea these lines are still on screen.
        if (e.Key is Key.PageUp or Key.PageDown) return;

        var sequence = MapKey(e.Key, ctrl);
        if (sequence is null) return;

        _session?.Send(sequence);
        e.Handled = true;
    }

    /// <summary>
    /// Translates a key press into the bytes a shell expects. Returns null for keys that
    /// produce a character — those arrive properly decoded (dead keys, IME and all) through
    /// <see cref="InputElement.TextInput"/>, and duplicating them here would double them up.
    /// </summary>
    private static string? MapKey(Key key, bool ctrl)
    {
        var sequence = key switch
        {
            Key.Enter => "\r",
            Key.Back => "\b",
            Key.Tab => "\t",
            Key.Escape => "\x1B",
            Key.Up => "\x1B[A",
            Key.Down => "\x1B[B",
            Key.Right => "\x1B[C",
            Key.Left => "\x1B[D",
            Key.Home => "\x1B[H",
            Key.End => "\x1B[F",
            Key.Delete => "\x1B[3~",
            Key.Insert => "\x1B[2~",
            _ => null,
        };

        if (sequence is not null || !ctrl) return sequence;

        // Ctrl+letter is a control character: Ctrl+A is 0x01 through Ctrl+Z is 0x1A. This
        // is what makes Ctrl+R (history search), Ctrl+W (delete word) and Ctrl+D (EOF) work,
        // and it is why the terminal takes priority over the app's chords while focused.
        if (key is >= Key.A and <= Key.Z)
            return ((char)(key - Key.A + 1)).ToString();

        return null;
    }

    private async System.Threading.Tasks.Task CopyAllAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;
        await clipboard.SetTextAsync(_screen.GetText());
    }

    private async System.Threading.Tasks.Task PasteAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;

        var text = await clipboard.TryGetTextAsync();
        if (string.IsNullOrEmpty(text)) return;

        // Shells expect CR for "line submitted". Pasting CRLF would submit each line twice.
        _session?.Send(text.Replace("\r\n", "\r").Replace('\n', '\r'));
    }
}
