using System.IO.Pipes;
using System.Security.Principal;
using Flyleaf.Core;

namespace FlyShell;

/// <summary>Asks FlyWatcher for the note on a path.</summary>
internal static class NoteClient
{
    // Explorer will not wait longer than this for a tooltip.
    private const int TimeoutMilliseconds = 80;

    /// <summary>
    /// Fetches a note. COM requires a synchronous answer, so this waits on the async pipe calls,
    /// which is safe because they are bounded by the timeout and never resume on this thread.
    /// </summary>
    /// <param name="path">The absolute path.</param>
    /// <returns>The note, or <see langword="null"/> if there is none or FlyWatcher did not answer in time.</returns>
    public static string? TryGetNote(string path)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeoutMilliseconds);
            return QueryAsync(path, timeout.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static async Task<string?> QueryAsync(string path, CancellationToken cancellationToken)
    {
        // Anonymous: the service has no need to act as the user, so don't give it the ability.
        await using var pipe = new NamedPipeClientStream(
            ".", PipeProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous);

        await pipe.ConnectAsync(TimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
        await pipe.WriteAsync(PipeProtocol.EncodeLine(path), cancellationToken).ConfigureAwait(false);
        await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);

        var buffer = new byte[4096];
        var length = 0;
        while (length < PipeProtocol.MaxLineBytes)
        {
            if (length == buffer.Length)
            {
                Array.Resize(ref buffer, buffer.Length * 2);
            }

            var read = await pipe.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            var newline = Array.IndexOf(buffer, (byte)'\n', length, read);
            if (newline >= 0)
            {
                return PipeProtocol.DecodeLine(buffer.AsSpan(0, newline));
            }

            length += read;
        }

        return null;
    }
}
