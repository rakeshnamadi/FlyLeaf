using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;

namespace FlySetup;

/// <summary>The machine-wide steps of installing and removing Flyleaf. Everything here runs elevated.</summary>
internal static class MachineInstaller
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private const string Version = "1.0.0";
    private const string ServiceName = "FlyWatcher";
    private const string PayloadPrefix = "payload/";
    private const string UninstallerName = "Uninstall-Flyleaf.exe";
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Flyleaf";
    private const string EventSourceKey = @"SYSTEM\CurrentControlSet\Services\EventLog\Application\Flyleaf";
    private const string NotesDirectoryValue = "NotesDirectory";
    private const string LocalServiceAce = "*S-1-5-19";
    private const uint MOVEFILE_DELAY_UNTIL_REBOOT = 4;

    // The usual Windows messages for "the code-integrity policy refused this file".
    private const int ErrorAccessDisabledByPolicy = 1260;
    private const int ErrorSystemIntegrityPolicyViolation = 4551;

    private const string BlockedMessage =
        "Windows blocked Flyleaf from running, and the changes have been undone.\n\n" +
        "This usually means Smart App Control is on and this copy of Flyleaf is not code-signed. Ask whoever shared it for a signed copy.";

    public static string InstallDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Flyleaf");

    /// <summary>True for Flyleaf-Setup.exe, and false for the uninstaller, which is the same program with nothing embedded.</summary>
    public static bool HasPayload { get; } =
        typeof(MachineInstaller).Assembly.GetManifestResourceNames().Any(name => name.StartsWith(PayloadPrefix, StringComparison.Ordinal));

    public static async Task InstallAsync(string notesDirectory, string userSid, CancellationToken cancellationToken)
    {
        try
        {
            await RemoveServiceAsync(cancellationToken);
            await ExtractPayloadAsync(cancellationToken);
            RegisterEventSource();
            await GrantNotesAccessAsync(notesDirectory, cancellationToken);
            await CreateAndStartServiceAsync(notesDirectory, userSid, cancellationToken);
            await RegisterShellExtensionAsync(cancellationToken);
            RegisterUninstaller(notesDirectory);
        }
        catch
        {
            await RemoveMachinePartsAsync(notesDirectory, CancellationToken.None);
            throw;
        }
    }

    public static async Task UninstallAsync(CancellationToken cancellationToken)
    {
        using var key = Registry.LocalMachine.OpenSubKey(UninstallKey);
        await RemoveMachinePartsAsync(key?.GetValue(NotesDirectoryValue) as string, cancellationToken);
    }

    /// <summary>Builds the service command line. Both paths can contain spaces, so each is quoted.</summary>
    public static string BuildServiceCommand(string executable, string notesDirectory, string userSid) =>
        $"\"{executable}\" --notes \"{Path.Combine(notesDirectory, "notes.json")}\" --user-sid {userSid}";

    private static async Task ExtractPayloadAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(InstallDirectory);
        DeleteStaleFiles();

        var assembly = typeof(MachineInstaller).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(name => name.StartsWith(PayloadPrefix, StringComparison.Ordinal)))
        {
            var target = Path.Combine(InstallDirectory, resource[PayloadPrefix.Length..]);
            MoveAsideIfInUse(target);

            await using var source = assembly.GetManifestResourceStream(resource)!;
            await using var destination = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            await source.CopyToAsync(destination, cancellationToken);
        }
    }

    // Explorer keeps the shell extension loaded until it exits, so during an upgrade the old DLL can be renamed but not overwritten.
    private static void MoveAsideIfInUse(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            File.Move(path, $"{path}.{Guid.NewGuid():N}.old");
        }
    }

    private static void DeleteStaleFiles()
    {
        foreach (var file in Directory.EnumerateFiles(InstallDirectory, "*.old"))
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still in use. The next upgrade tries again.
            }
        }
    }

    private static void RegisterEventSource()
    {
        using var key = Registry.LocalMachine.CreateSubKey(EventSourceKey);
        key.SetValue("EventMessageFile", @"%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\EventLogMessages.dll", RegistryValueKind.ExpandString);
        key.SetValue("TypesSupported", 7, RegistryValueKind.DWord);
    }

    // The service runs as the low-privilege LocalService account, which may read only this one folder of the profile.
    private static async Task GrantNotesAccessAsync(string notesDirectory, CancellationToken cancellationToken)
    {
        var (exitCode, output) = await RunToolAsync("icacls.exe", [notesDirectory, "/grant", $"{LocalServiceAce}:(OI)(CI)RX", "/Q"], cancellationToken);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Could not give the service access to the notes folder.\n\n{output}");
        }
    }

    private static async Task CreateAndStartServiceAsync(string notesDirectory, string userSid, CancellationToken cancellationToken)
    {
        var command = BuildServiceCommand(Path.Combine(InstallDirectory, "FlyWatcher.exe"), notesDirectory, userSid);

        var (createCode, createOutput) = await RunToolAsync(
            "sc.exe",
            ["create", ServiceName, "binPath=", command, "start=", "auto", "obj=", @"NT AUTHORITY\LocalService", "DisplayName=", "Flyleaf Watcher"],
            cancellationToken);
        if (createCode != 0)
        {
            throw new InvalidOperationException($"Could not create the FlyWatcher service.\n\n{createOutput}");
        }

        await RunToolAsync("sc.exe", ["description", ServiceName, "Answers Explorer tooltip lookups for Flyleaf notes."], cancellationToken);
        await RunToolAsync("sc.exe", ["failure", ServiceName, "reset=", "86400", "actions=", "restart/5000/restart/5000/restart/30000"], cancellationToken);

        var (startCode, startOutput) = await RunToolAsync("sc.exe", ["start", ServiceName], cancellationToken);
        if (startCode is ErrorAccessDisabledByPolicy or ErrorSystemIntegrityPolicyViolation)
        {
            throw new InvalidOperationException(BlockedMessage);
        }

        if (startCode != 0)
        {
            throw new InvalidOperationException($"Could not start the FlyWatcher service.\n\n{startOutput}");
        }

        if (!await WaitForServiceStateAsync("RUNNING", cancellationToken))
        {
            throw new InvalidOperationException("The FlyWatcher service did not start in time. See the Application event log.");
        }
    }

    private static async Task RegisterShellExtensionAsync(CancellationToken cancellationToken)
    {
        var (exitCode, _) = await RunToolAsync("regsvr32.exe", ["/s", Path.Combine(InstallDirectory, "FlyShell.dll")], cancellationToken);
        if (exitCode == 3)
        {
            // regsvr32's code for "the DLL could not be loaded", which is what a policy block looks like from here.
            throw new InvalidOperationException(BlockedMessage);
        }

        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Could not register the Explorer extension (regsvr32 code {exitCode}).");
        }
    }

    private static void RegisterUninstaller(string notesDirectory)
    {
        var uninstaller = Path.Combine(InstallDirectory, UninstallerName);
        var sizeInKilobytes = Directory.EnumerateFiles(InstallDirectory).Sum(file => new FileInfo(file).Length) / 1024;

        using var key = Registry.LocalMachine.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", "Flyleaf");
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", "Flyleaf");
        key.SetValue("InstallLocation", InstallDirectory);
        key.SetValue("DisplayIcon", Path.Combine(InstallDirectory, "Flyleaf.exe"));
        key.SetValue("UninstallString", $"\"{uninstaller}\"");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", (int)sizeInKilobytes, RegistryValueKind.DWord);
        key.SetValue(NotesDirectoryValue, notesDirectory);
    }

    private static async Task RemoveMachinePartsAsync(string? notesDirectory, CancellationToken cancellationToken)
    {
        var dll = Path.Combine(InstallDirectory, "FlyShell.dll");
        if (File.Exists(dll))
        {
            await RunToolAsync("regsvr32.exe", ["/s", "/u", dll], cancellationToken);
        }

        await RemoveServiceAsync(cancellationToken);
        Registry.LocalMachine.DeleteSubKeyTree(EventSourceKey, throwOnMissingSubKey: false);

        if (notesDirectory is not null && Directory.Exists(notesDirectory))
        {
            await RunToolAsync("icacls.exe", [notesDirectory, "/remove", LocalServiceAce, "/Q"], cancellationToken);
        }

        DeleteInstallDirectory();
        Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
    }

    // The running uninstaller, and the DLL if Explorer has it loaded, can only be removed at the next restart.
    private static void DeleteInstallDirectory()
    {
        if (!Directory.Exists(InstallDirectory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(InstallDirectory))
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                NativeMethods.MoveFileEx(file, null, MOVEFILE_DELAY_UNTIL_REBOOT);
            }
        }

        try
        {
            Directory.Delete(InstallDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            NativeMethods.MoveFileEx(InstallDirectory, null, MOVEFILE_DELAY_UNTIL_REBOOT);
        }
    }

    private static async Task RemoveServiceAsync(CancellationToken cancellationToken)
    {
        const int ErrorServiceDoesNotExist = 1060;

        var (queryCode, _) = await RunToolAsync("sc.exe", ["query", ServiceName], cancellationToken);
        if (queryCode == ErrorServiceDoesNotExist)
        {
            return;
        }

        await RunToolAsync("sc.exe", ["stop", ServiceName], cancellationToken);
        await WaitForServiceStateAsync("STOPPED", cancellationToken);
        await RunToolAsync("sc.exe", ["delete", ServiceName], cancellationToken);
    }

    private static async Task<bool> WaitForServiceStateAsync(string state, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            var (_, output) = await RunToolAsync("sc.exe", ["query", ServiceName], cancellationToken);
            if (output.Contains(state, StringComparison.Ordinal))
            {
                return true;
            }

            await Task.Delay(250, cancellationToken);
        }

        return false;
    }

    // Always the full System32 path, so that an elevated setup can't be tricked into running a look-alike tool.
    private static async Task<(int ExitCode, string Output)> RunToolAsync(string tool, string[] arguments, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, tool))
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return (process.ExitCode, output + await error);
        }
        catch (Win32Exception ex)
        {
            return (ex.NativeErrorCode, ex.Message);
        }
    }
}
