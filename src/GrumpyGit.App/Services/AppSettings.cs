using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using GrumpyGit.Core.Agents;

namespace GrumpyGit.App.Services;

public class AppSettings
{
    public string Theme { get; set; } = "dark";
    public int TerminalFontSize { get; set; } = 13;
    public int DiffContextLines { get; set; } = 3;
    public int AutoFetchIntervalSeconds { get; set; } = 0;
    public string DefaultRemote { get; set; } = "origin";
    public string[] RecentRepositories { get; set; } = [];
    public int MaxRecentRepos { get; set; } = 10;

    /// <summary>
    /// Which review module is in use, by <see cref="ReviewModuleId"/> name. Empty means the
    /// feature is off and the client is exactly the git client it was without it.
    ///
    /// Only the choice is stored. No prompt, no diff and no answer is ever written down,
    /// whichever module is picked (commandment 9).
    /// </summary>
    public string ReviewModule { get; set; } = string.Empty;

    /// <summary>
    /// Set once the user has been asked which module they want — including when they
    /// answered "none". Drives whether the first-run picker appears, and it is a separate
    /// flag from <see cref="ReviewModule"/> precisely so that "chose nothing" and "has not
    /// been asked" stay distinguishable: the first must never be nagged again.
    /// </summary>
    public bool ReviewModuleChosen { get; set; }

    /// <summary>
    /// A GGUF model file the user already has, used by the local module. Empty means no
    /// model is configured. Only the path is stored — never a prompt, a diff or an answer.
    /// </summary>
    public string LocalModelPath { get; set; } = string.Empty;

    /// <summary>
    /// Set when the user turns down the offer to fetch a model. The offer is then never
    /// shown again — an invitation that returns every session is nagging, not helpfulness.
    /// Settings still has the button for anyone who changes their mind.
    /// </summary>
    public bool LocalModelOfferDeclined { get; set; }

    /// <summary>
    /// Reads the chosen module, carrying forward a settings file written before modules
    /// existed: a configured GGUF path meant the local model, and that user should not be
    /// asked again for something they already answered.
    /// </summary>
    public ReviewModuleId ResolveReviewModule()
    {
        var chosen = ReviewModuleCatalogue.Parse(ReviewModule);
        if (chosen != ReviewModuleId.None || ReviewModuleChosen)
            return chosen;

        return LocalModelPath.Length > 0 ? ReviewModuleId.Local : ReviewModuleId.None;
    }

    /// <summary>True when the first-run picker still has a question to ask.</summary>
    public bool NeedsModuleChoice =>
        !ReviewModuleChosen
        && ReviewModuleCatalogue.Parse(ReviewModule) == ReviewModuleId.None
        && LocalModelPath.Length == 0
        && !LocalModelOfferDeclined;

    /// <summary>
    /// Repositories that were open as tabs when the app last closed, restored on
    /// startup so a multi-repo workspace survives a restart.
    /// </summary>
    public string[] OpenRepositories { get; set; } = [];

    /// <summary>Which of <see cref="OpenRepositories"/> was focused, so it reopens focused.</summary>
    public string ActiveRepository { get; set; } = string.Empty;

    // Resolved through AppPaths so the rename from "GrumpyGit" to "Grumpy" carries
    // existing settings across instead of silently starting from defaults.
    private static string SettingsDir => AppPaths.Root;

    private static string SettingsPath => AppPaths.SettingsFile;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch { }
    }

    /// <summary>
    /// Records the current tab set. Called on every tab open/close so an unclean
    /// shutdown still leaves the workspace recoverable.
    /// </summary>
    public void SaveOpenRepos(System.Collections.Generic.IEnumerable<string> openPaths, string activePath)
    {
        OpenRepositories = openPaths.ToArray();
        ActiveRepository = activePath;
        Save();
    }

    public void AddRecentRepo(string path)
    {
        var list = new System.Collections.Generic.List<string>(RecentRepositories);
        list.Remove(path);
        list.Insert(0, path);
        if (list.Count > MaxRecentRepos)
            list.RemoveRange(MaxRecentRepos, list.Count - MaxRecentRepos);
        RecentRepositories = list.ToArray();
        Save();
    }
}
