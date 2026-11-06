using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace FlySetup;

/// <summary>Thrown when Windows refuses to run the installed app, usually because of Smart App Control.</summary>
internal sealed class BlockedByWindowsException(Exception? inner = null) : Exception("Windows blocked Flyleaf from running.", inner);

/// <summary>Installs Flyleaf for the current user only, so nothing here needs administrator rights.</summary>
internal static class UserInstaller
{
    private const string PayloadResource = "payload/Flyleaf.zip";
    private const string Version = "1.0.0";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Flyleaf";
    private const string UninstallerName = "Uninstall-Flyleaf.exe";
    private const string ApplicationName = "Flyleaf.exe";

    // The menu item the app adds for itself when it starts. See ShellMenu in FlyleafStore.
    private static readonly string[] MenuKeys = [@"Software\Classes\*\shell\FlyleafNote", @"Software\Classes\Directory\shell\FlyleafNote"];

    // A blocked app exits sooner than this, so an app still running after it counts as started.
    private const int StartCheckMilliseconds = 3000;

    // The usual Windows messages for "the code-integrity policy refused this file".
    private const int ErrorAccessDisabledByPolicy = 1260;
    private const int ErrorSystemIntegrityPolicyViolation = 4551;

    private static string InstallDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Flyleaf");

    private static string ShortcutPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Flyleaf.lnk");

    // Automated UI tests set FLYLEAF_NOTES_FILE, as in the app, so that they never touch real notes.
    private static string NotesFile { get; } = Environment.GetEnvironmentVariable("FLYLEAF_NOTES_FILE") is { Length: > 0 } overridePath
        ? overridePath
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Flyleaf", "notes.json");

    /// <summary>True for Flyleaf-Setup.exe, and false for the uninstaller, which is the same program with nothing embedded.</summary>
    public static bool HasPayload { get; } = typeof(UserInstaller).Assembly.GetManifestResourceInfo(PayloadResource) is not null;

    /// <summary>Tells whether this user has any notes to keep or delete.</summary>
    public static bool HasNotes => File.Exists(NotesFile);

    /// <summary>Tells whether an exception is a failure to report, rather than a bug.</summary>
    /// <param name="ex">The exception.</param>
    /// <returns><see langword="true"/> if the user should be told about it.</returns>
    public static bool IsExpectedFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or InvalidDataException or COMException or BlockedByWindowsException;

    /// <summary>Installs or upgrades Flyleaf and starts it in the tray. Anything that fails is undone.</summary>
    /// <param name="report">Receives the current step (0 to 2) and the overall percentage.</param>
    public static void Install(Action<int, int> report)
    {
        StopTrayApp();
        ExtractPayload(report);

        try
        {
            var application = Path.Combine(InstallDirectory, ApplicationName);

            report(1, 85);
            RegisterForCurrentUser(application);
            TryCreateShortcut(application);

            report(2, 92);
            StartAndCheck(application);
            report(2, 100);
        }
        catch
        {
            RemoveEverything();
            FinishRemovingItself();
            throw;
        }
    }

    /// <summary>Removes everything Flyleaf added, and the user's notes only if asked to.</summary>
    /// <param name="deleteNotes">Whether to delete the notes as well.</param>
    /// <param name="report">Receives the current step (0 to 2) and the overall percentage.</param>
    public static void Uninstall(bool deleteNotes, Action<int, int> report)
    {
        report(0, 10);
        StopTrayApp();

        report(1, 40);
        RemoveEverything();

        report(2, 80);
        if (deleteNotes && Path.GetDirectoryName(NotesFile) is { } notesDirectory && Directory.Exists(notesDirectory))
        {
            Directory.Delete(notesDirectory, recursive: true);
        }

        report(2, 100);
    }

    private static void RemoveEverything()
    {
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
        {
            run?.DeleteValue("Flyleaf", throwOnMissingValue: false);
        }

        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        foreach (var key in MenuKeys)
        {
            Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
        }

        File.Delete(ShortcutPath);
        DeleteProgramFiles();
    }

    // The running uninstaller can't delete itself, so it stays until FinishRemovingItself.
    private static void DeleteProgramFiles()
    {
        if (!Directory.Exists(InstallDirectory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(InstallDirectory, "*", SearchOption.AllDirectories))
        {
            if (!file.Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(file);
            }
        }

        if (!IsRunningFromInstallDirectory)
        {
            Directory.Delete(InstallDirectory, recursive: true);
        }
    }

    private static bool IsRunningFromInstallDirectory =>
        Environment.ProcessPath?.StartsWith(InstallDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>After an uninstall, has Windows' command processor delete the folder just after this process exits.</summary>
    public static void FinishRemovingItself()
    {
        if (HasPayload || !IsRunningFromInstallDirectory || Registry.CurrentUser.OpenSubKey(UninstallKey) is not null)
        {
            return;
        }

        Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            Arguments = $"/c ping -n 3 127.0.0.1 > nul & rmdir /s /q \"{InstallDirectory}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Environment.SystemDirectory,
        })?.Dispose();
    }

