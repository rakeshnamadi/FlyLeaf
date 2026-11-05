using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace FlyleafMenu;

/// <summary>The functions Windows calls on the DLL itself to create the menu command.</summary>
public static unsafe class Exports
{
    private const int CLASS_E_CLASSNOTAVAILABLE = unchecked((int)0x80040111);

    /// <summary>The class ID of the command, which must match the package manifest.</summary>
    internal static readonly Guid CommandClassId = new("3D9A6C21-5E4B-4F07-9B12-A8C47E6D1F35");

    internal static StrategyBasedComWrappers ComWrappers { get; } = new();

    /// <summary>Returns the class factory for the menu command.</summary>
    /// <param name="classId">The requested class.</param>
    /// <param name="iid">The requested interface.</param>
    /// <param name="result">Receives the interface pointer.</param>
    /// <returns>An HRESULT.</returns>
    [UnmanagedCallersOnly(EntryPoint = "DllGetClassObject")]
    public static int DllGetClassObject(Guid* classId, Guid* iid, nint* result)
    {
        if (result is null)
        {
            return Native.E_POINTER;
        }

        *result = 0;
        return *classId == CommandClassId ? Native.QueryInterface(new ClassFactory(), iid, result) : CLASS_E_CLASSNOTAVAILABLE;
    }

    /// <summary>Tells Windows the DLL must stay loaded, because a Native AOT library cannot be unloaded.</summary>
    /// <returns>Always S_FALSE.</returns>
    [UnmanagedCallersOnly(EntryPoint = "DllCanUnloadNow")]
    public static int DllCanUnloadNow() => Native.S_FALSE;

    internal static string ModuleDirectory()
    {
        const uint FromAddress = 0x4;
        const uint KeepRefCount = 0x2;

        var address = (nint)(delegate* unmanaged<int>)&DllCanUnloadNow;
        if (!Native.GetModuleHandleExW(FromAddress | KeepRefCount, address, out var module))
        {
            throw new InvalidOperationException();
        }

        var buffer = new char[32768];
        fixed (char* start = buffer)
        {
            var length = Native.GetModuleFileNameW(module, start, (uint)buffer.Length);
            return length == 0 ? throw new InvalidOperationException() : Path.GetDirectoryName(new string(start, 0, (int)length))!;
        }
    }
}

[GeneratedComClass]
internal sealed unsafe partial class ClassFactory : IClassFactory
{
    public int CreateInstance(nint outer, Guid* iid, nint* instance)
    {
        const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);

        if (instance is null)
        {
            return Native.E_POINTER;
        }

        *instance = 0;
        if (outer != 0)
        {
            return CLASS_E_NOAGGREGATION;
        }

        try
        {
            return Native.QueryInterface(new AddNoteCommand(), iid, instance);
        }
        catch (Exception ex)
        {
            return ex.HResult;
        }
    }

    public int LockServer(int shouldLock) => Native.S_OK;
}

/// <summary>"Add Flyleaf note": opens Flyleaf on each selected file or folder.</summary>
[GeneratedComClass]
internal sealed unsafe partial class AddNoteCommand : IExplorerCommand
{
    private const string Title = "Add Flyleaf note";
    private const int E_NOTIMPL = unchecked((int)0x80004001);
    private const uint ECS_ENABLED = 0;
    private const uint ECF_DEFAULT = 0;
    private const uint SIGDN_FILESYSPATH = 0x80058000;
    private const int ASFW_ANY = -1;

    // Selecting thousands of files shouldn't start thousands of processes.
    private const int MaxItems = 16;

    public int GetTitle(nint items, char** name) => ReturnString(Title, name);

    public int GetIcon(nint items, char** icon)
    {
        try
        {
            return ReturnString(Path.Combine(Exports.ModuleDirectory(), "Flyleaf.exe") + ",0", icon);
        }
        catch (Exception ex)
        {
            return ex.HResult;
        }
    }

