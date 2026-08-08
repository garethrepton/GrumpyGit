using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;

using GrumpyGit.Core.Agents;

namespace GrumpyGit.Core.LocalModel;

/// <summary>
/// <see cref="IReviewAgent"/> on llama.cpp, via LLamaSharp, in this process.
///
/// The only type in the application that loads model weights or runs inference. It reads
/// one file from disk and otherwise touches nothing: no network, no cache directory, no
/// temporary file, and no logging of prompts — a prompt here is the user's source code
/// (commandment 9).
///
/// The model path is fixed at construction rather than settable. A loaded model and a
/// changed path have no sensible combined state, so changing the setting disposes this
/// instance and builds another; there is no half-configured object to reason about.
/// </summary>
public sealed class LlamaLocalModel : IReviewAgent, IDisposable
{
    /// <summary>
    /// Enough for the prompt budget plus the answer, and no more. The KV cache is sized
    /// from this, so a larger window costs memory on every review whether or not any diff
    /// is big enough to use it.
    /// </summary>
    private const uint ContextTokens = 8192;

    /// <summary>
    /// Offload every layer. llama.cpp caps this at the model's actual layer count, so a
    /// number comfortably above any real model means "all of it" without this code having
    /// to know how many layers the user's GGUF has. Ignored entirely when no GPU backend
    /// registers, which is what makes the same value safe on a machine without one.
    /// </summary>
    private const int AllLayersOnGpu = 99;

    private static int _nativeConfigured;

    /// <summary>
    /// Chooses the GPU backend, once per process and before any native call.
    ///
    /// Vulkan rather than CUDA, which was measured rather than assumed: on an RTX 4080 the
    /// Vulkan backend ran 124 tok/s against 31 on four CPU threads, while the CUDA backend
    /// silently fell back to CPU because its <c>ggml-cuda.dll</c> needs the CUDA Toolkit
    /// runtime installed — a thing no user of a git client should have to install. Vulkan
    /// needs only the graphics driver and works on NVIDIA, AMD and Intel alike.
    ///
    /// Auto-fallback stays on, so a machine with no usable Vulkan device quietly runs on
    /// the CPU backend instead of failing to load.
    /// </summary>
    private static void ConfigureNativeBackend()
    {
        if (Interlocked.Exchange(ref _nativeConfigured, 1) == 1)
            return;

        try
        {
            NativeLibraryConfig.All.WithVulkan(true).WithCuda(false).WithAutoFallback(true);
        }
        catch
        {
            // Throws if a native call already happened. Nothing to do about it here, and
            // the CPU backend still works — this is a preference, not a requirement.
        }
    }

    private readonly string? _modelPath;
    private readonly SemaphoreSlim _loadGate = new(1, 1);

    private LLamaWeights? _weights;
    private ModelParams? _parameters;
    private StatelessExecutor? _executor;
    private string _executorSystemMessage = string.Empty;
    private bool _loadFailed;
    private bool _disposed;

    /// <summary>
    /// Set once this model has been found to have no chat template, so the fallback is
    /// taken directly rather than by throwing on every review.
    /// </summary>
    private bool _templateUnavailable;

    private readonly ChatFormat _chatFormat;

    /// <param name="modelPath">
    /// A GGUF file the user already has. Null or missing means the feature is off — not an
    /// error, since nothing else in the client depends on it.
    /// </param>
    /// <param name="chatFormat">
    /// How to mark up turns when the file carries no template of its own. Defaults to
    /// reading it from the model, which is right for everything that has one.
    /// </param>
    public LlamaLocalModel(string? modelPath, ChatFormat chatFormat = ChatFormat.FromModel)
    {
        _modelPath = modelPath;
        _chatFormat = chatFormat;
    }

    public ReviewModuleId Module => ReviewModuleId.Local;

    public bool IsReady => _executor is not null;

    public string? LoadError { get; private set; }

    /// <summary>
    /// Physical memory the machine reports. Used only to turn "failed to load" into a
    /// sentence worth reading — llama.cpp's own message for running out of room is a
    /// tensor-allocation failure, which tells a user nothing about the 48 GB file they just
    /// fetched onto a 32 GB machine.
    /// </summary>
    private static long TotalMemoryBytes => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

