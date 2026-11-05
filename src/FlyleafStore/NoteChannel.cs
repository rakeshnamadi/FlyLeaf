using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace Flyleaf;

/// <summary>
/// Passes a path from a new launch (started by the Explorer menu) to the copy already running.
/// The pipe accepts only this user's processes in this sign-in session.
/// </summary>
internal sealed class NoteChannel : IDisposable
{
    private const int ConnectTimeoutMilliseconds = 2000;
    private const int ASFW_ANY = -1;

    private static readonly string PipeName = $"Flyleaf.Note.{Environment.UserName}.{Environment.ProcessId switch { _ => System.Diagnostics.Process.GetCurrentProcess().SessionId }}";

    private readonly CancellationTokenSource _stop = new();
    private readonly Task _listening;

    /// <summary>Starts listening.</summary>
    /// <param name="onPath">Called on the calling thread's synchronization context for each path received.</param>
    public NoteChannel(Func<string, Task> onPath)
    {
        var context = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _listening = Task.Run(() => ListenAsync(path => context.Post(_ => _ = onPath(path), null), _stop.Token));
    }

    /// <summary>Sends a path to the running copy and lets it come to the front.</summary>
    /// <param name="path">The full path of the file or folder.</param>
    /// <returns><see langword="true"/> if the running copy received it.</returns>
    public static bool TrySend(string path)
    {
        try
        {
            // Only the process Explorer just started may bring a window forward, so pass that right along.
            AllowSetForegroundWindow(ASFW_ANY);

            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(ConnectTimeoutMilliseconds);
            using var writer = new StreamWriter(pipe);
            writer.WriteLine(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task ListenAsync(Action<string> onPath, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken);

                using var reader = new StreamReader(pipe);
                var path = await reader.ReadLineAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path))
                {
                    onPath(path);
                }
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }
            catch (IOException)
            {
                // A sender that went away, or the pipe is briefly in use. Keep listening.
                await Task.Delay(100, CancellationToken.None);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stop.Cancel();
        _listening.Wait(TimeSpan.FromSeconds(1));
        _stop.Dispose();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
