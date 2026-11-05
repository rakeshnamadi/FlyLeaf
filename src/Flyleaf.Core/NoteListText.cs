namespace Flyleaf.Core;

/// <summary>Bullet-list formatting for note text. Bullets are plain characters, so notes.json stays readable.</summary>
public static class NoteListText
{
    /// <summary>The text that starts every list item.</summary>
    public const string Bullet = "• ";

    /// <summary>Adds a bullet to every line, or removes them if every non-empty line already has one.</summary>
    /// <param name="block">One or more whole lines, separated by \r\n or \n.</param>
    /// <returns>The reformatted lines, separated by \r\n.</returns>
    public static string ToggleBullets(string block)
    {
        var lines = block.ReplaceLineEndings("\n").Split('\n');
        var nonEmpty = lines.Where(line => line.Trim().Length > 0).ToList();
        var removing = nonEmpty.Count > 0 && nonEmpty.All(IsBullet);

        var result = lines.Select(line =>
        {
            if (removing)
            {
                return IsBullet(line) ? line[Bullet.Length..] : line;
            }

            // A blank line inside a selection stays blank, but a single empty line becomes a new list item.
            return IsBullet(line) || (lines.Length > 1 && line.Trim().Length == 0) ? line : Bullet + line;
        });

        return string.Join("\r\n", result);
    }

    /// <summary>Tells whether a line is a list item.</summary>
    /// <param name="line">The line, without its line ending.</param>
    /// <returns><see langword="true"/> if the line starts with a bullet.</returns>
    public static bool IsBullet(string line) => line.StartsWith(Bullet, StringComparison.Ordinal);

    /// <summary>Tells whether a line is a list item with nothing typed in it yet.</summary>
    /// <param name="line">The line, without its line ending.</param>
    /// <returns><see langword="true"/> if the line is only a bullet.</returns>
    public static bool IsEmptyBullet(string line) => line.Trim() == Bullet.Trim();
}
