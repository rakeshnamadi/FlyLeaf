namespace Flyleaf.Core;

/// <summary>
/// Finds the notes for an item shown in a folder window. Explorer reports only the item's display name,
/// which has no extension when "Hide extensions for known file types" is on, so a name can match several notes.
/// </summary>
public sealed class NoteLookup
{
    private readonly Dictionary<string, List<NoteEntry>> _byFolder = [];

    /// <summary>Indexes the notes by the folder that contains each item.</summary>
    /// <param name="notes">The notes to index.</param>
    public NoteLookup(NotesFile notes)
    {
        foreach (var entry in notes.Entries.Where(entry => !string.IsNullOrWhiteSpace(entry.Description) && !string.IsNullOrWhiteSpace(entry.Path)))
        {
            var folder = Path.GetDirectoryName(PathNormalizer.Normalize(entry.Path));
            if (folder is null)
            {
                continue;
            }

            if (!_byFolder.TryGetValue(folder, out var entries))
            {
                _byFolder[folder] = entries = [];
            }

            entries.Add(entry);
        }
    }

    /// <summary>Finds the notes to show for an item.</summary>
    /// <param name="folder">The folder the item is in.</param>
    /// <param name="displayName">The name Explorer shows for the item.</param>
    /// <returns>The exact match if there is one, otherwise every note whose name matches without its extension. Empty if none match.</returns>
    public IReadOnlyList<NoteEntry> Find(string folder, string displayName)
    {
        if (!_byFolder.TryGetValue(PathNormalizer.Normalize(folder), out var entries))
        {
            return [];
        }

        var exact = entries.Find(entry => Path.GetFileName(entry.Path).Equals(displayName, StringComparison.OrdinalIgnoreCase));
        return exact is not null
            ? [exact]
            : entries.FindAll(entry => Path.GetFileNameWithoutExtension(entry.Path).Equals(displayName, StringComparison.OrdinalIgnoreCase));
    }
}
