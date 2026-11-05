using System.Runtime.InteropServices;
using Flyleaf.Core;

namespace Flyleaf;

/// <summary>
/// Shows a note when the cursor rests on a noted item in File Explorer. All of this stays on this PC:
/// the name under the cursor is only compared with the local notes and is never stored or sent anywhere.
/// </summary>
internal sealed class HoverWatcher : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan HoverDelay = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan MaxDisplayTime = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReloadDelay = TimeSpan.FromMilliseconds(100);

    private readonly NotesRepository _repository = new(NotesRepository.DefaultFilePath);
    private readonly ManualResetEvent _stop = new(false);
    private readonly SynchronizationContext _uiContext;
    private readonly NoteTooltip _tooltip = new();
    private readonly Thread _thread;
    private readonly FileSystemWatcher? _fileWatcher;
    private readonly System.Threading.Timer _reloadTimer;
    private volatile NoteLookup _lookup = new(new NotesFile());

    /// <summary>Starts watching. Must be created on the UI thread.</summary>
    public HoverWatcher()
    {
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _ = _tooltip.Handle;

        _reloadTimer = new System.Threading.Timer(_ => _ = ReloadAsync());
        _fileWatcher = CreateFileWatcher();
        _reloadTimer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);

        // COM objects from the Shell need an STA thread, and UI Automation calls mustn't run on the UI thread.
        _thread = new Thread(Run) { IsBackground = true, Name = "Flyleaf hover" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private FileSystemWatcher? CreateFileWatcher()
    {
        try
        {
            var directory = Path.GetDirectoryName(_repository.FilePath)!;
            Directory.CreateDirectory(directory);

            var watcher = new FileSystemWatcher(directory, Path.GetFileName(_repository.FilePath));
            FileSystemEventHandler changed = (_, _) => _reloadTimer.Change(ReloadDelay, Timeout.InfiniteTimeSpan);
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Deleted += changed;
            watcher.Renamed += (_, _) => _reloadTimer.Change(ReloadDelay, Timeout.InfiniteTimeSpan);
            watcher.EnableRaisingEvents = true;
            return watcher;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Hover notes then stay as they were loaded. The main window reports any real problem with the file.
            return null;
        }
    }

    private async Task ReloadAsync()
    {
        try
        {
            _lookup = new NoteLookup(await _repository.LoadAsync());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotesFileCorruptException or ArgumentException)
        {
            // Keep showing the last good notes.
        }
    }

    private void Run()
    {
        var detector = new HoverDetector();
        detector.WarmUp();

        var last = Point.Empty;
        var restingSince = DateTime.UtcNow;
        var checkedThisRest = false;
        var shownBounds = Rectangle.Empty;
        var shownAt = DateTime.MinValue;

        while (!_stop.WaitOne(PollInterval))
        {
            if (!GetCursorPos(out var point))
            {
                continue;
            }

            var cursor = new Point(point.X, point.Y);
            var isShown = !shownBounds.IsEmpty;

            if (cursor != last)
            {
                last = cursor;
                restingSince = DateTime.UtcNow;
                checkedThisRest = false;

                if (isShown && !shownBounds.Contains(cursor))
                {
                    HideTooltip();
                    shownBounds = Rectangle.Empty;
                    isShown = false;
                }
            }

            if (isShown && DateTime.UtcNow - shownAt >= MaxDisplayTime)
            {
                // The list may have scrolled under a still cursor, so don't leave a note up indefinitely.
                HideTooltip();
                shownBounds = Rectangle.Empty;
                checkedThisRest = true;
                continue;
            }

            if (isShown || checkedThisRest || DateTime.UtcNow - restingSince < HoverDelay)
            {
                continue;
            }

            checkedThisRest = true;
            var target = detector.Detect(cursor);
            var notes = target is null ? [] : FindNotes(target);
            if (notes.Count > 0)
            {
                shownBounds = target!.Bounds;
                shownAt = DateTime.UtcNow;
                _uiContext.Post(_ => _tooltip.ShowNotes(notes, cursor), null);
            }
        }
    }

    private IReadOnlyList<NoteEntry> FindNotes(HoverTarget target)
    {
        foreach (var folder in target.Folders)
        {
            var notes = _lookup.Find(folder, target.DisplayName);
            if (notes.Count > 0)
            {
                return notes;
            }
        }

        return [];
    }

    private void HideTooltip() => _uiContext.Post(_ => _tooltip.HideNote(), null);

    /// <inheritdoc />
    public void Dispose()
    {
        _stop.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
        _fileWatcher?.Dispose();
        _reloadTimer.Dispose();
        _tooltip.Dispose();
        _stop.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);
}
