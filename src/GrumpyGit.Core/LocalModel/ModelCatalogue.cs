namespace GrumpyGit.Core.LocalModel;

/// <summary>
/// One file of a model. Most models are a single part; the largest are published as a
/// numbered set of shards, which llama.cpp reassembles when it opens the first one.
/// </summary>
/// <param name="Url">
/// Where it comes from. Fixed at compile time and never composed from user input — the
/// application downloads from this list or not at all.
/// </param>
/// <param name="FileName">Name on disk. Also the last segment of the URL.</param>
/// <param name="SizeBytes">Published size, used for the progress bar and a sanity check.</param>
/// <param name="Sha256">
/// Published SHA-256 of this part's contents. The download is verified against this before
/// it is accepted, so a truncated transfer or a substituted file is caught here rather
/// than by llama.cpp parsing something unexpected.
/// </param>
public sealed record ModelPart(string Url, string FileName, long SizeBytes, string Sha256);

/// <summary>
/// How a model's turns have to be marked up when its GGUF does not say.
///
/// Almost every model carries its own chat template in the file, and that is the only one
/// this application wants to use — hard-coding formats is how a client ends up subtly
/// mis-prompting every model it did not anticipate. This enum exists for the exception:
/// Google's QAT builds of Gemma 4 ship with no <c>tokenizer.chat_template</c> at all, so
/// there is nothing to read and the choice is between stating the format and shipping a
/// model that loads and then answers badly.
/// </summary>
public enum ChatFormat
{
    /// <summary>Read it from the GGUF. Correct for everything that has one.</summary>
    FromModel,

    /// <summary>
    /// Gemma's turn markers. Gemma has no system role, so the standing instruction is
    /// folded into the first user turn — that is the format's own convention, not a
    /// shortcut.
    /// </summary>
    Gemma,
}

