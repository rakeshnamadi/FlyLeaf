using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Flyleaf.Core;

/// <summary>Thrown when notes.json exists but does not contain valid notes.</summary>
/// <param name="path">The path of the unreadable file.</param>
/// <param name="inner">The parser error.</param>
public sealed class NotesFileCorruptException(string path, Exception inner)
    : Exception($"The notes file '{path}' could not be read.", inner);

/// <summary>Reads and writes notes.json. Saves are atomic, so a reader never sees a half-written file.</summary>
/// <param name="filePath">The full path of the notes file.</param>
public sealed class NotesRepository(string filePath)
{
    private const int MaxAttempts = 5;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

    private static readonly NotesJsonContext Json = new(new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,

        // The file is meant to be read by people, so keep accents and non-Latin text as they are.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    private readonly SemaphoreSlim _saveLock = new(1, 1);

    /// <summary>
    /// The default location: %AppData%\Flyleaf\notes.json for the current user.
    /// Automated UI tests set FLYLEAF_NOTES_FILE so that they never touch real notes.
    /// </summary>
    public static string DefaultFilePath { get; } = Environment.GetEnvironmentVariable("FLYLEAF_NOTES_FILE") is { Length: > 0 } overridePath
        ? overridePath
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Flyleaf", "notes.json");

    /// <summary>The full path of the notes file.</summary>
    public string FilePath { get; } = filePath;

    /// <summary>Loads the notes file. A missing file is treated as an empty one.</summary>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <returns>The notes on disk.</returns>
    /// <exception cref="NotesFileCorruptException">The file exists but is not valid.</exception>
    public async Task<NotesFile> LoadAsync(CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var stream = new FileStream(
                    FilePath,
                    new FileStreamOptions
                    {
                        Mode = FileMode.Open,
                        Access = FileAccess.Read,
                        Share = FileShare.ReadWrite | FileShare.Delete,
                        Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                    });

                return await JsonSerializer.DeserializeAsync(stream, Json.NotesFile, cancellationToken).ConfigureAwait(false)
                    ?? new NotesFile();
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return new NotesFile();
            }
            catch (JsonException ex)
            {
                throw new NotesFileCorruptException(FilePath, ex);
            }
            catch (IOException) when (attempt < MaxAttempts)
            {
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Saves the notes by writing a temporary file and moving it over the real one.</summary>
    /// <param name="notes">The notes to save.</param>
    /// <param name="cancellationToken">Cancels the save.</param>
    /// <returns>A task that completes when the file is on disk.</returns>
    public async Task SaveAsync(NotesFile notes, CancellationToken cancellationToken = default)
    {
        var tempPath = FilePath + ".tmp";

        await _saveLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, notes, Json.NotesFile, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(tempPath, FilePath, overwrite: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < MaxAttempts)
                {
                    await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
        finally
        {
            _saveLock.Release();
        }
    }

    /// <summary>Moves an unreadable notes file aside so that it is never overwritten.</summary>
    /// <returns>The path of the preserved copy.</returns>
    public string QuarantineCorruptFile()
    {
        var backup = Path.ChangeExtension(FilePath, $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.Move(FilePath, backup);
        return backup;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp file is overwritten by the next save.
        }
    }
}

[JsonSerializable(typeof(NotesFile))]
internal sealed partial class NotesJsonContext : JsonSerializerContext;
