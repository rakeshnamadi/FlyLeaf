using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Flyleaf.Core;
using FlyWatcher;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace Flyleaf.Tests;

/// <summary>Runs the real watcher and pipe server together against a temporary notes file.</summary>
public sealed class WatcherHost : IAsyncDisposable
{
    private readonly WatcherService _watcher;
    private readonly PipeServer _server;

    public WatcherHost(string pipeName)
    {
        NotesPath = Path.Combine(Path.GetTempPath(), "flyleaf-tests", Guid.NewGuid().ToString("N"), "notes.json");
        Repository = new NotesRepository(NotesPath);
        Index = new NotesIndex();

        var options = new WatcherOptions(NotesPath, WindowsIdentity.GetCurrent().User!, pipeName);
        _watcher = new WatcherService(options, Index, NullLogger<WatcherService>.Instance);
        _server = new PipeServer(options, Index, NullLogger<PipeServer>.Instance);
    }

    public string NotesPath { get; }

    public NotesRepository Repository { get; }

    public NotesIndex Index { get; }

    public async Task StartAsync()
    {
        await _watcher.StartAsync(CancellationToken.None);
        await _server.StartAsync(CancellationToken.None);
    }

    public async Task SaveNoteAsync(string path, string description)
    {
        var notes = await Repository.LoadAsync();
        NotesFile.UpdateDescription(notes.GetOrAdd(path, DateTimeOffset.Now), description, DateTimeOffset.Now);
        await Repository.SaveAsync(notes);
    }

    public async Task WaitForLookupAsync(string path, string expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (Index.Lookup(path) != expected)
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _server.StopAsync(CancellationToken.None);
        await _watcher.StopAsync(CancellationToken.None);
        Directory.Delete(Path.GetDirectoryName(NotesPath)!, recursive: true);
    }
}

public sealed class WatcherAndPipeTests : IAsyncLifetime
{
    private readonly string _pipeName = "FlyleafTest-" + Guid.NewGuid().ToString("N");
    private WatcherHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = new WatcherHost(_pipeName);
        await _host.StartAsync();
    }

    public async Task DisposeAsync() => await _host.DisposeAsync();

    private async Task<string> QueryAsync(string path)
    {
        await using var pipe = new NamedPipeClientStream(
            ".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous);
        await pipe.ConnectAsync(2000);
        await pipe.WriteAsync(PipeProtocol.EncodeLine(path));
        await pipe.FlushAsync();

        var reader = new StreamReader(pipe, Encoding.UTF8);
        var line = await reader.ReadLineAsync();
        Assert.NotNull(line);
        return PipeProtocol.DecodeLine(Encoding.UTF8.GetBytes(line));
    }

    [Fact]
    public async Task Pipe_ReturnsEmptyLine_WhenThereIsNoNote() =>
        Assert.Equal("", await QueryAsync(@"C:\no\such\note.txt"));

    [Fact]
    public async Task Pipe_ReturnsNote_AfterHotReload()
    {
        await _host.SaveNoteAsync(@"C:\Docs\Report.docx", "line one\nline two");
        await _host.WaitForLookupAsync(@"C:\Docs\Report.docx", "line one\nline two");

        Assert.Equal("line one\nline two", await QueryAsync(@"c:\docs\report.docx"));
    }

    [Fact]
    public async Task HotReload_PicksUpASecondSave()
    {
        await _host.SaveNoteAsync(@"C:\a.txt", "one");
        await _host.WaitForLookupAsync(@"C:\a.txt", "one");
        await _host.SaveNoteAsync(@"C:\a.txt", "two");
        await _host.WaitForLookupAsync(@"C:\a.txt", "two");

        Assert.Equal("two", await QueryAsync(@"C:\a.txt"));
    }

    [Fact]
    public async Task CorruptFile_KeepsServingTheLastGoodNotes()
    {
        await _host.SaveNoteAsync(@"C:\a.txt", "good");
        await _host.WaitForLookupAsync(@"C:\a.txt", "good");

        await File.WriteAllTextAsync(_host.NotesPath, "{ broken");
        await Task.Delay(500);

        Assert.Equal("good", await QueryAsync(@"C:\a.txt"));
    }

    [Fact]
    public async Task Pipe_HandlesManySimultaneousClients()
    {
        await _host.SaveNoteAsync(@"C:\a.txt", "shared");
        await _host.WaitForLookupAsync(@"C:\a.txt", "shared");

        var replies = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Task.Run(() => QueryAsync(@"C:\a.txt"))));

        Assert.All(replies, reply => Assert.Equal("shared", reply));
    }

    [Fact]
    public void Lookup_AveragesUnderOneMillisecond()
    {
        var notes = new NotesFile();
        for (var i = 0; i < 10_000; i++)
        {
            notes.Entries.Add(new NoteEntry { Path = $@"C:\Docs\file{i}.txt", Description = "note" });
        }

        var index = new NotesIndex();
        index.Replace(notes);

        const int Lookups = 10_000;
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < Lookups; i++)
        {
            Assert.Equal("note", index.Lookup($@"c:\docs\FILE{i}.txt"));
        }

        Assert.True(stopwatch.Elapsed.TotalMilliseconds / Lookups < 1, $"Average {stopwatch.Elapsed.TotalMilliseconds / Lookups} ms");
    }
}

