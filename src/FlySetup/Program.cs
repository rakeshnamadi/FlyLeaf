using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FlySetup;

// Setup is usually run from a Downloads folder, so never load a DLL planted beside it.
NativeMethods.SetDefaultDllDirectories(NativeMethods.LOAD_LIBRARY_SEARCH_SYSTEM32);

// --silent shows no prompts, for scripted installs and automated tests.
var silent = args.Contains("--silent");
return UserInstaller.HasPayload ? await InstallAsync(silent) : Uninstall(silent);

static async Task<int> InstallAsync(bool silent)
{
    if (!silent && !Ui.Confirm($"Install Flyleaf for {Environment.UserName}?\n\nA note on every file: hover over a file in File Explorer to read its note."))
    {
        return 0;
    }

    try
    {
        StopTrayApp();
        await UserInstaller.InstallAsync(CancellationToken.None);

        if (!silent)
        {
            Ui.Info("Flyleaf is installed.\n\nTo add a note, right-click a file or folder, choose Show more options, then Add Flyleaf note.\n\nTo read it, rest your pointer on the item in File Explorer.");
        }

        return 0;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException or InvalidDataException)
    {
        if (!silent)
        {
            Ui.Error(ex.Message);
        }

        return 1;
    }
}

static int Uninstall(bool silent)
{
    if (!silent && !Ui.Confirm("Remove Flyleaf from this PC?"))
    {
        return 0;
    }

    try
    {
        StopTrayApp();
        UserInstaller.Remove();

        var notesDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Flyleaf");
        if (silent)
        {
            return 0;
        }

        if (!Directory.Exists(notesDirectory))
        {
            Ui.Info("Flyleaf has been removed.");
        }
        else if (Ui.AskDefaultNo($"Flyleaf has been removed.\n\nAlso permanently delete your notes?\n{notesDirectory}\n\nChoose No to keep them."))
        {
            Directory.Delete(notesDirectory, recursive: true);
        }

        return 0;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        if (!silent)
        {
            Ui.Error($"Flyleaf could not be removed.\n\n{ex.Message}");
        }

        return 1;
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

        [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
        internal static partial int MessageBox(nint owner, string text, string caption, uint type);

        [LibraryImport("ole32.dll")]
        internal static partial int CoInitializeEx(nint reserved, uint mode);

        [LibraryImport("ole32.dll")]
        internal static unsafe partial int CoCreateInstance(Guid* classId, nint outer, uint context, Guid* iid, nint* instance);
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
