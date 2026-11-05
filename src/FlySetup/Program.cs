using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FlySetup;
using Microsoft.Win32;

// Setup is usually run from a Downloads folder, so never load a DLL planted beside it.
NativeMethods.SetDefaultDllDirectories(NativeMethods.LOAD_LIBRARY_SEARCH_SYSTEM32);

return args switch
{
    ["--elevated-install", var notesDirectory, var userSid, var logPath] =>
        await RunElevatedAsync(logPath, ct => MachineInstaller.InstallAsync(notesDirectory, userSid, ct)),
    ["--elevated-uninstall", var logPath] =>
        await RunElevatedAsync(logPath, MachineInstaller.UninstallAsync),
    _ when MachineInstaller.HasPayload => await InstallAsync(),
    _ => await UninstallAsync(),
};

static async Task<int> RunElevatedAsync(string logPath, Func<CancellationToken, Task> action)
{
    try
    {
        await action(CancellationToken.None);
        return 0;
    }
    catch (Exception ex)
    {
        await File.WriteAllTextAsync(logPath, ex.Message);
        return 1;
    }
}

static async Task<int> InstallAsync()
{
    if (!Ui.Confirm($"Install Flyleaf for {Environment.UserName}?\n\nA note on every file: hover over a file in Explorer to read its note.\n\nWindows will ask for administrator approval."))
    {
        return 0;
    }

    // Read these now, as the elevated copy may be running under a different administrator account.
    var notesDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Flyleaf");
    var userSid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;

    try
    {
        Directory.CreateDirectory(notesDirectory);
        StopTrayApp();

        if (!await RunElevatedCopyAsync(["--elevated-install", notesDirectory, userSid]))
        {
            return 1;
        }

        var tray = Path.Combine(MachineInstaller.InstallDirectory, "Flyleaf.exe");
        using (var run = Registry.CurrentUser.CreateSubKey(MachineInstaller.RunKey))
        {
            run.SetValue("Flyleaf", $"\"{tray}\"");
        }

        Process.Start(new ProcessStartInfo(tray) { UseShellExecute = true })?.Dispose();
        Ui.Info("Flyleaf is installed.\n\nOpen it from its icon in the system tray, add a note to a file, then hover over that file in Explorer.");
        return 0;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
    {
        Ui.Error($"Flyleaf could not be installed.\n\n{ex.Message}");
        return 1;
    }
}

static async Task<int> UninstallAsync()
{
    if (!Ui.Confirm("Remove Flyleaf from this PC?"))
    {
        return 0;
    }

    try
    {
        StopTrayApp();

        if (!await RunElevatedCopyAsync(["--elevated-uninstall"]))
        {
            return 1;
        }

        using (var run = Registry.CurrentUser.OpenSubKey(MachineInstaller.RunKey, writable: true))
        {
            run?.DeleteValue("Flyleaf", throwOnMissingValue: false);
        }

        var notesDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Flyleaf");
        if (Directory.Exists(notesDirectory))
        {
            if (Ui.AskDefaultNo($"Flyleaf has been removed.\n\nAlso permanently delete your notes?\n{notesDirectory}\n\nChoose No to keep them."))
            {
                Directory.Delete(notesDirectory, recursive: true);
            }
        }
        else
        {
            Ui.Info("Flyleaf has been removed.");
        }

        return 0;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
    {
        Ui.Error($"Flyleaf could not be removed.\n\n{ex.Message}");
        return 1;
    }
}

static async Task<bool> RunElevatedCopyAsync(string[] arguments)
{
    const int ErrorCancelled = 1223;

    var logPath = Path.GetTempFileName();
    try
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.ArgumentList.Add(logPath);

        using var process = Process.Start(start)!;
        await process.WaitForExitAsync();
        if (process.ExitCode == 0)
        {
            return true;
        }

        var details = await File.ReadAllTextAsync(logPath);
        Ui.Error(details.Length > 0 ? details : $"Setup stopped with error code {process.ExitCode}.");
        return false;
    }
    catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
    {
        Ui.Info("Nothing was changed, because administrator approval was declined.");
        return false;
    }
    finally
    {
        File.Delete(logPath);
    }
}

static void StopTrayApp()
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

namespace FlySetup
{
    internal static partial class NativeMethods
    {
        internal const uint LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x800;

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetDefaultDllDirectories(uint flags);

        [LibraryImport("kernel32.dll", EntryPoint = "MoveFileExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool MoveFileEx(string existing, string? replacement, uint flags);

        [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial int MessageBox(nint owner, string text, string caption, uint type);
    }

    internal static class Ui
    {
        private const uint MB_OK = 0, MB_OKCANCEL = 1, MB_YESNO = 4;
        private const uint MB_ICONERROR = 0x10, MB_ICONQUESTION = 0x20, MB_ICONWARNING = 0x30, MB_ICONINFORMATION = 0x40;
        private const uint MB_DEFBUTTON2 = 0x100, MB_SETFOREGROUND = 0x10000;
        private const int IDOK = 1, IDYES = 6;

        public static bool Confirm(string text) => Show(text, MB_OKCANCEL | MB_ICONQUESTION) == IDOK;

        public static bool AskDefaultNo(string text) => Show(text, MB_YESNO | MB_ICONWARNING | MB_DEFBUTTON2) == IDYES;

        public static void Info(string text) => Show(text, MB_OK | MB_ICONINFORMATION);

        public static void Error(string text) => Show(text, MB_OK | MB_ICONERROR);

        private static int Show(string text, uint type) => NativeMethods.MessageBox(0, text, "Flyleaf Setup", type | MB_SETFOREGROUND);
    }
}
