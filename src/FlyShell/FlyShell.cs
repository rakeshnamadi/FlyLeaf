using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace FlyShell;

/// <summary>The functions Windows calls on the DLL itself to create COM objects and to register it.</summary>
public static unsafe class Exports
{
    /// <summary>The class ID of the tooltip handler.</summary>
    internal static readonly Guid HandlerClassId = new("7B1E4F52-9C3A-4D86-A0E7-5F2C8D61B3A4");

    internal const int S_OK = 0;
    internal const int S_FALSE = 1;
    internal const int E_NOTIMPL = unchecked((int)0x80004001);
    internal const int E_NOINTERFACE = unchecked((int)0x80004002);
    internal const int E_POINTER = unchecked((int)0x80004003);
    internal const int E_FAIL = unchecked((int)0x80004005);
    internal const int E_INVALIDARG = unchecked((int)0x80070057);
    internal const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);
    internal const int CLASS_E_CLASSNOTAVAILABLE = unchecked((int)0x80040111);

    internal static StrategyBasedComWrappers ComWrappers { get; } = new();

    /// <summary>Returns the class factory for the tooltip handler.</summary>
    /// <param name="classId">The requested class.</param>
    /// <param name="iid">The requested interface.</param>
    /// <param name="result">Receives the interface pointer.</param>
    /// <returns>An HRESULT.</returns>
    [UnmanagedCallersOnly(EntryPoint = "DllGetClassObject")]
    public static int DllGetClassObject(Guid* classId, Guid* iid, nint* result)
    {
        if (result is null)
        {
            return E_POINTER;
        }

        *result = 0;
        return *classId == HandlerClassId ? QueryInterface(new ClassFactory(), iid, result) : CLASS_E_CLASSNOTAVAILABLE;
    }

    /// <summary>Tells Windows the DLL must stay loaded, because a Native AOT library cannot be unloaded.</summary>
    /// <returns>Always S_FALSE.</returns>
    [UnmanagedCallersOnly(EntryPoint = "DllCanUnloadNow")]
    public static int DllCanUnloadNow() => S_FALSE;

    /// <summary>Writes the registry keys that connect the handler to Explorer. Called by regsvr32.</summary>
    /// <returns>An HRESULT.</returns>
    [UnmanagedCallersOnly(EntryPoint = "DllRegisterServer")]
    public static int DllRegisterServer()
    {
        try
        {
            ShellRegistration.Register(GetModulePath());
            return S_OK;
        }
        catch (Exception ex)
        {
            return ex.HResult;
        }
    }

    /// <summary>Removes the registry keys. Called by regsvr32 /u.</summary>
    /// <returns>An HRESULT.</returns>
    [UnmanagedCallersOnly(EntryPoint = "DllUnregisterServer")]
    public static int DllUnregisterServer()
    {
        try
        {
            ShellRegistration.Unregister();
            return S_OK;
        }
        catch (Exception ex)
        {
            return ex.HResult;
        }
    }

    internal static int QueryInterface(object instance, Guid* iid, nint* result)
    {
        var unknown = ComWrappers.GetOrCreateComInterfaceForObject(instance, CreateComInterfaceFlags.None);
        try
        {
            return Marshal.QueryInterface(unknown, in *iid, out *result);
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    private static string GetModulePath()
    {
        const uint FromAddress = 0x4;
        const uint KeepRefCount = 0x2;

        var address = (nint)(delegate* unmanaged<int>)&DllCanUnloadNow;
        if (!NativeMethods.GetModuleHandleExW(FromAddress | KeepRefCount, address, out var module))
        {
            throw new InvalidOperationException();
        }

        var buffer = new char[32768];
        fixed (char* start = buffer)
        {
            var length = NativeMethods.GetModuleFileNameW(module, start, (uint)buffer.Length);
            return length == 0 ? throw new InvalidOperationException() : new string(start, 0, (int)length);
        }
    }
}

[GeneratedComClass]
internal sealed unsafe partial class ClassFactory : IClassFactory
{
    public int CreateInstance(nint outer, Guid* iid, nint* instance)
    {
        if (instance is null)
        {
            return Exports.E_POINTER;
        }

        *instance = 0;
        if (outer != 0)
        {
            return Exports.CLASS_E_NOAGGREGATION;
        }

        try
        {
            return Exports.QueryInterface(new InfoTipHandler(), iid, instance);
        }
        catch (Exception ex)
        {
            return ex.HResult;
        }
    }

    public int LockServer(int shouldLock) => Exports.S_OK;
}

/// <summary>Explorer creates one of these per item: it calls Load with the path, then GetInfoTip.</summary>
[GeneratedComClass]
internal sealed unsafe partial class InfoTipHandler : IPersistFile, IQueryInfo
{
    // The longest tip Explorer displays (INFOTIPSIZE).
    private const int MaxTipLength = 1024;

    private string? _path;

    public int GetClassID(Guid* classId)
    {
        if (classId is null)
        {
            return Exports.E_POINTER;
        }

        *classId = Exports.HandlerClassId;
        return Exports.S_OK;
    }

    public int IsDirty() => Exports.S_FALSE;

    public int Load(char* fileName, uint mode)
    {
        if (fileName is null)
        {
            return Exports.E_INVALIDARG;
        }

        _path = new string(fileName);
        return Exports.S_OK;
    }

    public int Save(char* fileName, int remember) => Exports.E_NOTIMPL;

    public int SaveCompleted(char* fileName) => Exports.E_NOTIMPL;

    public int GetCurFile(char** fileName) => Exports.E_NOTIMPL;

    public int GetInfoTip(uint flags, char** tip)
    {
        if (tip is null)
        {
            return Exports.E_POINTER;
        }

        *tip = null;

        // Anything unexpected must stay here: an exception escaping into Explorer could take it down.
        try
        {
            var note = _path is null ? null : NoteClient.TryGetNote(_path);
            if (string.IsNullOrEmpty(note))
            {
                return Exports.S_FALSE;
            }

            if (note.Length > MaxTipLength)
            {
                note = string.Concat(note.AsSpan(0, MaxTipLength - 1), "…");
            }

            *tip = (char*)Marshal.StringToCoTaskMemUni(note);
            return Exports.S_OK;
        }
        catch (Exception)
        {
            return Exports.S_FALSE;
        }
    }

    public int GetInfoFlags(uint* flags)
    {
        if (flags is null)
        {
            return Exports.E_POINTER;
        }

        *flags = 0;
        return Exports.S_OK;
    }
}

internal static unsafe partial class NativeMethods
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetModuleHandleExW(uint flags, nint address, out nint module);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint GetModuleFileNameW(nint module, char* fileName, uint size);

    [LibraryImport("shell32.dll")]
    internal static partial void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);
}
