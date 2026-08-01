using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

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