/// <summary>
/// One model this application will offer to fetch.
/// </summary>
/// <param name="Name">What the user sees.</param>
/// <param name="Parts">
/// Every file that must be on disk before the model will load, in published order.
/// </param>
/// <param name="Summary">One line on the trade-off, for choosing between them.</param>
public sealed record ModelOption(
    string Name,
    IReadOnlyList<ModelPart> Parts,
    string Summary,
    ChatFormat ChatFormat = ChatFormat.FromModel)
{
    public static ModelOption Single(
        string name, string url, string fileName, long sizeBytes, string sha256, string summary,
        ChatFormat chatFormat = ChatFormat.FromModel) =>
        new(name, [new ModelPart(url, fileName, sizeBytes, sha256)], summary, chatFormat);

    /// <summary>
    /// The catalogue entry whose file this path is, or null for a GGUF the user supplied
    /// themselves. Matched on file name because that is what the downloader writes and what
    /// the setting then points at.
    /// </summary>
    public static ModelOption? ForPath(string? modelPath) =>
        string.IsNullOrWhiteSpace(modelPath)
            ? null
            : ModelCatalogue.All.FirstOrDefault(m => string.Equals(
                m.FileName, Path.GetFileName(modelPath), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The file handed to llama.cpp. For a sharded model that is the first shard: llama.cpp
    /// reads the <c>split.count</c> metadata inside it and opens the siblings itself, which
    /// is why every part has to land in one directory under its published name.
    /// </summary>
    public string FileName => Parts[0].FileName;

    /// <summary>First part's URL — the one the outbound-surface test reads.</summary>
    public string Url => Parts[0].Url;

    public long SizeBytes => Parts.Sum(p => p.SizeBytes);

    public string SizeLabel => $"{SizeBytes / 1024d / 1024d / 1024d:0.0} GB";
}

/// <summary>
/// The models the download offer covers.
///
/// Deliberately a short, hard-coded list rather than a search over a model host. A fixed
/// list is a fixed outbound surface: a handful of URLs on one host, every one published by
/// the model's own vendor, every one with a published hash verified after transfer.
/// Anything broader would mean this client fetching and executing whatever a remote index
/// happened to name that day. Community re-quantisations are deliberately excluded even
/// where they are more popular — an official repository is the only provenance this list
/// is willing to assert.
///
/// Everything here is Apache-2.0 and runs on a CPU, though the last two only in the sense
/// that they will finish eventually. A user who wants something else points the setting at
/// their own file; that path is unchanged and remains the one that needs no network at all.
/// </summary>
public static class ModelCatalogue
{
    private const string QwenCoder = "https://huggingface.co/Qwen/Qwen2.5-Coder-";
    private const string Qwen3 = "https://huggingface.co/Qwen/Qwen3-";
    private const string Gemma4 = "https://huggingface.co/google/gemma-4-";

    public static readonly ModelOption QwenCoder05B = ModelOption.Single(
        name: "Qwen2.5-Coder 0.5B Instruct (Q4_K_M)",
        url: $"{QwenCoder}0.5B-Instruct-GGUF/resolve/main/qwen2.5-coder-0.5b-instruct-q4_k_m.gguf",
        fileName: "qwen2.5-coder-0.5b-instruct-q4_k_m.gguf",
        sizeBytes: 491_400_064,
        sha256: "1d9614638d18024d0fbb36575a15f1302a3adf044df10345688ec4f6e1c4ff32",
        summary: "Tiny and quick anywhere, including on a laptop with no GPU. Expect shallow readings — it will describe a change more often than judge it.");

    public static readonly ModelOption QwenCoder15B = ModelOption.Single(
        name: "Qwen2.5-Coder 1.5B Instruct (Q4_K_M)",
        url: $"{QwenCoder}1.5B-Instruct-GGUF/resolve/main/qwen2.5-coder-1.5b-instruct-q4_k_m.gguf",
        fileName: "qwen2.5-coder-1.5b-instruct-q4_k_m.gguf",
        sizeBytes: 1_117_320_768,
        sha256: "cc324af070c2ecbfd324a30884d2f951a7ff756aba85cb811a6ec436933bb046",
        summary: "The safe default. Usable on CPU alone, comfortable on any GPU. Measured at 31 tok/s on four CPU threads and 124 on an RTX 4080.");

    public static readonly ModelOption QwenCoder3B = ModelOption.Single(
        name: "Qwen2.5-Coder 3B Instruct (Q4_K_M)",
        url: $"{QwenCoder}3B-Instruct-GGUF/resolve/main/qwen2.5-coder-3b-instruct-q4_k_m.gguf",
        fileName: "qwen2.5-coder-3b-instruct-q4_k_m.gguf",
        sizeBytes: 2_104_932_800,
        sha256: "724fb256bec1ff062b2f65e4569e871ad2e95ab2a3989723d1769c54294730b7",
        summary: "Noticeably better judgement than the 1.5B. Still fine on CPU if you are patient; quick on any GPU with 4 GB.");

    public static readonly ModelOption Qwen34B = ModelOption.Single(
        name: "Qwen3 4B (Q4_K_M)",
        url: $"{Qwen3}4B-GGUF/resolve/main/Qwen3-4B-Q4_K_M.gguf",
        fileName: "Qwen3-4B-Q4_K_M.gguf",
        sizeBytes: 2_497_280_256,
        sha256: "7485fe6f11af29433bc51cab58009521f205840f5b4ae3a32fa7f92e8534fdf5",
        summary: "A generation newer than the 3B and reasons about a change rather than describing it. Not a code-only model, which shows on exotic syntax.");

    public static readonly ModelOption QwenCoder7B = ModelOption.Single(
        name: "Qwen2.5-Coder 7B Instruct (Q4_K_M)",
        url: $"{QwenCoder}7B-Instruct-GGUF/resolve/main/qwen2.5-coder-7b-instruct-q4_k_m.gguf",
        fileName: "qwen2.5-coder-7b-instruct-q4_k_m.gguf",
        sizeBytes: 4_683_073_536,
        sha256: "509287f78cb4d4cf6b3843734733b914b2c158e43e22a7f4bf5e963800894d3c",
        summary: "Where the reviews start being worth reading on their own. Wants a GPU with 6 GB or more; painful on CPU.");

    public static readonly ModelOption Qwen38B = ModelOption.Single(
        name: "Qwen3 8B (Q4_K_M)",
        url: $"{Qwen3}8B-GGUF/resolve/main/Qwen3-8B-Q4_K_M.gguf",
        fileName: "Qwen3-8B-Q4_K_M.gguf",
        sizeBytes: 5_027_783_488,
        sha256: "d98cdcbd03e17ce47681435b5150e34c1417f50b5c0019dd560e4882c5745785",
        summary: "The best of the dense models that still fits an 8 GB card. Reads intent well; occasionally argues with itself before answering.");

    /// <summary>
    /// Google's own quantisation-aware-trained build, not a re-quantisation of the release
    /// weights. That is the reason to prefer these at 4-bit: the model was trained expecting
    /// the precision it ships at, so it loses noticeably less than a post-hoc q4 of the
    /// same size would.
    ///
    /// The vision projector published alongside each one is deliberately not fetched. This
    /// application shows a model text diffs and nothing else, and a multimodal encoder it
    /// will never call is a gigabyte of somebody's disk spent on nothing.
    /// </summary>
    public static readonly ModelOption Gemma4E4B = ModelOption.Single(
        name: "Gemma 4 E4B Instruct (QAT q4_0)",
        url: $"{Gemma4}E4B-it-qat-q4_0-gguf/resolve/main/gemma-4-E4B_q4_0-it.gguf",
        fileName: "gemma-4-E4B_q4_0-it.gguf",
        sizeBytes: 5_154_941_280,
        sha256: "676c35070db6dbe52f93e9c864ee0fba4eddea94b9c875d9cb10daff453fbaee",
        summary: "Reads prose better than the Qwen models of its size and writes a shorter, plainer summary. Weaker on unusual syntax — a reviewer more than a coder.",
        chatFormat: ChatFormat.Gemma);

    public static readonly ModelOption Gemma412B = ModelOption.Single(
        name: "Gemma 4 12B Instruct (QAT q4_0)",
        url: $"{Gemma4}12B-it-qat-q4_0-gguf/resolve/main/gemma-4-12b-it-qat-q4_0.gguf",
        fileName: "gemma-4-12b-it-qat-q4_0.gguf",
        sizeBytes: 6_975_879_296,
        sha256: "93567e57a8fe10b23569b9d9ec38cd005deedf71e29477c421a4b83f418a538b",
        summary: "The best all-round choice that still fits an 8 GB card. Explains a change in a way you would want to read; slightly behind Qwen-Coder at spotting a bug.",
        chatFormat: ChatFormat.Gemma);

    public static readonly ModelOption QwenCoder14B = ModelOption.Single(
        name: "Qwen2.5-Coder 14B Instruct (Q4_K_M)",
        url: $"{QwenCoder}14B-Instruct-GGUF/resolve/main/qwen2.5-coder-14b-instruct-q4_k_m.gguf",
        fileName: "qwen2.5-coder-14b-instruct-q4_k_m.gguf",
        sizeBytes: 8_988_110_272,
        sha256: "c1e659736d89ac1065fb495330fb824d94001974a4bfa78e7270e43476a8d940",
        summary: "The best dense code model here at spotting a real mistake. Needs a GPU with 12 GB or more — on anything less it will run, slowly, from system memory.");

    /// <summary>
    /// Mixture-of-experts: 30B of weights, 3B of them active per token. That is the whole
    /// point of it here — it reads like a model far larger than the 14B while generating at
    /// roughly the speed of a 3B, including on a CPU. The catch is memory rather than
    /// compute: all 30B have to be resident, so this wants ~20 GB of RAM or VRAM free.
    /// </summary>
    /// <summary>
    /// Mixture-of-experts: 26B of weights, 4B active per token. Same bargain as the Qwen3
    /// MoE below — reads like something much larger while generating at small-model speed —
    /// but at roughly three-quarters the memory, which is the difference between fitting a
    /// 32 GB machine and not.
    /// </summary>
    public static readonly ModelOption Gemma426BA4B = ModelOption.Single(
        name: "Gemma 4 26B-A4B MoE (QAT q4_0)",
        url: $"{Gemma4}26B-A4B-it-qat-q4_0-gguf/resolve/main/gemma-4-26B_q4_0-it.gguf",
        fileName: "gemma-4-26B_q4_0-it.gguf",
        sizeBytes: 14_439_363_584,
        sha256: "3eca3b8f6d7baf218a7dd6bba5fb59a56ee25fe2d567b6f5f589b4f697eca51d",
        summary: "Mixture-of-experts: 4B active per token, so it generates at small-model speed. The best explanations in this list, and it fits a 32 GB machine — unlike the two below it.",
        chatFormat: ChatFormat.Gemma);

    public static readonly ModelOption Qwen330BA3B = ModelOption.Single(
        name: "Qwen3 30B-A3B MoE (Q4_K_M)",
        url: $"{Qwen3}30B-A3B-GGUF/resolve/main/Qwen3-30B-A3B-Q4_K_M.gguf",
        fileName: "Qwen3-30B-A3B-Q4_K_M.gguf",
        sizeBytes: 18_556_685_824,
        sha256: "0d003f6662faee786ed5da3e31b29c978de5ae5d275c8794c606a7f3c01aa8f5",
        summary: "Mixture-of-experts: 3B active per token, so it generates at small-model speed even on CPU. Wants about 20 GB of memory free — that is the real cost, not the compute.");

    /// <summary>
    /// The largest thing this list will offer, and the only sharded one. Four files and
    /// roughly 48 GB on disk, which is why it sits alone at the bottom of the ladder rather
    /// than being anybody's default: on a machine that cannot hold it in memory llama.cpp
    /// will page it off disk and a single file review becomes a coffee break.
    /// </summary>
    public static readonly ModelOption Qwen3CoderNext = new(
        Name: "Qwen3-Coder-Next MoE (Q4_K_M, 4 files)",
        Parts:
        [
            new ModelPart(
                $"{Qwen3}Coder-Next-GGUF/resolve/main/Qwen3-Coder-Next-Q4_K_M/Qwen3-Coder-Next-Q4_K_M-00001-of-00004.gguf",
                "Qwen3-Coder-Next-Q4_K_M-00001-of-00004.gguf",
                15_524_827_040,
                "6bcfc9f9c37901eeb92172e2ab871224dab36a453d263bcb2547f737409534da"),
            new ModelPart(
                $"{Qwen3}Coder-Next-GGUF/resolve/main/Qwen3-Coder-Next-Q4_K_M/Qwen3-Coder-Next-Q4_K_M-00002-of-00004.gguf",
                "Qwen3-Coder-Next-Q4_K_M-00002-of-00004.gguf",
                14_872_168_352,
                "817def0691ee9d08bf3dc4444be7aed29c9e52091e8fa9d97901ce7e7f6f01d3"),
            new ModelPart(
                $"{Qwen3}Coder-Next-GGUF/resolve/main/Qwen3-Coder-Next-Q4_K_M/Qwen3-Coder-Next-Q4_K_M-00003-of-00004.gguf",
                "Qwen3-Coder-Next-Q4_K_M-00003-of-00004.gguf",
                14_503_294_496,
                "23aa634d47dca9b4ca3ea249384e6f01951b24c83cdc076f37f6f43d6c99883f"),
            new ModelPart(
                $"{Qwen3}Coder-Next-GGUF/resolve/main/Qwen3-Coder-Next-Q4_K_M/Qwen3-Coder-Next-Q4_K_M-00004-of-00004.gguf",
                "Qwen3-Coder-Next-Q4_K_M-00004-of-00004.gguf",
                3_510_702_144,
                "249c768cc5f130dc731567d6edcbdacc48e14dec9e02c5dbe2b2185d2c5bdb2b"),
        ],
        Summary: "The best reviews this list can produce, and by far the most machine. Four files, 48 GB on disk, and it needs most of that in memory — a workstation model, not a laptop one.");

    /// <summary>Smallest first, so the list reads as a ladder rather than a menu.</summary>
    public static IReadOnlyList<ModelOption> All { get; } =
    [
        QwenCoder05B,
        QwenCoder15B,
        QwenCoder3B,
        Qwen34B,
        QwenCoder7B,
        Qwen38B,
        Gemma4E4B,
        Gemma412B,
        QwenCoder14B,
        Gemma426BA4B,
        Qwen330BA3B,
        Qwen3CoderNext,
    ];
}
