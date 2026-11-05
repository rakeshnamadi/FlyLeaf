using System.Diagnostics;
using System.Windows.Automation;
using Flyleaf.Core;
using Xunit;

namespace Flyleaf.Tests;

public class NoteLookupTests
{
    private const string Folder = @"C:\Docs";

    private static NoteLookup Lookup(params (string Path, string Note)[] notes)
    {
        var file = new NotesFile();
        foreach (var (path, note) in notes)
        {
            file.Entries.Add(new NoteEntry { Path = path, Description = note });
        }

        return new NoteLookup(file);
    }

    [Fact]
    public void Find_MatchesAFullNameInAnyCase() =>
        Assert.Equal("note", Assert.Single(Lookup((@"C:\Docs\Report.docx", "note")).Find(@"c:\DOCS\", "REPORT.docx")).Description);

    [Fact]
    public void Find_MatchesAHiddenExtension() =>
        Assert.Equal("note", Assert.Single(Lookup((@"C:\Docs\Report.docx", "note")).Find(Folder, "Report")).Description);

    [Fact]
    public void Find_MatchesAFolder() =>
        Assert.Equal("note", Assert.Single(Lookup((@"C:\Docs\Archive", "note")).Find(Folder, "Archive")).Description);

    [Fact]
    public void Find_ReturnsBothNotes_WhenNamesakesBothHaveNotes() =>
        Assert.Equal(2, Lookup((@"C:\Docs\Report.docx", "one"), (@"C:\Docs\Report.pdf", "two")).Find(Folder, "Report").Count);

    [Fact]
    public void Find_PrefersTheExactName_OverNamesakes() =>
        Assert.Equal("two", Assert.Single(Lookup((@"C:\Docs\Report.docx", "one"), (@"C:\Docs\Report.pdf", "two")).Find(Folder, "Report.pdf")).Description);

    [Theory]
    [InlineData(@"C:\Other", "Report")]
    [InlineData(Folder, "Summary")]
    public void Find_ReturnsNothing_WhenThereIsNoNote(string folder, string name) =>
        Assert.Empty(Lookup((@"C:\Docs\Report.docx", "note")).Find(folder, name));

    [Fact]
    public void Find_IgnoresEmptyNotes() =>
        Assert.Empty(Lookup((@"C:\Docs\Report.docx", "")).Find(Folder, "Report.docx"));
}

public class NoteListTextTests
{
    [Fact]
    public void ToggleBullets_AddsToEveryLine() =>
        Assert.Equal("• one\r\n• two", NoteListText.ToggleBullets("one\r\ntwo"));

    [Fact]
    public void ToggleBullets_RemovesWhenAllLinesHaveThem() =>
        Assert.Equal("one\r\ntwo", NoteListText.ToggleBullets("• one\r\n• two"));

    [Fact]
    public void ToggleBullets_CompletesAPartlyBulletedSelection() =>
        Assert.Equal("• one\r\n• two", NoteListText.ToggleBullets("• one\r\ntwo"));

    [Fact]
    public void ToggleBullets_TurnsAnEmptyLineIntoAListItem() =>
        Assert.Equal("• ", NoteListText.ToggleBullets(""));

    [Fact]
    public void ToggleBullets_KeepsBlankLinesInsideASelection() =>
        Assert.Equal("• one\r\n\r\n• two", NoteListText.ToggleBullets("one\r\n\r\ntwo"));

    [Theory]
    [InlineData("• ", true)]
    [InlineData("•", true)]
    [InlineData("• text", false)]
    [InlineData("", false)]
    public void IsEmptyBullet_RecognisesAnUntypedItem(string line, bool expected) =>
        Assert.Equal(expected, NoteListText.IsEmptyBullet(line));
}

/// <summary>
/// Opens a real File Explorer window on a temporary folder and asks the detector what is at each item's position.
/// It needs a visible desktop and briefly opens a window, so it only runs when FLYLEAF_UI_TESTS=1.
/// </summary>
public sealed class HoverDetectorExplorerTests
{
    private sealed class ExplorerFactAttribute : FactAttribute
    {
        public ExplorerFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("FLYLEAF_UI_TESTS") != "1")
            {
                Skip = "Set FLYLEAF_UI_TESTS=1 to run against a real Explorer window.";
            }
        }
    }

    [ExplorerFact]
    public void Detect_FindsTheItemAndFolder_InARealExplorerWindow()
    {
        var thread = new Thread(RunCheck);
        Exception? failure = null;

        void RunCheck()
        {
            try
            {
                Check();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }

    private static void Check()
    {
        var folder = Path.Combine(Path.GetTempPath(), "flyleaf-hover-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "first note target.txt"), "x");
        Directory.CreateDirectory(Path.Combine(folder, "second folder"));

        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        var existing = CountWindows(shell);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true })?.Dispose();

        try
        {
            var window = WaitForWindow(shell, folder);
            var root = AutomationElement.FromHandle((nint)(long)window.HWND);
            Thread.Sleep(1500);

            var items = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
            var seen = new List<string>();
            var detector = new HoverDetector();

            foreach (AutomationElement item in items)
            {
                var name = item.Current.Name;
                var bounds = item.Current.BoundingRectangle;
                var target = detector.Detect(new Point((int)(bounds.X + 20), (int)(bounds.Y + (bounds.Height / 2))));

                Assert.NotNull(target);
                Assert.Equal(name, target.DisplayName);
                Assert.Equal(PathNormalizer.Normalize(folder), PathNormalizer.Normalize(Assert.Single(target.Folders)));
                Assert.False(target.Bounds.IsEmpty);
                seen.Add(name);
            }

            Assert.Contains("second folder", seen);
            Assert.Contains(seen, name => name.StartsWith("first note target", StringComparison.Ordinal));
            window.Quit();
        }
        finally
        {
            Thread.Sleep(500);
            Directory.Delete(folder, recursive: true);
        }
    }

    private static int CountWindows(dynamic shell) => shell.Windows().Count;

    private static dynamic WaitForWindow(dynamic shell, string folder)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(10))
        {
            foreach (var window in shell.Windows())
            {
                try
                {
                    string path = window.Document.Folder.Self.Path;
                    if (path.Equals(folder, StringComparison.OrdinalIgnoreCase))
                    {
                        return window;
                    }
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
                {
                    // A window that is still opening.
                }
            }

            Thread.Sleep(100);
        }

        throw new TimeoutException("The Explorer window did not appear.");
    }
}
