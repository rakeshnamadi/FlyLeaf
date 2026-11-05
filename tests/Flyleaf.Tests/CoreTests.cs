using System.Text;
using Flyleaf.Core;
using FlyWatcher;
using Xunit;

namespace Flyleaf.Tests;

public class PathNormalizerTests
{
    [Theory]
    [InlineData(@"C:\Users\Me\File.TXT", @"c:\users\me\file.txt")]
    [InlineData("C:/Users/Me/File.txt", @"c:\users\me\file.txt")]
    [InlineData(@"C:\Users\Me\", @"c:\users\me")]
    [InlineData(@"C:\", @"c:\")]
    [InlineData(@"\\Server\Share\Doc.txt", @"\\server\share\doc.txt")]
    [InlineData(@"\\?\C:\Users\Me\File.txt", @"c:\users\me\file.txt")]
    [InlineData(@"\\?\UNC\Server\Share\Doc.txt", @"\\server\share\doc.txt")]
    [InlineData(@"C:\a\..\b\File.txt", @"c:\b\file.txt")]
    public void Normalize_ProducesCanonicalKey(string input, string expected) =>
        Assert.Equal(expected, PathNormalizer.Normalize(input));
}

public class PipeProtocolTests
{
    [Theory]
    [InlineData("")]
    [InlineData("plain note")]
    [InlineData("line one\nline two\r\nline three")]
    [InlineData(@"C:\new\readme.txt")]
    [InlineData("ends with backslash\\")]
    [InlineData("日本語のメモ ✓")]
    public void EncodedLine_RoundTrips(string text)
    {
        var bytes = PipeProtocol.EncodeLine(text);

        Assert.Equal((byte)'\n', bytes[^1]);
        Assert.Single(bytes, b => b == (byte)'\n');
        Assert.Equal(text, PipeProtocol.DecodeLine(bytes.AsSpan(0, bytes.Length - 1)));
    }
}

public class NotesFileTests
{
    private static readonly DateTimeOffset T1 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = T1.AddDays(1);

    [Fact]
    public void UpdateDescription_MovesPreviousTextToHistory()
    {
        var entry = new NoteEntry { Path = @"C:\a.txt", Description = "old", LastModified = T1 };

        Assert.True(NotesFile.UpdateDescription(entry, "new", T2));

        Assert.Equal("new", entry.Description);
        Assert.Equal(T2, entry.LastModified);
        var item = Assert.Single(entry.History);
        Assert.Equal("old", item.Text);
        Assert.Equal(T1, item.Timestamp);
    }

    [Fact]
    public void UpdateDescription_IgnoresSameTextWithDifferentLineEndings()
    {
        var entry = new NoteEntry { Description = "a\nb", LastModified = T1 };

        Assert.False(NotesFile.UpdateDescription(entry, "a\r\nb", T2));

        Assert.Empty(entry.History);
        Assert.Equal(T1, entry.LastModified);
    }

    [Fact]
    public void UpdateDescription_DoesNotRecordAnEmptyFirstNote()
    {
        var entry = new NoteEntry { LastModified = T1 };

        NotesFile.UpdateDescription(entry, "first", T2);

        Assert.Empty(entry.History);
    }

    [Fact]
    public void GetOrAdd_TreatsDifferentCasingAsTheSamePath()
    {
        var notes = new NotesFile();

        var first = notes.GetOrAdd(@"C:\Docs\Report.docx", T1);
        var second = notes.GetOrAdd(@"c:/docs/REPORT.docx", T2);

        Assert.Same(first, second);
        Assert.Single(notes.Entries);
        Assert.Equal(@"C:\Docs\Report.docx", first.Path);
    }
}

public sealed class NotesRepositoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "flyleaf-tests", Guid.NewGuid().ToString("N"));

    private string NotesPath => Path.Combine(_directory, "notes.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task Load_ReturnsEmptyWhenFileIsMissing()
    {
        var notes = await new NotesRepository(NotesPath).LoadAsync();

        Assert.Empty(notes.Entries);
    }

    [Fact]
    public async Task SaveThenLoad_PreservesEverything()
    {
        var repository = new NotesRepository(NotesPath);
        var notes = new NotesFile();
        var entry = notes.GetOrAdd(@"C:\Docs\résumé.docx", DateTimeOffset.Now);
        NotesFile.UpdateDescription(entry, "first", DateTimeOffset.Now);
        NotesFile.UpdateDescription(entry, "second\nline", DateTimeOffset.Now);

        await repository.SaveAsync(notes);
        var loaded = await repository.LoadAsync();

        var result = Assert.Single(loaded.Entries);
        Assert.Equal(@"C:\Docs\résumé.docx", result.Path);
        Assert.Equal("second\nline", result.Description);
        Assert.Equal("first", Assert.Single(result.History).Text);
        Assert.False(File.Exists(NotesPath + ".tmp"));
    }

    [Fact]
    public async Task Save_WritesReadableJson()
    {
        var notes = new NotesFile();
        notes.GetOrAdd(@"C:\Docs\résumé.docx", DateTimeOffset.Now);

        await new NotesRepository(NotesPath).SaveAsync(notes);

        var json = await File.ReadAllTextAsync(NotesPath);
        Assert.Contains("\"entries\"", json);
        Assert.Contains("résumé", json);
        Assert.Contains("\n", json);
    }

    [Fact]
    public async Task ConcurrentSaves_DoNotCollide()
    {
        var repository = new NotesRepository(NotesPath);
        var notes = new NotesFile();
        notes.GetOrAdd(@"C:\a.txt", DateTimeOffset.Now);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => repository.SaveAsync(notes)));

        Assert.Single((await repository.LoadAsync()).Entries);
    }

    [Fact]
    public async Task Load_ReportsCorruptFile_AndQuarantineKeepsIt()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(NotesPath, "{ not json", Encoding.UTF8);
        var repository = new NotesRepository(NotesPath);

        await Assert.ThrowsAsync<NotesFileCorruptException>(() => repository.LoadAsync());

        var backup = repository.QuarantineCorruptFile();
        Assert.False(File.Exists(NotesPath));
        Assert.Equal("{ not json", await File.ReadAllTextAsync(backup));
    }
}

public class NotesIndexTests
{
    [Fact]
    public void Lookup_MatchesAnyCasingAndHandlesBadInput()
    {
        var notes = new NotesFile();
        notes.Entries.Add(new NoteEntry { Path = @"C:\Docs\Report.docx", Description = "Q3 final" });
        var index = new NotesIndex();

        index.Replace(notes);

        Assert.Equal("Q3 final", index.Lookup(@"c:\docs\REPORT.DOCX"));
        Assert.Equal("", index.Lookup(@"C:\other.txt"));
        Assert.Equal("", index.Lookup("   "));
        Assert.Equal("", index.Lookup("C:\\bad\0path"));
    }
}
