using System.Text;

namespace Flyleaf.Core;

/// <summary>
/// The line-based contract between FlyShell and FlyWatcher. Each side sends one UTF-8 line ending in \n.
/// Line breaks inside a note are escaped so that a multi-line note still travels as one line.
/// </summary>
public static class PipeProtocol
{
    /// <summary>The name of the pipe, without the \\.\pipe\ prefix.</summary>
    public const string PipeName = "FlyWatcherPipe";

    /// <summary>The largest request or reply line, in bytes, that either side will accept.</summary>
    public const int MaxLineBytes = 256 * 1024;

    /// <summary>Encodes text as an escaped line ready to write to the pipe.</summary>
    /// <param name="text">The text to send. May contain line breaks.</param>
    /// <returns>UTF-8 bytes ending in a newline.</returns>
    public static byte[] EncodeLine(string text)
    {
        var escaped = text.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n");
        return Encoding.UTF8.GetBytes(escaped + "\n");
    }

    /// <summary>Decodes the bytes of a received line, not including its newline.</summary>
    /// <param name="line">The UTF-8 bytes of the line.</param>
    /// <returns>The original text with its line breaks restored.</returns>
    public static string DecodeLine(ReadOnlySpan<byte> line)
    {
        var text = Encoding.UTF8.GetString(line);
        if (!text.Contains('\\'))
        {
            return text;
        }

        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\' || i == text.Length - 1)
            {
                result.Append(text[i]);
                continue;
            }

            i++;
            result.Append(text[i] switch
            {
                'n' => '\n',
                'r' => '\r',
                _ => text[i],
            });
        }

        return result.ToString();
    }
}
