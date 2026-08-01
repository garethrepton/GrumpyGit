using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GrumpyGit.App.Services;

/// <summary>
/// Remembers which files a human has actually reviewed, per repository.
///
/// Reviewing a large agent-written session is not a single sitting, so the
/// reviewed/unreviewed marks have to survive closing the app — otherwise the
/// feature is worse than useless, because it silently loses your place.
///
/// State is keyed by (session head commit, file path). Keying on the head commit
/// means that if the agent amends or adds commits, the session's identity changes
/// and previously-reviewed files correctly revert to unreviewed — you are reviewing
/// a different set of changes at that point.
/// </summary>
public sealed class ReviewStateStore
{
    private readonly string _statePath;
    private Dictionary<string, HashSet<string>> _reviewed = new(StringComparer.Ordinal);

    private static string StateDir => AppPaths.ReviewStateDir;

    public ReviewStateStore(string repoPath)
    {
        // Repo paths cannot be used as filenames directly. Hash them so any path
        // (including UNC and paths with invalid filename characters) maps to a
        // stable, filesystem-safe key.
        var key = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(repoPath.ToLowerInvariant())))[..16];
        _statePath = Path.Combine(StateDir, $"{key}.json");
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_statePath)) return;
            var json = File.ReadAllText(_statePath);
            var raw = JsonSerializer.Deserialize<Dictionary<string, string[]>>(json);
            if (raw is null) return;
            _reviewed = raw.ToDictionary(
                kv => kv.Key,
                kv => new HashSet<string>(kv.Value, StringComparer.Ordinal),
                StringComparer.Ordinal);
        }
        catch
        {
            // Corrupt or unreadable state must never block opening a repo —
            // losing review marks is recoverable, failing to open is not.
            _reviewed = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(StateDir);
            var raw = _reviewed.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal);
            File.WriteAllText(_statePath, JsonSerializer.Serialize(raw));
        }
        catch { }
    }

    public bool IsReviewed(string sessionKey, string filePath) =>
        _reviewed.TryGetValue(sessionKey, out var files) && files.Contains(filePath);

    public void SetReviewed(string sessionKey, string filePath, bool reviewed)
    {
        if (reviewed)
        {
            if (!_reviewed.TryGetValue(sessionKey, out var files))
                _reviewed[sessionKey] = files = new HashSet<string>(StringComparer.Ordinal);
            files.Add(filePath);
        }
        else if (_reviewed.TryGetValue(sessionKey, out var files))
        {
            files.Remove(filePath);
            if (files.Count == 0)
                _reviewed.Remove(sessionKey);
        }

        Save();
    }

    public void ClearSession(string sessionKey)
    {
        if (_reviewed.Remove(sessionKey))
            Save();
    }
}
