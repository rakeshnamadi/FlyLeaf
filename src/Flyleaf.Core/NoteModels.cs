namespace Flyleaf.Core;

/// <summary>The root object of notes.json.</summary>
public sealed class NotesFile
{
    /// <summary>Schema version of the file.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Every file or folder that has a note.</summary>
    public List<NoteEntry> Entries { get; set; } = [];

    /// <summary>Finds the entry for a path, ignoring case and separator differences.</summary>
    /// <param name="path">The absolute path to look for.</param>
    /// <returns>The matching entry, or <see langword="null"/> if there is none.</returns>
    public NoteEntry? Find(string path)
    {
        var key = PathNormalizer.Normalize(path);
        return Entries.Find(entry => PathNormalizer.Normalize(entry.Path) == key);
    }

    /// <summary>Returns the entry for a path, creating an empty one if needed.</summary>
    /// <param name="path">The absolute path to register.</param>
    /// <param name="now">The time to record on a new entry.</param>
    /// <returns>The existing or newly created entry.</returns>
    public NoteEntry GetOrAdd(string path, DateTimeOffset now)
    {
        if (Find(path) is { } existing)
        {
            return existing;
        }

        var entry = new NoteEntry { Path = Path.GetFullPath(path), LastModified = now };
        Entries.Add(entry);
        return entry;
    }

    /// <summary>Sets a new description, moving the previous one into the entry's history.</summary>
    /// <param name="entry">The entry to update.</param>
    /// <param name="description">The new description.</param>
    /// <param name="now">The time to record for the change.</param>
    /// <returns><see langword="true"/> if the description changed.</returns>
    public static bool UpdateDescription(NoteEntry entry, string description, DateTimeOffset now)
    {
        var normalized = description.ReplaceLineEndings("\n");
        if (normalized == entry.Description)
        {
            return false;
        }

        if (entry.Description.Length > 0)
        {
            entry.History.Insert(0, new NoteHistoryItem { Text = entry.Description, Timestamp = entry.LastModified });
        }

        entry.Description = normalized;
        entry.LastModified = now;
        return true;
    }
}

/// <summary>A note attached to one file or folder. Only the path is stored, never file contents.</summary>
public sealed class NoteEntry
{
    /// <summary>The absolute path, in its original casing for display.</summary>
    public string Path { get; set; } = "";

    /// <summary>The current note, shown as the Explorer tooltip.</summary>
    public string Description { get; set; } = "";

    /// <summary>When the current note was saved.</summary>
    public DateTimeOffset LastModified { get; set; }

    /// <summary>Previous notes, newest first.</summary>
    public List<NoteHistoryItem> History { get; set; } = [];
}

/// <summary>A previous version of a note.</summary>
public sealed class NoteHistoryItem
{
    /// <summary>The text of the note at that time.</summary>
    public string Text { get; set; } = "";

    /// <summary>When that text was saved.</summary>
    public DateTimeOffset Timestamp { get; set; }
}
