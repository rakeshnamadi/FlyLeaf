using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace FlyleafMenu;

[GeneratedComInterface]
[Guid("00000001-0000-0000-C000-000000000046")]
internal unsafe partial interface IClassFactory
{
    [PreserveSig]
    int CreateInstance(nint outer, Guid* iid, nint* instance);

    [PreserveSig]
    int LockServer(int shouldLock);
}

[GeneratedComInterface]
[Guid("A08CE4D0-FA25-44AB-B57C-C7B1C323E0B9")]
internal unsafe partial interface IExplorerCommand
{
    [PreserveSig]
    int GetTitle(nint items, char** name);

    [PreserveSig]
    int GetIcon(nint items, char** icon);

    [PreserveSig]
    int GetToolTip(nint items, char** tip);

    [PreserveSig]
    int GetCanonicalName(Guid* name);

    [PreserveSig]
    int GetState(nint items, int okToBeSlow, uint* state);

    [PreserveSig]
    int Invoke(nint items, nint bindContext);

    [PreserveSig]
    int GetFlags(uint* flags);

    [PreserveSig]
    int EnumSubCommands(nint* commands);
}