    private static void StopTrayApp()
    {
        foreach (var process in Process.GetProcessesByName("Flyleaf"))
        {
            using (process)
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    // Another user's copy, or one that has already exited.
                }
            }
        }
    }

    // Extracted beside the real folder first, so that a failed extraction leaves the current copy working.
    private static void ExtractPayload(Action<int, int> report)
    {
        var incoming = InstallDirectory + ".new";
        if (Directory.Exists(incoming))
        {
            Directory.Delete(incoming, recursive: true);
        }

        try
        {
            var root = Directory.CreateDirectory(incoming).FullName + Path.DirectorySeparatorChar;
            using var payload = typeof(UserInstaller).Assembly.GetManifestResourceStream(PayloadResource)!;
            using var archive = new ZipArchive(payload, ZipArchiveMode.Read);

            var entries = archive.Entries;
            for (var i = 0; i < entries.Count; i++)
            {
                var target = Path.GetFullPath(Path.Combine(root, entries[i].FullName));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The installer's contents are damaged.");
                }

                if (entries[i].Name.Length > 0)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entries[i].ExtractToFile(target, overwrite: true);
                }

                report(0, (i + 1) * 80 / entries.Count);
            }

            if (Directory.Exists(InstallDirectory))
            {
                Directory.Delete(InstallDirectory, recursive: true);
            }

            Directory.Move(incoming, InstallDirectory);
        }
        catch
        {
            if (Directory.Exists(incoming))
            {
                Directory.Delete(incoming, recursive: true);
            }

            throw;
        }
    }

    private static void RegisterForCurrentUser(string application)
    {
        using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            run.SetValue("Flyleaf", $"\"{application}\" --tray");
        }

        var sizeInKilobytes = new DirectoryInfo(InstallDirectory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length) / 1024;

        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", "Flyleaf");
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", "Flyleaf");
        key.SetValue("InstallLocation", InstallDirectory);
        key.SetValue("DisplayIcon", application);
        key.SetValue("UninstallString", $"\"{Path.Combine(InstallDirectory, UninstallerName)}\"");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)sizeInKilobytes, RegistryValueKind.DWord);
    }

    private static void StartAndCheck(string application)
    {
        var start = new ProcessStartInfo(application) { UseShellExecute = false, WorkingDirectory = InstallDirectory };
        start.ArgumentList.Add("--tray");

        Process process;
        try
        {
            process = Process.Start(start)!;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is ErrorAccessDisabledByPolicy or ErrorSystemIntegrityPolicyViolation)
        {
            throw new BlockedByWindowsException(ex);
        }

        using (process)
        {
            if (process.WaitForExit(StartCheckMilliseconds))
            {
                throw new BlockedByWindowsException();
            }
        }
    }

    // A missing shortcut is a small loss: Flyleaf still runs from the tray and starts at sign-in.
    private static void TryCreateShortcut(string application)
    {
        try
        {
            CreateShortcut(application);
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine(ex);
        }
    }

    private static unsafe void CreateShortcut(string application)
    {
        // Slots after the three IUnknown methods, in the order Windows declares them.
        const int ShellLink_SetDescription = 7;
        const int ShellLink_SetWorkingDirectory = 9;
        const int ShellLink_SetIconLocation = 17;
        const int ShellLink_SetPath = 20;
        const int PersistFile_Save = 6;
        const uint COINIT_APARTMENTTHREADED = 2;
        const uint CLSCTX_INPROC_SERVER = 1;

        _ = NativeMethods.CoInitializeEx(0, COINIT_APARTMENTTHREADED);

        var classId = new Guid("00021401-0000-0000-C000-000000000046");
        var shellLinkIid = new Guid("000214F9-0000-0000-C000-000000000046");
        var persistFileIid = new Guid("0000010B-0000-0000-C000-000000000046");

        nint link;
        Marshal.ThrowExceptionForHR(NativeMethods.CoCreateInstance(&classId, 0, CLSCTX_INPROC_SERVER, &shellLinkIid, &link));
        try
        {
            SetText(link, ShellLink_SetPath, application);
            SetText(link, ShellLink_SetWorkingDirectory, InstallDirectory);
            SetText(link, ShellLink_SetDescription, "A note on every file.");

            fixed (char* icon = application)
            {
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, char*, int, int>)Slot(link, ShellLink_SetIconLocation))(link, icon, 0));
            }

            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(link, in persistFileIid, out var persist));
            try
            {
                fixed (char* path = ShortcutPath)
                {
                    Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, char*, int, int>)Slot(persist, PersistFile_Save))(persist, path, 1));
                }
            }
            finally
            {
                Marshal.Release(persist);
            }
        }
        finally
        {
            Marshal.Release(link);
        }
    }

    private static unsafe void SetText(nint comObject, int slot, string text)
    {
        fixed (char* value = text)
        {
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, char*, int>)Slot(comObject, slot))(comObject, value));
        }
    }

    private static unsafe nint Slot(nint comObject, int slot) => (*(nint**)comObject)[slot];
}
