using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace Flyleaf;

/// <summary>An item in a folder window, and the file-system folders it could be in.</summary>
/// <param name="Folders">The candidate folders. The desktop shows two at once: the user's and the public one.</param>
/// <param name="DisplayName">The name Explorer shows for the item.</param>
/// <param name="Bounds">The item's position on screen, in physical pixels.</param>
internal sealed record HoverTarget(IReadOnlyList<string> Folders, string DisplayName, Rectangle Bounds);

/// <summary>
/// Works out which File Explorer item is at a screen position, using UI Automation for the item and the
/// Shell windows list for its folder. Views with no real folder, like Home and search results, return nothing.
/// Use from a single STA thread that isn't a UI thread, because these calls wait on Explorer.
/// </summary>
internal sealed class HoverDetector
{
    private const uint GA_ROOT = 2;
    private const int MaxAncestorsToCheck = 4;

    // The top-level browser of a Shell window, which on Windows 11 is one tab.
    private static readonly Guid SID_STopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid IID_IShellBrowser = new("000214E2-0000-0000-C000-000000000046");

    private object? _shell;

    /// <summary>Loads UI Automation and the Shell, which would otherwise delay the first tooltip.</summary>
    public void WarmUp()
    {
        try
        {
            AutomationElement.FromPoint(new System.Windows.Point(0, 0));
            ExplorerFolder(0);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException or MissingMethodException)
        {
            _shell = null;
        }
    }

    /// <summary>Finds the item at a screen position.</summary>
    /// <param name="position">The position, in physical pixels.</param>
    /// <returns>The item, or <see langword="null"/> if the position is not over a file or folder in a known folder.</returns>
    public HoverTarget? Detect(Point position)
    {
        var root = GetAncestor(WindowFromPoint(new POINT(position.X, position.Y)), GA_ROOT);
        var windowClass = GetClassName(root);
        var isExplorer = windowClass is "CabinetWClass" or "ExploreWClass";
        var isDesktop = windowClass is "Progman" or "WorkerW";
        if (!isExplorer && !isDesktop)
        {
            return null;
        }

        try
        {
            var item = FindListItem(AutomationElement.FromPoint(new System.Windows.Point(position.X, position.Y)));
            if (item is null)
            {
                return null;
            }

            var folders = isDesktop ? DesktopFolders() : ExplorerFolder(root);
            if (folders.Count == 0)
            {
                return null;
            }

            var bounds = item.Current.BoundingRectangle;
            return new HoverTarget(folders, item.Current.Name, new Rectangle((int)bounds.X, (int)bounds.Y, (int)bounds.Width, (int)bounds.Height));
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException or MissingMethodException or TargetInvocationException)
        {
            // Explorer changed the view or closed the window while we were asking.
            _shell = null;
            return null;
        }
    }

    // The element under the cursor may be the name text inside the row, so look a few levels up for the row itself.
    private static AutomationElement? FindListItem(AutomationElement? element)
    {
        var walker = TreeWalker.ControlViewWalker;
        for (var level = 0; element is not null && level < MaxAncestorsToCheck; level++)
        {
            if (element.Current.ControlType == ControlType.ListItem)
            {
                return element;
            }

            element = walker.GetParent(element);
        }

        return null;
    }

    private static List<string> DesktopFolders() =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
    ];

    private List<string> ExplorerFolder(nint root)
    {
        // A Windows 11 window can hold several tabs under one handle, and the topmost tab host is the visible one.
        var activeTab = FindWindowEx(root, 0, "ShellTabWindowClass", null);

        _shell ??= Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        string? fallback = null;

        foreach (var window in (IEnumerable)Invoke(_shell, "Windows", BindingFlags.InvokeMethod)!)
        {
            if (window is null || (nint)Convert.ToInt64(Invoke(window, "HWND")) != root)
            {
                continue;
            }

            var path = (string)Invoke(Invoke(Invoke(Invoke(window, "Document")!, "Folder")!, "Self")!, "Path")!;
            if (activeTab == 0 || GetTabHandle(window) == activeTab)
            {
                return ToFolderList(path);
            }

            fallback ??= path;
        }

        return fallback is null ? [] : ToFolderList(fallback);
    }

    // Late-bound COM, because these Shell objects only offer IDispatch without a generated interop assembly.
    private static object? Invoke(object target, string member, BindingFlags kind = BindingFlags.GetProperty) =>
        target.GetType().InvokeMember(member, kind, null, target, null);

    // Virtual locations report a value like "::{GUID}" instead of a path.
    private static List<string> ToFolderList(string path) => Path.IsPathFullyQualified(path) ? [path] : [];

    private static nint GetTabHandle(object window)
    {
        if (window is not IServiceProvider provider)
        {
            return 0;
        }

        var service = SID_STopLevelBrowser;
        var iid = IID_IShellBrowser;
        if (provider.QueryService(ref service, ref iid, out var browser) != 0 || browser == 0)
        {
            return 0;
        }

        try
        {
            return Marshal.GetObjectForIUnknown(browser) is IOleWindow oleWindow && oleWindow.GetWindow(out var handle) == 0 ? handle : 0;
        }
        finally
        {
            Marshal.Release(browser);
        }
    }

    private static string GetClassName(nint window)
    {
        var name = new StringBuilder(256);
        return GetClassNameW(window, name, name.Capacity) > 0 ? name.ToString() : "";
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct POINT(int X, int Y);

    [ComImport]
    [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid service, ref Guid iid, out nint result);
    }

    [ComImport]
    [Guid("00000114-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IOleWindow
    {
        [PreserveSig]
        int GetWindow(out nint window);
    }

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")]
    private static extern int GetClassNameW(nint window, StringBuilder name, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowExW")]
    private static extern nint FindWindowEx(nint parent, nint after, string className, string? windowName);
}
