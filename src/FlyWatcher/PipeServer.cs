using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Flyleaf.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlyWatcher;

/// <summary>Answers path lookups from the shell extension over a named pipe.</summary>
/// <param name="options">The service settings.</param>
/// <param name="index">The index to answer from.</param>
/// <param name="logger">The logger.</param>
public sealed class PipeServer(WatcherOptions options, NotesIndex index, ILogger<PipeServer> logger) : BackgroundService
{
    // Each worker keeps one pipe instance waiting, so there is no gap in which a client finds the pipe busy.
    private const int WorkerCount = 8;
    private const int BufferSize = 4096;

    // A client that stalls gives up its worker after this long, so it cannot starve the others.
    private static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ErrorDelay = TimeSpan.FromSeconds(1);

    /// <inheritdoc />
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, WorkerCount).Select(_ => Task.Run(() => RunWorkerAsync(stoppingToken), stoppingToken)));

    private async Task RunWorkerAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = CreatePipe();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                // Most likely another copy of the service already owns the pipe.
                logger.LogError(ex, "Could not create the pipe {PipeName}.", options.PipeName);
                await DelayAfterErrorAsync(stoppingToken);
                continue;
            }

            await using (pipe)
            {
                try
                {
                    await pipe.WaitForConnectionAsync(stoppingToken);

                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    timeout.CancelAfter(ClientTimeout);
                    await HandleClientAsync(pipe, timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    // Shutting down, or the client stalled. The loop condition decides which.
                }
                catch (IOException ex)
                {
                    // Usually a client that went away, which happens whenever Explorer gives up on a tooltip.
                    logger.LogDebug(ex, "A pipe client disconnected early.");
                }
            }
        }
    }

    private NamedPipeServerStream CreatePipe()
    {
        // Only the signed-in user may connect: notes can be private, and the default pipe permissions
        // would both let other accounts read them and block the user's Explorer from writing a request.
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(options.AllowedUser, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            options.PipeName,
            PipeDirection.InOut,
            WorkerCount,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            BufferSize,
            BufferSize,
            security);
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        var path = await ReadLineAsync(pipe, cancellationToken);
        if (path is null)
        {
            return;
        }

        await pipe.WriteAsync(PipeProtocol.EncodeLine(index.Lookup(path)), cancellationToken);
        await pipe.FlushAsync(cancellationToken);

        // Closing now could discard a reply the client has not read yet, so wait for the client to hang up.
        var ignored = new byte[1];
        _ = await pipe.ReadAsync(ignored, cancellationToken);
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];
        var length = 0;

        while (length < PipeProtocol.MaxLineBytes)
        {
            if (length == buffer.Length)
            {
                Array.Resize(ref buffer, buffer.Length * 2);
            }

            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken);
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

    private static async Task DelayAfterErrorAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(ErrorDelay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }
}
