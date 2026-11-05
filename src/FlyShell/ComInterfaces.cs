using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace FlyShell;

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
[Guid("0000010C-0000-0000-C000-000000000046")]
internal unsafe partial interface IPersist
{
    [PreserveSig]
    int GetClassID(Guid* classId);
}

[GeneratedComInterface]
[Guid("0000010B-0000-0000-C000-000000000046")]
internal unsafe partial interface IPersistFile : IPersist
{
    [PreserveSig]
    int IsDirty();

    [PreserveSig]
    int Load(char* fileName, uint mode);

    [PreserveSig]
    int Save(char* fileName, int remember);

    [PreserveSig]
    int SaveCompleted(char* fileName);

    [PreserveSig]
    int GetCurFile(char** fileName);
}

[GeneratedComInterface]
[Guid("00021500-0000-0000-C000-000000000046")]
internal unsafe partial interface IQueryInfo
{
    [PreserveSig]
    int GetInfoTip(uint flags, char** tip);

    [PreserveSig]
    int GetInfoFlags(uint* flags);
}
