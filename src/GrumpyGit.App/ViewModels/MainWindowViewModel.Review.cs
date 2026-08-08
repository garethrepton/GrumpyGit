using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GrumpyGit.App.Services;
using GrumpyGit.Core.Git;
using GrumpyGit.Core.Agents;
using GrumpyGit.Core.LocalModel;
using GrumpyGit.Core.Models;

namespace GrumpyGit.App.ViewModels;

/// <summary>
/// Partial class — the chosen review module's reading of whatever diff is on screen.
///
/// Every diff asks for a review, and asks for it in the background: the diff renders when
/// git answers, and the review appears underneath whenever it is ready. Moving to another
/// file cancels the one in flight rather than queueing behind it, so the panel always
/// describes the file you are looking at and never the one you just left.
///
/// None of that depends on which module answered — that is the whole point of
/// <see cref="IReviewAgent"/>. What does depend on it is one sentence the user must always
/// be able to see: whether the diff left this machine. The panel carries the module's badge
/// for exactly that reason, and <see cref="MainWindowViewModel.Modules"/> holds the choice.
///
/// Nothing is written down here whichever module is in use: the prompt is built in memory
/// and the answer lives as long as the session does (commandment 9).
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>
    /// How long a selection has to stand still before it is worth asking the module. Long
    /// enough to skip the files you click past, short enough that a deliberate click feels
    /// immediate.
    /// </summary>
    private static readonly TimeSpan ReviewDebounce = TimeSpan.FromMilliseconds(400);

    private IReviewAgent? _agent;
    private DiffReviewService? _reviewService;
    private CancellationTokenSource? _reviewCts;

    [ObservableProperty] private string _diffReviewText = string.Empty;
    [ObservableProperty] private bool _isDiffReviewRunning;
    [ObservableProperty] private bool _isDiffReviewVisible = true;

    /// <summary>Issues the model believes it found, newest review only.</summary>
    public ObservableCollection<ReviewIssueViewModel> DiffReviewIssues { get; } = new();

    /// <summary>
    /// One note per change, for the callouts drawn above each section of the diff, and the
    /// lines to warn on. Both are plain data the view reads — the drawing lives in the
    /// diff control, which is the only thing that knows where a line ends up on screen.
    /// </summary>
    [ObservableProperty] private IReadOnlyList<ChangeNote> _diffChangeNotes = [];
    [ObservableProperty] private IReadOnlyList<int> _diffWarningLines = [];

    /// <summary>
    /// Which changes reach the filesystem, the network, a process or a credential. Drawn as
    /// a badge on the section that carries them, so a reader skimming twenty sections knows
    /// which three to stop at.
    /// </summary>
    [ObservableProperty] private IReadOnlyList<ChangeConcern> _diffChangeConcerns = [];

    [ObservableProperty] private ReviewRisk _diffReviewRisk = ReviewRisk.None;

    public bool HasDiffReviewIssues => DiffReviewIssues.Count > 0;

    /// <summary>Badge text. Empty for an unremarkable change, so nothing is drawn at all.</summary>
    public string DiffRiskLabel => DiffReviewRisk switch
    {
        ReviewRisk.Danger => "DANGER",
        ReviewRisk.Caution => "CAUTION",
        _ => string.Empty,
    };

    public bool HasDiffRisk => DiffReviewRisk != ReviewRisk.None;

    /// <summary>Drives the badge's colour class — "danger" and "caution" are styled apart.</summary>
    public string DiffRiskClass => DiffReviewRisk == ReviewRisk.Danger ? "danger" : "warn";

    partial void OnDiffReviewRiskChanged(ReviewRisk value)
    {
        OnPropertyChanged(nameof(DiffRiskLabel));
        OnPropertyChanged(nameof(HasDiffRisk));
        OnPropertyChanged(nameof(DiffRiskClass));
    }

    /// <summary>Path to the GGUF, mirrored into the settings panel.</summary>
    [ObservableProperty] private string _settingsLocalModelPath = string.Empty;

    /// <summary>
    /// True once a module is chosen and set up. Drives whether the panel exists at all — a
    /// user who picked no module should see no trace of the feature rather than an empty
    /// box explaining its absence.
    /// </summary>
    public bool HasReviewAgent => _agent?.IsConfigured == true;

    /// <summary>
    /// Header text: doubles as the status line, so the panel needs no spinner. Names the
    /// module when idle, because "which of these read my code" is the question a reader has
    /// every time and should never have to open settings to answer.
    /// </summary>
    public string DiffReviewHeader => IsDiffReviewRunning
        ? "READING THE DIFF…"
        : ActiveModule?.Badge is { Length: > 0 } badge ? $"{badge} REVIEW" : "REVIEW";

    partial void OnIsDiffReviewRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(DiffReviewHeader));
        UpdateNotebookRunningState(value);
    }

    [RelayCommand]
    private void ToggleDiffReview() => IsDiffReviewVisible = !IsDiffReviewVisible;

    /// <summary>
    /// Builds the agent and the review queue from settings. Called at startup and again
    /// whenever the module or the model path changes; the old agent is disposed, since
    /// loaded weights and a changed choice have no sensible combined state.
    /// </summary>
    private void InitialiseReviewAgent(ReviewModuleId module, string? modelPath)
    {
        CancelPendingReview();

        (_agent as IDisposable)?.Dispose();

        _agent = ReviewAgentFactory.Create(module, modelPath, AppPaths.AgentWorkDir);
        _reviewService = _agent is null ? null : new DiffReviewService(_agent);

        DiffReviewText = string.Empty;
        OnPropertyChanged(nameof(HasReviewAgent));
        OnPropertyChanged(nameof(ActiveModule));
        OnPropertyChanged(nameof(ActiveModuleId));
        OnPropertyChanged(nameof(UsesLocalModule));
        OnPropertyChanged(nameof(ActiveModuleName));
        OnPropertyChanged(nameof(ActiveModuleSendsCodeOffMachine));
        OnPropertyChanged(nameof(ReviewPrivacyLine));
        OnPropertyChanged(nameof(DiffReviewHeader));
        OnPropertyChanged(nameof(ShowsModelOffer));
        OnPropertyChanged(nameof(IsReviewPanelVisible));
        OnPropertyChanged(nameof(CanRunAiScan));
    }

    /// <summary>
    /// Asks for a reading of the diff now on screen. Returns immediately: loading the
    /// model on first use takes seconds, and generation takes more, none of which the
    /// diff should wait for.
    /// </summary>
    private void RequestDiffReview(string? path, ParsedDiff? parsed)
    {
        CancelPendingReview();
        ClearReview();

        if (_reviewService is null || string.IsNullOrEmpty(path) || parsed is null || parsed.Hunks.Count == 0)
            return;

        // A review already held for this exact diff is shown in the same frame as the
        // diff, rather than flashing "reading…" for something already known.
        var cached = _reviewService.TryGetCached(path, parsed);
        if (cached is not null)
        {
            ApplyReview(cached);
            return;
        }

        if (!HasReviewAgent)
            return;

        var summary = ChangeSummaryBuilder.Build(path, parsed);
        var cts = new CancellationTokenSource();
        _reviewCts = cts;

        IsDiffReviewRunning = true;

        // Progress<T> captures this (UI) context, so partial text lands on the UI thread
        // without the viewmodel knowing about a dispatcher. Only the summary is streamed:
        // the rest of the reply is labels the parser eats, and watching "HUNK 3:" type
        // itself out is noise, not progress.
        var partial = new Progress<string>(text =>
        {
            if (ReferenceEquals(_reviewCts, cts))
                DiffReviewText = FirstSummaryLine(text);
        });

        _ = RunReviewAsync(path, parsed, summary, partial, cts);
    }

    private async Task RunReviewAsync(
        string path,
        ParsedDiff parsed,
        FileChangeSummary summary,
        IProgress<string> partial,
        CancellationTokenSource cts)
    {
        try
        {
            // Settle first. Clicking down a file list would otherwise start an inference
            // per file and cancel each one a moment later — eight threads spinning up and
            // dying repeatedly, while git.exe waits behind them for the next click. The
            // pause costs nothing a reader notices and removes the thrash entirely.
            await Task.Delay(ReviewDebounce, cts.Token);

            // Each pass of a multi-pass file paints as it lands, so the sections fill in
            // from the top rather than the whole file appearing at once minutes later.
            var onChunk = new Progress<DiffReviewResult>(result =>
            {
                if (ReferenceEquals(_reviewCts, cts))
                    ApplyReview(result);
            });

            var review = await _reviewService!.ReviewAsync(
                path, parsed, summary, partial, cts.Token, onChunk);

            // A result for a file the user has already left is dropped rather than
            // painted over the current one.
            if (!ReferenceEquals(_reviewCts, cts))
                return;

            switch (review.State)
            {
                case DiffReviewState.Complete:
                    ApplyReview(review.Result);
                    break;
                case DiffReviewState.Failed:
                    ClearReview();
                    DiffReviewText = $"{ActiveModuleName} could not read this diff: {review.Text}";
                    break;
                case DiffReviewState.TooLarge:
                    ClearReview();
                    DiffReviewText =
                        $"This change is too large to review — more than {DiffReviewService.MaxChangedLines} lines changed.";
                    break;
                default:
                    ClearReview();
                    break;
            }
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_reviewCts, cts))
                DiffReviewText = $"Review failed: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_reviewCts, cts))
                IsDiffReviewRunning = false;
            cts.Dispose();
        }
    }

    /// <summary>Puts a finished review on screen: summary, badge, issues, callouts, warnings.</summary>
    private void ApplyReview(DiffReviewResult result)
    {
        // Only overwrite when there is something to overwrite with. A reply that carried
        // CHANGE lines but no parseable SUMMARY is still Complete, and assigning its empty
        // summary here blanked the panel at the exact moment the descriptions arrived —
        // the overview appearing to vanish as the change notes loaded.
        if (result.HasSummary)
            DiffReviewText = result.Summary;

        DiffReviewRisk = result.Risk;

        DiffReviewIssues.Clear();
        foreach (var issue in result.Issues)
            DiffReviewIssues.Add(new ReviewIssueViewModel { Model = issue });
        OnPropertyChanged(nameof(HasDiffReviewIssues));

        DiffChangeNotes = result.ChangeNotes;
        DiffChangeConcerns = result.Concerns;

        // Only anchored issues can be highlighted — an issue the model attached to a line
        // the diff never showed has nowhere to draw a warning.
        DiffWarningLines = result.Issues
            .Where(i => i.IsAnchored)
            .Select(i => i.RenderedLine)
            .Distinct()
            .ToList();

        // The notebook puts these notes above their hunks, so it is stale until it is told.
        RebuildNotebook();
        OnPropertyChanged(nameof(DiffReviewDetail));
        OnPropertyChanged(nameof(HasDiffReviewDetail));
    }

    /// <summary>
    /// The line under the summary: what the model was given and what it made of it.
    ///
    /// Worth stating because the summary alone cannot be judged without it. "Read 6 of 19
    /// changes" is the difference between a reading of the file and a reading of its first
    /// third, and that is exactly the thing a confident-sounding sentence hides.
    /// </summary>
    public string DiffReviewDetail
    {
        get
        {
            var blocks = DiffNotebook.Split(CurrentDiff);
            if (blocks.Count == 0) return string.Empty;

            var sent = blocks.Count(b => b.WasSentToModel);
            var described = DiffChangeNotes.Count;

            var parts = new List<string>
            {
                blocks.Count == 1 ? "1 change" : $"{blocks.Count} changes",
                $"+{blocks.Sum(b => b.Added)} −{blocks.Sum(b => b.Removed)}",
                $"{described} described",
            };

            if (sent < blocks.Count)
                parts.Add($"{blocks.Count - sent} past the review budget");

            if (DiffReviewIssues.Count > 0)
                parts.Add(DiffReviewIssues.Count == 1 ? "1 issue" : $"{DiffReviewIssues.Count} issues");

            return string.Join(" · ", parts);
        }
    }

    public bool HasDiffReviewDetail => DiffReviewDetail.Length > 0;

    private void ClearReview()
    {
        DiffReviewText = string.Empty;
        DiffReviewRisk = ReviewRisk.None;
        DiffReviewIssues.Clear();
        OnPropertyChanged(nameof(HasDiffReviewIssues));
        DiffChangeNotes = [];
        DiffChangeConcerns = [];
        DiffWarningLines = [];
        IsDiffReviewRunning = false;
        RebuildNotebook();
        OnPropertyChanged(nameof(DiffReviewDetail));
        OnPropertyChanged(nameof(HasDiffReviewDetail));
    }

    /// <summary>
    /// Pulls the summary out of a reply that is still arriving. The model writes SUMMARY
    /// first, so this shows a sentence forming rather than an empty box for several
    /// seconds — but it must not show the label itself.
    /// </summary>
    private static string FirstSummaryLine(string partialReply)
    {
        const string label = "SUMMARY:";
        var start = partialReply.IndexOf(label, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
            return string.Empty;

        var text = partialReply[(start + label.Length)..];
        var end = text.IndexOf('\n');
        return (end < 0 ? text : text[..end]).Trim();
    }

    // ── Changeset orientation ─────────────────────────────────────────────────

    [ObservableProperty] private string _changeSetSummary = string.Empty;
    [ObservableProperty] private bool _isChangeSetReviewRunning;
    [ObservableProperty] private ReviewRisk _changeSetRisk = ReviewRisk.None;

    /// <summary>Files the model says to read first, with its reason for each.</summary>
    public ObservableCollection<WatchItem> ChangeSetWatch { get; } = new();

    private CancellationTokenSource? _changeSetCts;

    public bool HasChangeSetReview => ChangeSetSummary.Length > 0 || ChangeSetWatch.Count > 0;

    public bool ChangeSetHasRisk => ChangeSetRisk != ReviewRisk.None;

    public string ChangeSetRiskLabel => ChangeSetRisk switch
    {
        ReviewRisk.Danger => "DANGER",
        ReviewRisk.Caution => "CAUTION",
        _ => string.Empty,
    };

    public string ChangeSetHeader => IsChangeSetReviewRunning ? "READING THE CHANGE…" : "OVERVIEW";

    partial void OnChangeSetSummaryChanged(string value)
        => OnPropertyChanged(nameof(HasChangeSetReview));

    partial void OnIsChangeSetReviewRunningChanged(bool value)
        => OnPropertyChanged(nameof(ChangeSetHeader));

    partial void OnChangeSetRiskChanged(ReviewRisk value)
    {
        OnPropertyChanged(nameof(ChangeSetHasRisk));
        OnPropertyChanged(nameof(ChangeSetRiskLabel));
    }

    /// <summary>
    /// Asks for an overview of the whole set of changes now selected — a commit, or the
    /// working tree. Called once the file list is known, since the file list <em>is</em>
    /// the input: this pass reads the shape of the change, not its code.
    /// </summary>
    private void RequestChangeSetReview(string title, IEnumerable<FileChangeViewModel> files)
    {
        CancelChangeSetReview();
        ClearChangeSetReview();

        if (_reviewService is null || !HasReviewAgent) return;

        var input = files
            .Select(f => new ChangeSetFile(
                f.Path,
                f.LinesAdded,
                f.LinesRemoved,
                [],
                // Anything already reviewed contributes its own reading, which costs
                // nothing and is far better than what the shape alone would suggest.
                _reviewService.TryGetCachedSummary(f.Path)))
            .ToList();

        if (input.Count == 0) return;

        var cts = new CancellationTokenSource();
        _changeSetCts = cts;
        IsChangeSetReviewRunning = true;

        _ = RunChangeSetReviewAsync(title, input, cts);
    }

    private async Task RunChangeSetReviewAsync(
        string title, IReadOnlyList<ChangeSetFile> input, CancellationTokenSource cts)
    {
        try
        {
            // Same settle-first rule as the per-file review: clicking down the commit list
            // should not start an inference for every commit it passes through.
            await Task.Delay(ReviewDebounce, cts.Token);

            var result = await _reviewService!.ReviewChangeSetAsync(title, input, cts.Token);

            if (!ReferenceEquals(_changeSetCts, cts) || result is null) return;

            ChangeSetSummary = result.Summary;
            ChangeSetRisk = result.Risk;
            ChangeSetWatch.Clear();
            foreach (var item in result.Watch)
                ChangeSetWatch.Add(item);
            OnPropertyChanged(nameof(HasChangeSetReview));
        }
        catch (OperationCanceledException)
        {
            // Moved on before it answered.
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_changeSetCts, cts))
                StatusMessage = $"Overview failed: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_changeSetCts, cts))
                IsChangeSetReviewRunning = false;
            cts.Dispose();
        }
    }

    private void ClearChangeSetReview()
    {
        ChangeSetSummary = string.Empty;
        ChangeSetRisk = ReviewRisk.None;
        ChangeSetWatch.Clear();
        IsChangeSetReviewRunning = false;
        OnPropertyChanged(nameof(HasChangeSetReview));
    }

    private void CancelChangeSetReview()
    {
        var running = _changeSetCts;
        _changeSetCts = null;
        try { running?.Cancel(); }
        catch (ObjectDisposedException) { /* already finished */ }
    }

    /// <summary>Opens the file the overview points at, so a watch line is one click.</summary>
    [RelayCommand]
    private void OpenWatchedFile(WatchItem? item)
    {
        if (item is null) return;

        var file = ChangedFiles.FirstOrDefault(f => f.Path == item.Path)
                   ?? StagedFiles.FirstOrDefault(f => f.Path == item.Path);
        if (file is not null)
            SelectedFile = file;
    }

    /// <summary>Scrolls the diff to the line an issue names, the way the symbol list does.</summary>
    [RelayCommand]
    private void GoToIssue(ReviewIssueViewModel? issue)
    {
        if (issue?.Model is not { IsAnchored: true } model) return;
        ScrollToDiffLineRequested?.Invoke(this, model.RenderedLine);
    }

    private void CancelPendingReview()
    {
        var running = _reviewCts;
        _reviewCts = null;
        IsDiffReviewRunning = false;

        try { running?.Cancel(); }
        catch (ObjectDisposedException) { /* already finished and cleaned up */ }
    }

    // ── Settings ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ChooseLocalModelAsync()
    {
        if (OwnerWindow is null) return;

        var picked = await OwnerWindow.StorageProvider.OpenFilePickerAsync(
            new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "Choose a GGUF model file",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new Avalonia.Platform.Storage.FilePickerFileType("GGUF model")
                    {
                        Patterns = ["*.gguf"],
                    },
                ],
            });

        if (picked.Count == 0) return;

        SettingsLocalModelPath = picked[0].Path.LocalPath;
    }

    [RelayCommand]
    private void ClearLocalModel() => SettingsLocalModelPath = string.Empty;

    // ── Download ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The models the app will fetch. A fixed list, so the outbound surface is two known
    /// URLs rather than "whatever a remote index names".
    /// </summary>
    public IReadOnlyList<ModelOption> DownloadableModels => ModelCatalogue.All;

    [ObservableProperty] private ModelOption? _selectedDownloadModel = ModelCatalogue.QwenCoder15B;
    [ObservableProperty] private bool _isDownloadingModel;
    [ObservableProperty] private double _modelDownloadFraction;
    [ObservableProperty] private string _modelDownloadStatus = string.Empty;

    private CancellationTokenSource? _downloadCts;

    /// <summary>
    /// True when the offer to fetch a model should be shown: the local module is chosen,
    /// no model is configured yet, and no download is already running.
    ///
    /// Gated on the module because the offer is now one module's setup step rather than the
    /// feature's front door — a user on Copilot has no use for a GGUF and should never be
    /// asked to fetch one.
    /// </summary>
    public bool ShowsModelOffer =>
        ActiveModuleId == ReviewModuleId.Local
        && !HasLocalModelFile
        && !IsDownloadingModel
        && !_modelOfferDeclined;

    /// <summary>
    /// The panel exists when there is a review to show, a download running, or an offer to
    /// make. For a user who chose no module it disappears entirely and the client is exactly
    /// what it was before any of this was added.
    /// </summary>
    public bool IsReviewPanelVisible => HasReviewAgent || IsDownloadingModel || ShowsModelOffer;

    private bool _modelOfferDeclined;

    partial void OnIsDownloadingModelChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowsModelOffer));
        OnPropertyChanged(nameof(IsReviewPanelVisible));
    }

    /// <summary>Turns the offer down for good. Settings keeps the door open.</summary>
    [RelayCommand]
    private void DeclineModelOffer()
    {
        _modelOfferDeclined = true;

        var settings = AppSettings.Load();
        settings.LocalModelOfferDeclined = true;
        settings.Save();

        OnPropertyChanged(nameof(ShowsModelOffer));
        OnPropertyChanged(nameof(IsReviewPanelVisible));
    }

    /// <summary>
    /// Fetches the chosen model, verifies it, and turns local review on. Runs only from a
    /// button press — nothing here is automatic, and nothing is fetched at startup.
    /// </summary>
    [RelayCommand]
    private async Task DownloadModelAsync()
    {
        if (SelectedDownloadModel is not { } option || IsDownloadingModel) return;

        var directory = Path.Combine(AppPaths.Root, "models");

        _downloadCts = new CancellationTokenSource();
        IsDownloadingModel = true;
        ModelDownloadFraction = 0;
        ModelDownloadStatus = $"Starting {option.Name}…";

        var progress = new Progress<DownloadProgress>(p =>
        {
            ModelDownloadFraction = p.Fraction;
            ModelDownloadStatus = $"{option.Name} — {p.Label}";
        });

        try
        {
            var path = await new ModelDownloader()
                .DownloadAsync(option, directory, progress, _downloadCts.Token);

            SaveActiveModelPath(path);

            ModelDownloadStatus = string.Empty;
            ShowToast("Model ready — diffs will be reviewed locally from here on.", Controls.ToastSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            ModelDownloadStatus = string.Empty;
            ShowToast("Download cancelled.", Controls.ToastSeverity.Info);
        }
        catch (Exception ex)
        {
            ModelDownloadStatus = string.Empty;
            ShowToast($"Download failed: {ex.Message}", Controls.ToastSeverity.Error, 8000);
        }
        finally
        {
            IsDownloadingModel = false;
            _downloadCts?.Dispose();
            _downloadCts = null;
        }
    }

    [RelayCommand]
    private void CancelModelDownload()
    {
        try { _downloadCts?.Cancel(); }
        catch (ObjectDisposedException) { /* already finished */ }
    }

    /// <summary>
    /// Applies a module and a model path. Rebuilds only when one of them actually changed —
    /// otherwise saving any unrelated setting would drop a loaded model and pay the
    /// several-second reload on the next diff.
    /// </summary>
    private void ApplyModuleSetting(ReviewModuleId module, string path)
    {
        var trimmed = path?.Trim() ?? string.Empty;
        if (module == ReviewModuleId.Local && trimmed.Length > 0 && !File.Exists(trimmed))
        {
            ShowToast("That model file no longer exists — local review is off.", Controls.ToastSeverity.Warning);
            trimmed = string.Empty;
        }

        if (module == _moduleInUse
            && string.Equals(_localModelPathInUse, trimmed, StringComparison.OrdinalIgnoreCase))
            return;

        _moduleInUse = module;
        _localModelPathInUse = trimmed;
        InitialiseReviewAgent(module, trimmed.Length == 0 ? null : trimmed);
    }

    private string _localModelPathInUse = string.Empty;
    private ReviewModuleId _moduleInUse = ReviewModuleId.None;

    /// <summary>True when the local module has a GGUF to point at.</summary>
    private bool HasLocalModelFile => _localModelPathInUse.Length > 0;

    /// <summary>Which module is live. Null when the feature is off.</summary>
    public ReviewModule? ActiveModule => ReviewModuleCatalogue.Find(_moduleInUse);

    public ReviewModuleId ActiveModuleId => _moduleInUse;

    /// <summary>Gates the settings rows that only mean anything for the local module.</summary>
    public bool UsesLocalModule => _moduleInUse == ReviewModuleId.Local;

    /// <summary>The module's name in a sentence, for the messages that name it.</summary>
    public string ActiveModuleName => ActiveModule?.Name ?? "The review module";

    /// <summary>
    /// True when the live module sends the diff off this machine. Drives the one badge the
    /// panel must never lose: a reader has to be able to tell a private reading from a
    /// hosted one at a glance, not by remembering what they picked.
    /// </summary>
    public bool ActiveModuleSendsCodeOffMachine => ActiveModule?.SendsCodeOffMachine == true;

    /// <summary>
    /// The line under the panel header: what this reading is worth, and where the diff went
    /// to produce it. Both halves matter — the first stops a suggestion being read as a
    /// verdict, and the second is the thing a user must never have to remember.
    /// </summary>
    public string ReviewPrivacyLine => ActiveModule is { } module
        ? $"a suggestion, not a verdict · {module.PrivacyLine}"
        : "a suggestion, not a verdict";

    /// <summary>Loads the chosen module at startup, before any diff is opened.</summary>
    private void InitialiseReviewModuleFromSettings()
    {
        var settings = AppSettings.Load();
        SettingsLocalModelPath = settings.LocalModelPath;
        _modelOfferDeclined = settings.LocalModelOfferDeclined;
        ApplyModuleSetting(settings.ResolveReviewModule(), settings.LocalModelPath);
        BuildModelLibrary();
        InitialiseModulePicker(settings);
    }
}
