using System.Threading.Channels;
using Flyleaf.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FlyWatcher;

/// <summary>Keeps <see cref="NotesIndex"/> in step with notes.json using a file watcher, without polling.</summary>
/// <param name="options">The service settings.</param>
/// <param name="index">The index to keep up to date.</param>
/// <param name="logger">The logger.</param>
public sealed class WatcherService(WatcherOptions options, NotesIndex index, ILogger<WatcherService> logger) : BackgroundService
{
    // One save raises several file events, so wait for them to settle before reloading.
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan WatcherRetryDelay = TimeSpan.FromSeconds(30);

    private readonly NotesRepository _repository = new(options.NotesFilePath);
    private readonly Channel<bool> _changes = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var watcher = await CreateWatcherAsync(stoppingToken);
            await ReloadAsync(stoppingToken);

            await foreach (var _ in _changes.Reader.ReadAllAsync(stoppingToken))
            {
                await Task.Delay(SettleDelay, stoppingToken);
                _changes.Reader.TryRead(out var _);
                await ReloadAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task<FileSystemWatcher> CreateWatcherAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                var directory = Path.GetDirectoryName(options.NotesFilePath)!;
                Directory.CreateDirectory(directory);

                var watcher = new FileSystemWatcher(directory, Path.GetFileName(options.NotesFilePath))
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                };
                watcher.Changed += (_, _) => _changes.Writer.TryWrite(true);
                watcher.Created += (_, _) => _changes.Writer.TryWrite(true);
                watcher.Deleted += (_, _) => _changes.Writer.TryWrite(true);
                watcher.Renamed += (_, _) => _changes.Writer.TryWrite(true);
                watcher.Error += (_, e) =>
                {
                    // The event buffer overflowed, so a change may have been missed.
                    logger.LogWarning(e.GetException(), "The file watcher reported an error. Reloading notes.");
                    _changes.Writer.TryWrite(true);
                };
                watcher.EnableRaisingEvents = true;
                return watcher;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                logger.LogError(ex, "Cannot watch {NotesFile}. Retrying in {Delay}.", options.NotesFilePath, WatcherRetryDelay);
                await Task.Delay(WatcherRetryDelay, cancellationToken);
            }
        }
    }

    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var notes = await _repository.LoadAsync(cancellationToken);
            index.Replace(notes);
            logger.LogInformation("Loaded {Count} notes from {NotesFile}.", index.Count, options.NotesFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotesFileCorruptException or ArgumentException)
        {
            // Keep serving the last good notes until the file is readable again.
            logger.LogError(ex, "Cannot load {NotesFile}. Keeping the previous notes.", options.NotesFilePath);
        }
    }
}
