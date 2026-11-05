using Flyleaf.Core;

namespace FlyWatcher;

/// <summary>
/// The in-memory lookup table. A reload builds a new dictionary and swaps it in with one reference
/// assignment, so lookups never take a lock and never see a partly built table.
/// </summary>
public sealed class NotesIndex
{
    private volatile Dictionary<string, NoteEntry> _entries = [];

    /// <summary>The number of entries currently indexed.</summary>
    public int Count => _entries.Count;

    /// <summary>Replaces the whole index with the contents of a notes file.</summary>
    /// <param name="notes">The notes to index.</param>
    public void Replace(NotesFile notes)
    {
        var entries = new Dictionary<string, NoteEntry>(notes.Entries.Count);
        foreach (var entry in notes.Entries)
        {
            if (!string.IsNullOrWhiteSpace(entry.Path))
            {
                entries[PathNormalizer.Normalize(entry.Path)] = entry;
            }
        }

        _entries = entries;
    }

    /// <summary>Looks up the current note for a path.</summary>
    /// <param name="path">The absolute path, in any casing.</param>
    /// <returns>The note, or an empty string if there is none or the path is invalid.</returns>
    public string Lookup(string path)
    {
        try
        {
            return _entries.TryGetValue(PathNormalizer.Normalize(path), out var entry) ? entry.Description : "";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return "";
        }
    }
}