/// <summary>
/// Loads the published native FlyShell.dll and drives it through COM vtables exactly as Explorer does.
/// Run installer\build.ps1 first. These use the real pipe name, so stop an installed FlyWatcher first.
/// </summary>
[Collection("FlyShell")]
public sealed class FlyShellNativeTests(ITestOutputHelper output)
{
    private static readonly Guid HandlerClassId = new("7B1E4F52-9C3A-4D86-A0E7-5F2C8D61B3A4");
    private static readonly Guid ClassFactoryIid = new("00000001-0000-0000-C000-000000000046");
    private static readonly Guid PersistFileIid = new("0000010B-0000-0000-C000-000000000046");
    private static readonly Guid QueryInfoIid = new("00021500-0000-0000-C000-000000000046");
    private const int S_OK = 0;
    private const int S_FALSE = 1;

    private static string? FindDll()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "dist", "payload", "FlyShell.dll");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static unsafe nint Slot(nint comObject, int slot) => (*(nint**)comObject)[slot];

    private static unsafe (int Result, string? Tip) GetTip(string path)
    {
        var dll = FindDll() ?? throw new FileNotFoundException("Run installer\\build.ps1 first.");
        var module = NativeLibrary.Load(dll);
        var getClassObject = (delegate* unmanaged<Guid*, Guid*, nint*, int>)NativeLibrary.GetExport(module, "DllGetClassObject");

        var classId = HandlerClassId;
        var factoryIid = ClassFactoryIid;
        nint factory;
        Assert.Equal(S_OK, getClassObject(&classId, &factoryIid, &factory));

        var persistIid = PersistFileIid;
        nint persist;
        var createInstance = (delegate* unmanaged<nint, nint, Guid*, nint*, int>)Slot(factory, 3);
        Assert.Equal(S_OK, createInstance(factory, 0, &persistIid, &persist));

        fixed (char* pathPointer = path)
        {
            var load = (delegate* unmanaged<nint, char*, uint, int>)Slot(persist, 5);
            Assert.Equal(S_OK, load(persist, pathPointer, 0));
        }

        var queryIid = QueryInfoIid;
        Assert.Equal(S_OK, Marshal.QueryInterface(persist, in queryIid, out var query));

        char* tip = null;
        var getInfoTip = (delegate* unmanaged<nint, uint, char**, int>)Slot(query, 3);
        var result = getInfoTip(query, 0, &tip);
        var text = tip is null ? null : new string(tip);
        Marshal.FreeCoTaskMem((nint)tip);

        Marshal.Release(query);
        Marshal.Release(persist);
        Marshal.Release(factory);
        return (result, text);
    }

    [SkippableDllFact]
    public void GetInfoTip_ReturnsSFalse_WhenTheServiceIsNotRunning()
    {
        // The first call includes loading the DLL and JIT-compiling this test, neither of which Explorer pays for.
        GetTip(@"C:\Docs\Report.docx");

        var stopwatch = Stopwatch.StartNew();
        var (result, tip) = GetTip(@"C:\Docs\Report.docx");
        output.WriteLine($"No service: {stopwatch.ElapsedMilliseconds} ms");

        Assert.Equal(S_FALSE, result);
        Assert.Null(tip);
        Assert.True(stopwatch.ElapsedMilliseconds < 500, $"Took {stopwatch.ElapsedMilliseconds} ms");
    }

    [SkippableDllFact]
    public async Task GetInfoTip_ReturnsTheNote_ThroughTheRealPipe()
    {
        await using var host = new WatcherHost(PipeProtocol.PipeName);
        await host.StartAsync();
        await host.SaveNoteAsync(@"C:\Docs\Report.docx", "Q3 final\nApproved by finance");
        await host.WaitForLookupAsync(@"C:\Docs\Report.docx", "Q3 final\nApproved by finance");

        // The first call pays for start-up inside the DLL. Hover latency is what the later calls show.
        await Task.Run(() => GetTip(@"C:\no\note.txt"));

        var times = new List<long>();
        (int Result, string? Tip) reply = default;
        for (var i = 0; i < 50; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            reply = await Task.Run(() => GetTip(@"c:\docs\report.docx"));
            times.Add(stopwatch.ElapsedMilliseconds);
        }

        output.WriteLine($"With note: median {times.Order().ElementAt(times.Count / 2)} ms, worst {times.Max()} ms");

        Assert.Equal(S_OK, reply.Result);
        Assert.Equal("Q3 final\nApproved by finance", reply.Tip);
        Assert.True(times.Order().ElementAt(times.Count / 2) < 80, $"Median {times.Order().ElementAt(times.Count / 2)} ms");

        var empty = await Task.Run(() => GetTip(@"C:\no\note.txt"));
        Assert.Equal(S_FALSE, empty.Result);
        Assert.Null(empty.Tip);
    }

    private sealed class SkippableDllFactAttribute : FactAttribute
    {
        public SkippableDllFactAttribute()
        {
            if (FindDll() is null)
            {
                Skip = "dist\\payload\\FlyShell.dll not found. Run installer\\build.ps1 first.";
            }
        }
    }
}

[CollectionDefinition("FlyShell", DisableParallelization = true)]
public sealed class FlyShellCollection;
