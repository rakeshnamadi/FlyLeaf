using Microsoft.Win32;

namespace FlyShell;

/// <summary>Creates and removes the registry keys that connect the handler to Explorer.</summary>
internal static class ShellRegistration
{
    private const string QueryInfoHandlerKey = @"shellex\{00021500-0000-0000-C000-000000000046}";
    private const string ApprovedKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved";
    private const string PreviousHandlerValuePrefix = "Previous-";
    private const int SHCNE_ASSOCCHANGED = 0x08000000;

    // "*" covers every file and "Directory" covers every folder.
    private static readonly string[] Targets = ["*", "Directory"];

    private static string ClassId => Exports.HandlerClassId.ToString("B").ToUpperInvariant();

    private static string ClassKey => $@"CLSID\{ClassId}";

    /// <summary>Registers the handler, remembering any handler it replaces so that it can be restored.</summary>
    /// <param name="modulePath">The full path of this DLL.</param>
    public static void Register(string modulePath)
    {
        using var classes = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Classes");

        using (var classKey = classes.CreateSubKey(ClassKey))
        {
            classKey.SetValue(null, "Flyleaf tooltip handler");
            using var server = classKey.CreateSubKey("InprocServer32");
            server.SetValue(null, modulePath);
            server.SetValue("ThreadingModel", "Apartment");

            foreach (var target in Targets)
            {
                using var handler = classes.CreateSubKey($@"{target}\{QueryInfoHandlerKey}");
                if (handler.GetValue(null) is string previous && !previous.Equals(ClassId, StringComparison.OrdinalIgnoreCase))
                {
                    classKey.SetValue(PreviousHandlerValuePrefix + target, previous);
                }

                handler.SetValue(null, ClassId);
            }
        }

        using (var approved = Registry.LocalMachine.CreateSubKey(ApprovedKey))
        {
            approved.SetValue(ClassId, "Flyleaf tooltip handler");
        }

        NotifyShell();
    }

    /// <summary>Removes the handler and restores any handler it replaced.</summary>
    public static void Unregister()
    {
        using var classes = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Classes");

        using (var classKey = classes.OpenSubKey(ClassKey))
        {
            foreach (var target in Targets)
            {
                var previous = classKey?.GetValue(PreviousHandlerValuePrefix + target) as string;
                var handlerPath = $@"{target}\{QueryInfoHandlerKey}";

                if (previous is not null)
                {
                    using var handler = classes.CreateSubKey(handlerPath);
                    handler.SetValue(null, previous);
                }
                else
                {
                    using var handler = classes.OpenSubKey(handlerPath);
                    if (string.Equals(handler?.GetValue(null) as string, ClassId, StringComparison.OrdinalIgnoreCase))
                    {
                        classes.DeleteSubKey(handlerPath, throwOnMissingSubKey: false);
                    }
                }
            }
        }

        classes.DeleteSubKeyTree(ClassKey, throwOnMissingSubKey: false);

        using (var approved = Registry.LocalMachine.OpenSubKey(ApprovedKey, writable: true))
        {
            approved?.DeleteValue(ClassId, throwOnMissingValue: false);
        }

        NotifyShell();
    }

    private static void NotifyShell() => NativeMethods.SHChangeNotify(SHCNE_ASSOCCHANGED, 0, 0, 0);
}