    /// <summary>True when a path was configured at all, whether or not it loaded.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_modelPath);

    public async Task<bool> EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_executor is not null) return true;
        if (_loadFailed || _disposed) return false;
        if (string.IsNullOrWhiteSpace(_modelPath) || !File.Exists(_modelPath)) return false;

        ConfigureNativeBackend();

        await _loadGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_executor is not null) return true;
            if (_loadFailed) return false;

            var parameters = new ModelParams(_modelPath)
            {
                ContextSize = ContextTokens,

                // Everything on the GPU when there is one. Harmless when there is not:
                // with no GPU backend registered llama.cpp keeps the layers on the CPU.
                GpuLayerCount = AllLayersOnGpu,

                // Only matters on the CPU path. llama.cpp defaults to every hardware
                // thread, and half was still too greedy: on a 16-core machine that is
                // eight threads at 100% for the length of a review, and the next thing the
                // user clicks needs git.exe, which then loses the race and makes the whole
                // app feel stuck.
                Threads = Math.Max(2, Environment.ProcessorCount / 4),
            };

            _weights = await LLamaWeights.LoadFromFileAsync(parameters, ct).ConfigureAwait(false);
            _parameters = parameters;

            // Stateless: each review is independent, and nothing from one file's diff
            // leaks into the reading of the next. ApplyTemplate lets the model's own chat
            // template shape the turn, so this code never hard-codes one model's format.
            _executor = NewExecutor(string.Empty);

            return true;
        }
        catch (OperationCanceledException)
        {
            // Cancelled load is not a failed one — a later review may try again.
            return false;
        }
        catch (Exception ex)
        {
            // A wrong file, an unsupported architecture, or not enough memory. Latch it:
            // retrying on every diff would freeze the app repeatedly for the same answer.
            _loadFailed = true;
            LoadError = DescribeLoadFailure(ex);
            _weights?.Dispose();
            _weights = null;
            return false;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    public async Task<string> CompleteAsync(
        ModelPrompt prompt,
        ReviewOptions options,
        IProgress<string>? partial = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(options);

        if (!await EnsureLoadedAsync(ct).ConfigureAwait(false) || _executor is null)
            throw new InvalidOperationException("No local model is loaded.");

        // SystemMessage is init-only, so a changed system turn means a new executor rather
        // than a mutation. In practice the instruction is constant, so this rebuilds once
        // on the first review and never again; the weights — the expensive part — are
        // shared across executors and loaded once.
        if (!string.Equals(_executorSystemMessage, prompt.System, StringComparison.Ordinal))
            _executor = NewExecutor(prompt.System);

        var inference = new InferenceParams
        {
            MaxTokens = options.MaxTokens,
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = options.Temperature,

                // A review that repeats itself burns the token budget on nothing; a small
                // penalty is enough without pushing the model into inventing variety.
                RepeatPenalty = 1.1f,
            },
        };

        try
        {
            return await GenerateAsync(prompt, inference, partial, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (!_templateUnavailable && IsMissingTemplate(ex))
        {
            // Not every GGUF carries a chat template. Google's QAT builds of Gemma 4 ship
            // without one, and llama.cpp then refuses to apply a template it cannot find —
            // which surfaced as a model that downloaded, verified, loaded, and reviewed
            // nothing. Fall back to sending the turns as plain text rather than declining a
            // model the user has already spent gigabytes on.
            _templateUnavailable = true;
            _executor = NewExecutor(prompt.System);

            return await GenerateAsync(prompt, inference, partial, ct).ConfigureAwait(false);
        }
    }

    private async Task<string> GenerateAsync(
        ModelPrompt prompt, InferenceParams inference, IProgress<string>? partial, CancellationToken ct)
    {
        var text = _templateUnavailable ? WithoutModelTemplate(prompt) : prompt.User;

        var answer = new StringBuilder();
        await foreach (var token in _executor!.InferAsync(text, inference, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            answer.Append(token);

            // Reported as the whole answer so far rather than the delta: the panel binds
            // one string, and reassembling deltas in the UI is a second place to get the
            // ordering wrong.
            partial?.Report(answer.ToString());
        }

        return answer.ToString();
    }

    /// <summary>
    /// The prompt marked up by hand, for a model whose file carries no template.
    ///
    /// Only reached for models the catalogue names a format for. Anything else — a GGUF the
    /// user supplied that also lacks a template — gets the two turns as plain text, which
    /// is the honest answer when nobody has told this code what the model expects: no
    /// markers at all beats markers borrowed from a different model, which look like
    /// structure and are not.
    /// </summary>
    private string WithoutModelTemplate(ModelPrompt prompt) => _chatFormat switch
    {
        // Gemma has no system role. Its own convention is to fold the standing instruction
        // into the first user turn, so that is what this does rather than inventing a slot.
        ChatFormat.Gemma =>
            $"<start_of_turn>user\n{prompt.System}\n\n{prompt.User}<end_of_turn>\n<start_of_turn>model\n",

        _ => $"{prompt.System}\n\n{prompt.User}",
    };

    /// <summary>
    /// Whether this failure is llama.cpp declining to apply a chat template the model does
    /// not have.
    ///
    /// Matched on the message because the native layer reports it as a general failure with
    /// no distinguishing type. Narrow on purpose: a wrong guess here retries a real error
    /// once with a differently-shaped prompt, so it must not swallow anything else.
    /// </summary>
    private static bool IsMissingTemplate(Exception ex) =>
        ex.GetType().FullName is { } name
        && name.StartsWith("LLama.Exceptions.", StringComparison.Ordinal)
        && name.Contains("Template", StringComparison.Ordinal);

    /// <summary>
    /// Turns a native load failure into something a user can act on.
    ///
    /// The overwhelmingly common cause is a model larger than the machine, and llama.cpp
    /// reports that as a failed tensor allocation — accurate and useless. Weights plus the
    /// KV cache have to be resident, so the file size against physical memory is the check
    /// worth stating, and stating it beats any wording of the underlying error.
    /// </summary>
    private string DescribeLoadFailure(Exception ex)
    {
        var total = TotalMemoryBytes;

        try
        {
            var size = ModelSizeOnDisk();

            // Not a tight bound — the KV cache and llama.cpp's own overhead sit on top —
            // but a model already past raw physical memory has no chance at all.
            if (size > 0 && total > 0 && size >= total)
                return $"This model needs about {Gb(size)} of memory and this machine has {Gb(total)}. " +
                       "Pick a smaller one from Settings — the download is kept, so you can come back to it.";
        }
        catch
        {
            // Falling back to the raw message is the whole point of this being best-effort.
        }

        return ex.Message;
    }

    /// <summary>Every part of a sharded model, since llama.cpp will load all of them.</summary>
    private long ModelSizeOnDisk()
    {
        if (string.IsNullOrWhiteSpace(_modelPath)) return 0;

        var directory = Path.GetDirectoryName(_modelPath);
        var name = Path.GetFileName(_modelPath);

        // "…-00001-of-00004.gguf" — the loader opens the siblings, so the cost is all four.
        var split = name.IndexOf("-00001-of-", StringComparison.OrdinalIgnoreCase);
        if (split < 0 || directory is null)
            return new FileInfo(_modelPath).Length;

        return Directory
            .EnumerateFiles(directory, name[..split] + "-*.gguf")
            .Sum(f => new FileInfo(f).Length);
    }

    private static string Gb(long bytes) => $"{bytes / 1024d / 1024d / 1024d:0.0} GB";

    /// <summary>
    /// A stateless executor over the already-loaded weights. Cheap next to the load: it
    /// builds its own context per inference, so replacing one drops no work in progress.
    /// </summary>
    private StatelessExecutor NewExecutor(string systemMessage)
    {
        _executorSystemMessage = systemMessage;
        return new StatelessExecutor(_weights!, _parameters!)
        {
            // Off only for models that turned out not to have a template — see
            // CompleteAsync. Left on by default so each model's own format is used and this
            // code never hard-codes one.
            ApplyTemplate = !_templateUnavailable,
            SystemMessage = _templateUnavailable ? string.Empty : systemMessage,
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _executor = null;
        _weights?.Dispose();
        _weights = null;
        _loadGate.Dispose();
    }
}
