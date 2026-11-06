using System.Runtime.InteropServices;

namespace FlySetup;

/// <summary>The Windows functions Setup uses. It's a native program, so it has no WinForms to call instead.</summary>
internal static unsafe partial class NativeMethods
{
    internal const uint LOAD_LIBRARY_SEARCH_SYSTEM32 = 0x800;

    internal const uint WM_DESTROY = 0x0002, WM_PAINT = 0x000F, WM_CLOSE = 0x0010, WM_ERASEBKGND = 0x0014, WM_SETFONT = 0x0030, WM_DRAWITEM = 0x002B;
    internal const uint WM_SETICON = 0x0080, WM_COMMAND = 0x0111, WM_TIMER = 0x0113, WM_CTLCOLORBTN = 0x0135, WM_CTLCOLORSTATIC = 0x0138, WM_DPICHANGED = 0x02E0, WM_APP = 0x8000;
    internal const uint BM_GETCHECK = 0x00F0;

    internal const uint WS_OVERLAPPED = 0, WS_CAPTION = 0xC00000, WS_SYSMENU = 0x80000, WS_CHILD = 0x40000000, WS_TABSTOP = 0x10000;
    internal const uint BS_OWNERDRAW = 0xB, BS_AUTOCHECKBOX = 0x3;
    internal const int SW_HIDE = 0, SW_SHOW = 5;
    internal const uint SWP_NOZORDER = 4, SWP_NOACTIVATE = 0x10;
    internal const uint SC_CLOSE = 0xF060, MF_BYCOMMAND = 0, MF_GRAYED = 1;
    internal const uint DT_WORDBREAK = 0x10, DT_CENTER = 1, DT_VCENTER = 4, DT_SINGLELINE = 0x20, DT_CALCRECT = 0x400, DT_NOPREFIX = 0x800, DT_RIGHT = 2;
    internal const uint ODS_SELECTED = 1, ODS_FOCUS = 0x10, ODS_NOFOCUSRECT = 0x200;
    internal const int TRANSPARENT = 1, SRCCOPY = 0xCC0020, FW_NORMAL = 400, FW_SEMIBOLD = 600, CLEARTYPE_QUALITY = 5;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SIZE
    {
        public int Width, Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public nint Window;
        public uint Message;
        public nint WParam, LParam;
        public uint Time;
        public POINT Point;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PAINTSTRUCT
    {
        public nint Dc;
        public int Erase;
        public RECT Paint;
        public int Restore, IncUpdate;
        public fixed byte Reserved[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DRAWITEMSTRUCT
    {
        public uint ControlType, ControlId, ItemId, ItemAction, ItemState;
        public nint Item, Dc;
        public RECT Bounds;
        public nuint ItemData;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WNDCLASSEXW
    {
        public uint Size, Style;
        public delegate* unmanaged<nint, uint, nint, nint, nint> WndProc;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background;
        public char* MenuName, ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GdiplusStartupInput
    {
        public uint Version;
        public nint Callback;
        public int SuppressBackgroundThread, SuppressExternalCodecs;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetDefaultDllDirectories(uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint GetModuleHandle(string? name);

    [LibraryImport("ole32.dll")]
    internal static partial int CoInitializeEx(nint reserved, uint mode);

    [LibraryImport("ole32.dll")]
    internal static partial int CoCreateInstance(Guid* classId, nint outer, uint context, Guid* iid, nint* instance);

    [LibraryImport("comctl32.dll")]
    internal static partial void InitCommonControls();

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmSetWindowAttribute(nint window, int attribute, int* value, int size);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW")]
    internal static partial ushort RegisterClassEx(WNDCLASSEXW* windowClass);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    internal static partial nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    internal static partial int GetMessageW(MSG* message, nint window, uint min, uint max);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsDialogMessageW(nint dialog, MSG* message);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TranslateMessage(MSG* message);

    [LibraryImport("user32.dll")]
    internal static partial nint DispatchMessageW(MSG* message);

    [LibraryImport("user32.dll")]
    internal static partial void PostQuitMessage(int exitCode);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessageW(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    internal static partial nint SendMessage(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll")]
    internal static partial nint SetFocus(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowText(nint window, string text);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool InvalidateRect(nint window, RECT* rect, [MarshalAs(UnmanagedType.Bool)] bool erase);

    [LibraryImport("user32.dll")]
    internal static partial nint BeginPaint(nint window, PAINTSTRUCT* paint);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EndPaint(nint window, PAINTSTRUCT* paint);

    [LibraryImport("user32.dll")]
    internal static partial nuint SetTimer(nint window, nuint id, uint milliseconds, nint callback);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool KillTimer(nint window, nuint id);

    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForWindow(nint window);

    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForSystem();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AdjustWindowRectExForDpi(RECT* rect, uint style, [MarshalAs(UnmanagedType.Bool)] bool hasMenu, uint exStyle, uint dpi);

    [LibraryImport("user32.dll")]
    internal static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    internal static partial nint GetSystemMenu(nint window, [MarshalAs(UnmanagedType.Bool)] bool revert);

    [LibraryImport("user32.dll")]
    internal static partial int EnableMenuItem(nint menu, uint item, uint enable);

    [LibraryImport("user32.dll", EntryPoint = "LoadIconW")]
    internal static partial nint LoadIcon(nint instance, nint name);

    [LibraryImport("user32.dll", EntryPoint = "LoadCursorW")]
    internal static partial nint LoadCursor(nint instance, nint name);

    [LibraryImport("user32.dll", EntryPoint = "DrawTextW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int DrawText(nint dc, string text, int length, RECT* rect, uint format);

    [LibraryImport("gdi32.dll", EntryPoint = "CreateFontW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateFont(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut, uint charSet, uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);

    [LibraryImport("gdi32.dll")]
    internal static partial nint SelectObject(nint dc, nint item);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteObject(nint item);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteDC(nint dc);

    [LibraryImport("gdi32.dll")]
    internal static partial nint CreateCompatibleDC(nint dc);

    [LibraryImport("gdi32.dll")]
    internal static partial nint CreateCompatibleBitmap(nint dc, int width, int height);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool BitBlt(nint target, int x, int y, int width, int height, nint source, int sourceX, int sourceY, int operation);

    [LibraryImport("gdi32.dll")]
    internal static partial uint SetTextColor(nint dc, uint color);

    [LibraryImport("gdi32.dll")]
    internal static partial int SetBkMode(nint dc, int mode);

    [LibraryImport("gdi32.dll")]
    internal static partial uint SetBkColor(nint dc, uint color);

    [LibraryImport("gdi32.dll")]
    internal static partial nint CreateSolidBrush(uint color);

    [LibraryImport("gdi32.dll", EntryPoint = "GetTextExtentPoint32W", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetTextExtentPoint32(nint dc, string text, int length, SIZE* size);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdiplusStartup(nuint* token, GdiplusStartupInput* input, nint output);

    [LibraryImport("gdiplus.dll")]
    internal static partial void GdiplusShutdown(nuint token);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipCreateFromHDC(nint dc, nint* graphics);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipDeleteGraphics(nint graphics);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipSetSmoothingMode(nint graphics, int mode);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipCreateSolidFill(uint argb, nint* brush);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipDeleteBrush(nint brush);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipCreatePen1(uint argb, float width, int unit, nint* pen);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipDeletePen(nint pen);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipFillEllipse(nint graphics, nint brush, float x, float y, float width, float height);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipDrawEllipse(nint graphics, nint pen, float x, float y, float width, float height);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipDrawArc(nint graphics, nint pen, float x, float y, float width, float height, float start, float sweep);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipDrawLines(nint graphics, nint pen, float* points, int count);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipCreatePath(int fillMode, nint* path);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipDeletePath(nint path);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipAddPathArc(nint path, float x, float y, float width, float height, float start, float sweep);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipClosePathFigure(nint path);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipFillPath(nint graphics, nint brush, nint path);

    [LibraryImport("gdiplus.dll")]
    internal static partial int GdipDrawPath(nint graphics, nint pen, nint path);
}
