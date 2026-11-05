using Microsoft.Win32;
using Windows.ApplicationModel;

namespace Flyleaf;

/// <summary>
/// The right-click menu item for copies that aren't installed from a package, such as trial and development builds.
/// An installed package gets its item from FlyleafMenu.dll and its manifest instead, which puts it in Windows 11's
/// main menu and removes it on uninstall. These registry verbs only appear under "Show more options".
/// </summary>
internal static class ShellMenu
{
    private const string VerbName = "FlyleafNote";

    // "*" covers every file and "Directory" covers every folder.
    private static readonly string[] Targets = ["*", "Directory"];

    /// <summary>Adds or refreshes the menu item, unless this copy is an installed package.</summary>
    public static void RegisterIfNotPackaged()
    {
        if (IsPackaged() || Environment.ProcessPath is not { } executable)
        {
            return;
        }

        try
        {
            foreach (var target in Targets)
            {
                using var verb = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{target}\shell\{VerbName}");
                verb.SetValue(null, "Add Flyleaf note");
                verb.SetValue("Icon", executable);

                using var command = verb.CreateSubKey("command");
                command.SetValue(null, $"\"{executable}\" --note \"%1\"");
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            // The app is still fully usable without the menu item.
        }
    }

    /// <summary>Removes the menu item.</summary>
    public static void Unregister()
    {
        foreach (var target in Targets)
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{target}\shell\{VerbName}", throwOnMissingSubKey: false);
        }
    }

    private static bool IsPackaged()
    {
        try
        {
            _ = Package.Current;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }
}
