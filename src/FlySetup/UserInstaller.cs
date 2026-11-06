using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace FlySetup;

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

    // How long a new app must keep running before it counts as started. A blocked app exits sooner than this.
    private static readonly TimeSpan StartCheckTime = TimeSpan.FromSeconds(3);

    // The usual Windows messages for "the code-integrity policy refused this file".
    private const int ErrorAccessDisabledByPolicy = 1260;
    private const int ErrorSystemIntegrityPolicyViolation = 4551;

    private const string BlockedMessage =
        "Windows blocked Flyleaf from running, and the changes have been undone.\n\n" +
        "This usually means Smart App Control is on and this copy of Flyleaf is not code-signed. Ask whoever shared it for a signed copy.";

    private static string InstallDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Flyleaf");

    private static string ShortcutPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Flyleaf.lnk");

    /// <summary>True for Flyleaf-Setup.exe, and false for the uninstaller, which is the same program with nothing embedded.</summary>
    public static bool HasPayload { get; } = typeof(UserInstaller).Assembly.GetManifestResourceInfo(PayloadResource) is not null;

    /// <summary>Installs or upgrades Flyleaf and starts it. Anything that fails is undone.</summary>
    /// <param name="cancellationToken">Cancels the install.</param>
    /// <returns>A task that completes when Flyleaf is installed and running.</returns>
    public static async Task InstallAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ExtractPayloadAsync(cancellationToken);
            var application = Path.Combine(InstallDirectory, ApplicationName);

            RegisterForCurrentUser(application);
            TryCreateShortcut(application);
            await StartAndCheckAsync(application, cancellationToken);
        }
        catch
        {
            Remove();
            throw;
        }
    }

    /// <summary>Removes everything Flyleaf added, except the user's notes.</summary>
    public static void Remove()
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
        RemoveInstallDirectory();
    }

    // Extracted beside the real folder first, so that a failed upgrade leaves the current copy working.
    private static async Task ExtractPayloadAsync(CancellationToken cancellationToken)
    {
        var incoming = InstallDirectory + ".new";
        if (Directory.Exists(incoming))
        {
            Directory.Delete(incoming, recursive: true);
        }

        Directory.CreateDirectory(incoming);
        await using (var payload = typeof(UserInstaller).Assembly.GetManifestResourceStream(PayloadResource)!)
        {
            using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
            archive.ExtractToDirectory(incoming);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (Directory.Exists(InstallDirectory))
        {
            Directory.Delete(InstallDirectory, recursive: true);
        }

        Directory.Move(incoming, InstallDirectory);
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

    private static async Task StartAndCheckAsync(string application, CancellationToken cancellationToken)
    {
        Process process;
        try
        {
            process = Process.Start(new ProcessStartInfo(application) { UseShellExecute = false, WorkingDirectory = InstallDirectory })!;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is ErrorAccessDisabledByPolicy or ErrorSystemIntegrityPolicyViolation)
        {
            throw new InvalidOperationException(BlockedMessage, ex);
        }

        using (process)
        {
            await Task.Delay(StartCheckTime, cancellationToken);
            if (process.HasExited && process.ExitCode != 0)
            {
                throw new InvalidOperationException(BlockedMessage);
            }
        }
    }

    // The running uninstaller can't delete its own file, so Windows' command processor finishes the job just after it exits.
    private static void RemoveInstallDirectory()
    {
        if (!Directory.Exists(InstallDirectory))
        {
            return;
        }

        var isInside = Environment.ProcessPath?.StartsWith(InstallDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) == true;
        if (!isInside)
        {
            Directory.Delete(InstallDirectory, recursive: true);
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
