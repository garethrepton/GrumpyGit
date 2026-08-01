using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GrumpyGit.App.Services;

/// <summary>
/// Free-text notes a reviewer writes against a file, persisted per repository.
///
/// Review findings are worthless if they evaporate when you move to the next file, and
/// alt-tabbing to somewhere else to write them down is what stops people recording them
/// at all. Notes are keyed by file path only — deliberately not by commit — so a note
/// survives the file changing underneath it. A note that vanished because the author
/// pushed a fixup would be worse than a slightly stale one.
/// </summary>
public sealed class ReviewNotesStore
{
    private readonly string _path;
    private Dictionary<string, string> _notes = new(StringComparer.Ordinal);

    private static string NotesDir => AppPaths.ReviewNotesDir;

    public ReviewNotesStore(string repoPath)
    {
        var key = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(repoPath.ToLowerInvariant())))[..16];
        _path = Path.Combine(NotesDir, $"{key}.json");
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            _notes = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path))
                     ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch
        {
            // Unreadable notes must never block opening a repo.
            _notes = new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Raised when a write fails, so the UI can tell the user their notes are not being
    /// saved. Swallowing this silently would show notes on screen that are already lost.
    /// </summary>
    public event Action<string>? SaveFailed;

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(NotesDir);

            // Write to a temp file and swap, so a crash mid-write cannot leave a
            // truncated JSON file that Load() would silently discard as corrupt.
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_notes));

            if (File.Exists(_path))
                File.Replace(temp, _path, destinationBackupFileName: null);
            else
                File.Move(temp, _path);
        }
        catch (Exception ex)
        {
            SaveFailed?.Invoke($"Review notes could not be saved: {ex.Message}");
        }
    }

    public string Get(string filePath) =>
        _notes.TryGetValue(filePath, out var note) ? note : string.Empty;

    public bool Has(string filePath) =>
        _notes.TryGetValue(filePath, out var note) && !string.IsNullOrWhiteSpace(note);

    public void Set(string filePath, string? note)
    {
        if (string.IsNullOrWhiteSpace(note))
            _notes.Remove(filePath);
        else
            _notes[filePath] = note;

        Save();
    }

    /// <summary>Paths that currently carry a note, for marking them in the file list.</summary>
    public IReadOnlyCollection<string> NotedPaths =>
        _notes.Where(kv => !string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key).ToArray();

    public int Count => NotedPaths.Count;
}