    public int GetToolTip(nint items, char** tip) => E_NOTIMPL;

    public int GetCanonicalName(Guid* name) => E_NOTIMPL;

    public int GetState(nint items, int okToBeSlow, uint* state)
    {
        if (state is null)
        {
            return Native.E_POINTER;
        }

        *state = ECS_ENABLED;
        return Native.S_OK;
    }

    public int GetFlags(uint* flags)
    {
        if (flags is null)
        {
            return Native.E_POINTER;
        }

        *flags = ECF_DEFAULT;
        return Native.S_OK;
    }

    public int EnumSubCommands(nint* commands) => E_NOTIMPL;

    public int Invoke(nint items, nint bindContext)
    {
        // Anything unexpected must stay here: this runs on Explorer's behalf.
        try
        {
            var executable = FindExecutable();

            // Explorer gave this process the right to bring a window forward, so pass that along to Flyleaf.
            Native.AllowSetForegroundWindow(ASFW_ANY);

            foreach (var path in GetPaths(items))
            {
                var start = new ProcessStartInfo(executable) { UseShellExecute = false };
                start.ArgumentList.Add("--note");
                start.ArgumentList.Add(path);
                Process.Start(start)?.Dispose();
            }

            return Native.S_OK;
        }
        catch (Exception ex)
        {
            return ex.HResult;
        }
    }

    // A package's own folder can't be launched directly, so an installed app is started through its execution alias.
    private static string FindExecutable()
    {
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "flyleaf.exe");
        return File.Exists(alias) ? alias : Path.Combine(Exports.ModuleDirectory(), "Flyleaf.exe");
    }

    private static List<string> GetPaths(nint items)
    {
        // Slots after the three IUnknown methods, in the order Windows declares them.
        const int ShellItemArray_GetCount = 7;
        const int ShellItemArray_GetItemAt = 8;
        const int ShellItem_GetDisplayName = 5;

        var paths = new List<string>();
        if (items == 0)
        {
            return paths;
        }

        uint count;
        Check(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(items, ShellItemArray_GetCount))(items, &count));

        for (uint i = 0; i < Math.Min(count, MaxItems); i++)
        {
            nint item;
            Check(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(items, ShellItemArray_GetItemAt))(items, i, &item));
            try
            {
                char* name = null;

                // Items with no real path, such as a recycle bin entry, fail here and are skipped.
                if (((delegate* unmanaged[Stdcall]<nint, uint, char**, int>)Slot(item, ShellItem_GetDisplayName))(item, SIGDN_FILESYSPATH, &name) >= 0 && name is not null)
                {
                    paths.Add(new string(name));
                    Marshal.FreeCoTaskMem((nint)name);
                }
            }
            finally
            {
                Marshal.Release(item);
            }
        }

        return paths;
    }

    private static nint Slot(nint comObject, int slot) => (*(nint**)comObject)[slot];

    private static void Check(int result)
    {
        if (result < 0)
        {
            Marshal.ThrowExceptionForHR(result);
        }
    }

    private static int ReturnString(string text, char** result)
    {
        if (result is null)
        {
            return Native.E_POINTER;
        }

        *result = (char*)Marshal.StringToCoTaskMemUni(text);
        return Native.S_OK;
    }
}

internal static unsafe partial class Native
{
    internal const int S_OK = 0;
    internal const int S_FALSE = 1;
    internal const int E_POINTER = unchecked((int)0x80004003);

    internal static int QueryInterface(object instance, Guid* iid, nint* result)
    {
        var unknown = Exports.ComWrappers.GetOrCreateComInterfaceForObject(instance, CreateComInterfaceFlags.None);
        try
        {
            return Marshal.QueryInterface(unknown, in *iid, out *result);
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetModuleHandleExW(uint flags, nint address, out nint module);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint GetModuleFileNameW(nint module, char* fileName, uint size);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AllowSetForegroundWindow(int processId);
}
