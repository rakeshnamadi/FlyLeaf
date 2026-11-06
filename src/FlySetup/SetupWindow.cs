using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static FlySetup.NativeMethods;

namespace FlySetup;

/// <summary>The dialogs Setup and the uninstaller can show.</summary>
internal enum Screen
{
    Welcome,
    Installing,
    Installed,
    Blocked,
    Failed,
    Confirm,
    Removing,
    Removed,
}

/// <summary>
/// The Setup window, which paints the screens from the Flyleaf design. Buttons and the check box are real Windows
/// controls, so Tab, Enter, Esc and screen readers work on them. The work runs on its own thread.
/// </summary>
internal static unsafe class SetupWindow
{
    // Sizes are in pixels at 100% scale, taken from the design (600 x 400 with a 36-pixel title bar).
    private const int ClientWidth = 600, ClientHeight = 364, PanelWidth = 216, PanelPadding = 20, Padding = 24;
    private const int ButtonWidth = 96, WideButtonWidth = 124, ButtonHeight = 44, NotesBoxTop = 104;

    private const int IdPrimary = 1, IdSecondary = 2, IdNotes = 3;
    private const uint WmProgress = WM_APP + 1, WmFinished = WM_APP + 2;
    private const nuint SpinnerTimer = 1;
    private const int DWMWA_CAPTION_COLOR = 35;

    private const uint Style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU;

    // Colors are written as 0xRRGGBB.
    private const uint Deep = 0x0B3B3C, Teal = 0x0F766E, TealPressed = 0x0B5A54, Mint = 0x5EEAD4, Tint = 0xE6F6F3, Ink = 0x1B2424, Body = 0x3F4B49, Muted = 0x55615F;
    private const uint Line = 0xDDE5E3, Pending = 0x8A9896, ButtonLine = 0xC2CDCB, White = 0xFFFFFF, Warning = 0xFBBF77, PressedLight = 0xEEF2F1, SoftWhite = 0x2F5A5A;

    private static readonly string[] InstallSteps = ["Copying files", "Adding the Start menu shortcut", "Starting Flyleaf"];
    private static readonly string[] RemoveSteps = ["Closing Flyleaf", "Removing the menu item and shortcut", "Deleting program files"];

    private static nint _window, _primary, _secondary, _notes, _whiteBrush;
    private static nint _fontHeading, _fontBody, _fontBodyBold, _fontSmall, _fontSmallBold, _fontTiny, _fontTinyBold, _fontBrand, _fontTagline;
    private static uint _dpi = 96;
    private static Screen _screen;
    private static bool _isPreview, _hasNotes, _deletedNotes;
    private static volatile int _step, _percent;
    private static string _failureTitle = "", _failureMessage = "";
    private static float _spinnerAngle;
    private static int _exitCode;

    /// <summary>Shows the window until the user closes it.</summary>
    /// <param name="first">The first screen.</param>
    /// <param name="isPreview">Whether to show just this screen with sample state, changing nothing.</param>
    /// <returns>The process exit code.</returns>
    public static int Run(Screen first, bool isPreview)
    {
        _isPreview = isPreview;
        _hasNotes = isPreview || UserInstaller.HasNotes;
        _failureTitle = "Flyleaf couldn't be installed";
        _failureMessage = "The app couldn't be copied to your account's programs folder.";

        var input = new GdiplusStartupInput { Version = 1 };
        nuint token;
        if (GdiplusStartup(&token, &input, 0) != 0)
        {
            return 1;
        }

        InitCommonControls();
        CreateMainWindow(first is Screen.Confirm or Screen.Removing or Screen.Removed ? "Uninstall Flyleaf" : "Flyleaf Setup");
        if (isPreview)
        {
            _step = 1;
            _percent = first is Screen.Installing ? 62 : 48;
        }

        GoTo(first);
        ShowWindow(_window, SW_SHOW);
        SetForegroundWindow(_window);
        SetFocus(_primary);

        MSG message;
        while (GetMessageW(&message, 0, 0, 0) > 0)
        {
            if (!IsDialogMessageW(_window, &message))
            {
                TranslateMessage(&message);
                DispatchMessageW(&message);
            }
        }

        GdiplusShutdown(token);
        return _exitCode;
    }

    private static void CreateMainWindow(string title)
    {
        var instance = GetModuleHandle(null);
        const nint IDC_ARROW = 32512, ApplicationIcon = 32512;
        var icon = LoadIcon(instance, ApplicationIcon);

        fixed (char* className = "FlyleafSetupWindow")
        {
            var windowClass = new WNDCLASSEXW
            {
                Size = (uint)sizeof(WNDCLASSEXW),
                WndProc = &WindowProc,
                Instance = instance,
                Icon = icon,
                SmallIcon = icon,
                Cursor = LoadCursor(0, IDC_ARROW),
                ClassName = className,
            };
            RegisterClassEx(&windowClass);
        }

        _dpi = GetDpiForSystem();
        var size = WindowSize(_dpi);

        const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;
        var x = (GetSystemMetrics(SM_CXSCREEN) - size.Width) / 2;
        var y = (GetSystemMetrics(SM_CYSCREEN) - size.Height) / 2;

        _whiteBrush = CreateSolidBrush(Bgr(White));
        _window = CreateWindowEx(0, "FlyleafSetupWindow", title, Style, x, y, size.Width, size.Height, 0, 0, instance, 0);

        // Windows 11 only: matches the title bar to the design. Older versions ignore it.
        var caption = (int)Bgr(0xF3F6F5);
        _ = DwmSetWindowAttribute(_window, DWMWA_CAPTION_COLOR, &caption, sizeof(int));

        _dpi = GetDpiForWindow(_window);
        const uint Child = WS_CHILD | WS_TABSTOP;
        _secondary = CreateWindowEx(0, "BUTTON", "Cancel", Child | BS_OWNERDRAW, 0, 0, 0, 0, _window, IdSecondary, instance, 0);
        _primary = CreateWindowEx(0, "BUTTON", "Install", Child | BS_OWNERDRAW, 0, 0, 0, 0, _window, IdPrimary, instance, 0);
        _notes = CreateWindowEx(0, "BUTTON", "Also delete my notes", Child | BS_AUTOCHECKBOX, 0, 0, 0, 0, _window, IdNotes, instance, 0);

        ApplyDpi();
    }

    private static SIZE WindowSize(uint dpi)
    {
        var rect = new RECT { Right = MulDiv(ClientWidth, dpi), Bottom = MulDiv(ClientHeight, dpi) };
        AdjustWindowRectExForDpi(&rect, Style, false, 0, dpi);
        return new SIZE { Width = rect.Right - rect.Left, Height = rect.Bottom - rect.Top };
    }

    private static int MulDiv(int value, uint dpi) => (int)(value * dpi / 96);

    private static int S(int value) => MulDiv(value, _dpi);

    // GDI wants colors as 0xBBGGRR.
    private static uint Bgr(uint rgb) => ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);

    private static uint Argb(uint rgb) => 0xFF000000 | rgb;

    private static void ApplyDpi()
    {
        foreach (var font in new[] { _fontHeading, _fontBody, _fontBodyBold, _fontSmall, _fontSmallBold, _fontTiny, _fontTinyBold, _fontBrand, _fontTagline })
        {
            if (font != 0)
            {
                DeleteObject(font);
            }
        }

        _fontHeading = Font(22, FW_SEMIBOLD);
        _fontBody = Font(14, FW_NORMAL);
        _fontBodyBold = Font(14, FW_SEMIBOLD);
        _fontSmall = Font(13, FW_NORMAL);
        _fontSmallBold = Font(13, FW_SEMIBOLD);
        _fontTiny = Font(11, FW_NORMAL);
        _fontTinyBold = Font(11, FW_SEMIBOLD);
        _fontBrand = Font(24, FW_SEMIBOLD);
        _fontTagline = Font(13, FW_NORMAL);

        SendMessage(_notes, WM_SETFONT, _fontBody, 1);
        SendMessage(_primary, WM_SETFONT, _fontBodyBold, 1);
        SendMessage(_secondary, WM_SETFONT, _fontBody, 1);
        PositionControls();
    }

    private static nint Font(int pixels, int weight) =>
        CreateFont(-S(pixels), 0, 0, 0, weight, 0, 0, 0, 1, 0, 0, CLEARTYPE_QUALITY, 0, "Segoe UI");

    private static void PositionControls()
    {
        var secondaryWidth = _screen == Screen.Confirm ? WideButtonWidth : ButtonWidth;
        var buttonTop = S(ClientHeight - Padding - ButtonHeight);
        var primaryLeft = S(ClientWidth - Padding - ButtonWidth);

        SetWindowPos(_primary, 0, primaryLeft, buttonTop, S(ButtonWidth), S(ButtonHeight), SWP_NOZORDER | SWP_NOACTIVATE);
        SetWindowPos(_secondary, 0, primaryLeft - S(8 + secondaryWidth), buttonTop, S(secondaryWidth), S(ButtonHeight), SWP_NOZORDER | SWP_NOACTIVATE);
        SetWindowPos(_notes, 0, S(PanelWidth + Padding + 12), S(NotesBoxTop + 9), S(ClientWidth - PanelWidth - (2 * Padding) - 24), S(22), SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private static void GoTo(Screen screen)
    {
        _screen = screen;
        var isBusy = screen is Screen.Installing or Screen.Removing;

        var (primary, secondary) = screen switch
        {
            Screen.Welcome => ("Install", "Cancel"),
            Screen.Installed => ("Done", null),
            Screen.Blocked or Screen.Failed or Screen.Removed => ("Close", null),
            Screen.Confirm => ("Remove", "Keep Flyleaf"),
            _ => ((string?)null, (string?)null),
        };

        SetButton(_primary, primary);
        SetButton(_secondary, secondary);
        ShowWindow(_notes, screen == Screen.Confirm && _hasNotes ? SW_SHOW : SW_HIDE);
        PositionControls();

        // The window can't be closed halfway through, because that would leave a partial install.
        EnableMenuItem(GetSystemMenu(_window, false), SC_CLOSE, MF_BYCOMMAND | (isBusy ? MF_GRAYED : 0));

        if (isBusy)
        {
            SetTimer(_window, SpinnerTimer, 50, 0);
        }
        else
        {
            KillTimer(_window, SpinnerTimer);
            SetFocus(_primary);
        }

        InvalidateRect(_window, null, false);
    }

    private static void SetButton(nint button, string? text)
    {
        ShowWindow(button, text is null ? SW_HIDE : SW_SHOW);
        if (text is not null)
        {
            SetWindowText(button, text);
            InvalidateRect(button, null, false);
        }
    }

    private static void StartWork(Screen working, Action<Action<int, int>> work, Screen succeeded, string failureTitle)
    {
        _step = 0;
        _percent = 0;
        _failureTitle = failureTitle;
        GoTo(working);

        void Report(int step, int percent)
        {
            _step = step;
            _percent = percent;
            PostMessageW(_window, WmProgress, 0, 0);
        }

        var thread = new Thread(() =>
        {
            var result = succeeded;
            try
            {
                work(Report);
            }
            catch (BlockedByWindowsException)
            {
                result = Screen.Blocked;
            }
            catch (Exception ex) when (UserInstaller.IsExpectedFailure(ex))
            {
                _failureMessage = ex.Message.Length > 220 ? string.Concat(ex.Message.AsSpan(0, 219), "…") : ex.Message;
                result = Screen.Failed;
            }

            PostMessageW(_window, WmFinished, (nint)result, 0);
        });
        thread.IsBackground = false;
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private static void OnPrimary()
    {
        switch (_screen)
        {
            case Screen.Welcome when !_isPreview:
                StartWork(Screen.Installing, UserInstaller.Install, Screen.Installed, "Flyleaf couldn't be installed");
                break;
            case Screen.Confirm when !_isPreview:
                _deletedNotes = SendMessage(_notes, BM_GETCHECK, 0, 0) == 1;
                var deleteNotes = _deletedNotes;
                StartWork(Screen.Removing, report => UserInstaller.Uninstall(deleteNotes, report), Screen.Removed, "Flyleaf couldn't be removed");
                break;
            default:
                Close();
                break;
        }
    }

    private static void Close()
    {
        if (_screen is Screen.Installing or Screen.Removing && !_isPreview)
        {
            return;
        }

        DestroyWindow(_window);
    }

    [UnmanagedCallersOnly]
    private static nint WindowProc(nint window, uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case WM_PAINT:
                Paint(window);
                return 0;
            case WM_ERASEBKGND:
                return 1;
            case WM_DRAWITEM:
                DrawButton((DRAWITEMSTRUCT*)lParam);
                return 1;
            case WM_CTLCOLORBTN:
            case WM_CTLCOLORSTATIC:
                SetBkColor(wParam, Bgr(White));
                SetTextColor(wParam, Bgr(Ink));
                return _whiteBrush;
            case WM_COMMAND:
                OnCommand((int)(wParam & 0xFFFF));
                return 0;
            case WM_TIMER:
                _spinnerAngle = (_spinnerAngle + 24) % 360;
                InvalidateRect(window, null, false);
                return 0;
            case WmProgress:
                InvalidateRect(window, null, false);
                return 0;
            case WmFinished:
                if ((Screen)wParam is Screen.Blocked or Screen.Failed)
                {
                    _exitCode = 1;
                }

                GoTo((Screen)wParam);
                return 0;
            case WM_DPICHANGED:
                _dpi = (uint)(wParam & 0xFFFF);
                var suggested = (RECT*)lParam;
                SetWindowPos(window, 0, suggested->Left, suggested->Top, suggested->Right - suggested->Left, suggested->Bottom - suggested->Top, SWP_NOZORDER | SWP_NOACTIVATE);
                ApplyDpi();
                InvalidateRect(window, null, false);
                return 0;
            case WM_CLOSE:
                Close();
                return 0;
            case WM_DESTROY:
                PostQuitMessage(_exitCode);
                return 0;
            default:
                return DefWindowProc(window, message, wParam, lParam);
        }
    }

    private static void OnCommand(int id)
    {
        const int IDOK = 1, IDCANCEL = 2;

        // Enter and Esc arrive here too, as the same ids.
        if (id == IDOK && _screen is not (Screen.Installing or Screen.Removing))
        {
            OnPrimary();
        }
        else if (id == IDCANCEL)
        {
            Close();
        }
    }

    private static void Paint(nint window)
    {
        PAINTSTRUCT paint;
        var target = BeginPaint(window, &paint);
        var width = S(ClientWidth);
        var height = S(ClientHeight);

        // Drawn off screen first, so that the spinner never flickers.
        var dc = CreateCompatibleDC(target);
        var bitmap = CreateCompatibleBitmap(target, width, height);
        var previous = SelectObject(dc, bitmap);
        SetBkMode(dc, TRANSPARENT);

        nint graphics;
        GdipCreateFromHDC(dc, &graphics);
        const int SmoothingModeAntiAlias = 4;
        GdipSetSmoothingMode(graphics, SmoothingModeAntiAlias);

        var canvas = new Canvas(dc, graphics);
        canvas.Rect(0, 0, width, height, White);
        canvas.Rect(0, 0, S(PanelWidth), height, Deep);
        DrawBrand(canvas);
        DrawContent(canvas);

        GdipDeleteGraphics(graphics);
        BitBlt(target, 0, 0, width, height, dc, 0, 0, SRCCOPY);
        SelectObject(dc, previous);
        DeleteObject(bitmap);
        DeleteDC(dc);
        EndPaint(window, &paint);
    }

    private static void DrawBrand(Canvas canvas)
    {
        canvas.Text("Flyleaf", S(PanelPadding), S(PanelPadding), S(PanelWidth - (2 * PanelPadding)), _fontBrand, White);
        canvas.Text("A note on every file.", S(PanelPadding), S(PanelPadding + 34), S(PanelWidth - (2 * PanelPadding)), _fontTagline, Mint);

        switch (_screen)
        {
            case Screen.Welcome:
                DrawNoteCardIllustration(canvas);
                break;
            case Screen.Installed:
            case Screen.Removed:
                DrawCheckBadge(canvas);
                break;
            case Screen.Blocked:
            case Screen.Failed:
                DrawWarningBadge(canvas);
                break;
        }
    }

    private static void DrawNoteCardIllustration(Canvas canvas)
    {
        int x = S(PanelPadding), width = S(PanelWidth - (2 * PanelPadding));
        int rowTop = S(ClientHeight - PanelPadding - 26), rowHeight = S(26);
        int cardHeight = S(56), cardTop = rowTop - S(6) - cardHeight;

        canvas.RoundedRect(x, rowTop, width, rowHeight, S(4), SoftWhite);
        canvas.Line(White, S(1), [x + S(9), rowTop + S(7), x + S(14), rowTop + S(7), x + S(18), rowTop + S(11), x + S(18), rowTop + S(19), x + S(9), rowTop + S(19), x + S(9), rowTop + S(7)]);
        canvas.Text("release plan", x + S(25), rowTop + S(5), width - S(30), _fontTiny, White);

        canvas.RoundedRect(x, cardTop, width, cardHeight, S(8), White);
        canvas.Text("release plan.docx", x + S(10), cardTop + S(8), width - S(50), _fontTinyBold, Ink);
        canvas.TextRight("5 Nov", x, cardTop + S(9), width - S(10), _fontTiny, Muted);
        canvas.Text("Final plan. Ship on the 20th.", x + S(10), cardTop + S(28), width - S(20), _fontTiny, Ink);
    }

    private static void DrawCheckBadge(Canvas canvas)
    {
        int x = S(PanelPadding), y = S(ClientHeight - PanelPadding - 56), size = S(56);
        canvas.Ellipse(x, y, size, size, Mint);
        canvas.Line(Deep, S(4), [x + S(16), y + S(29), x + S(24), y + S(37), x + S(41), y + S(20)]);
    }

    private static void DrawWarningBadge(Canvas canvas)
    {
        int x = S(PanelPadding), y = S(ClientHeight - PanelPadding - 56), size = S(56);
        canvas.Ellipse(x, y, size, size, Warning);
        canvas.RoundedRect(x + S(25), y + S(14), S(5), S(19), S(2), Deep);
        canvas.Ellipse(x + S(25), y + S(37), S(6), S(6), Deep);
    }

    private static void DrawContent(Canvas canvas)
    {
        var x = S(PanelWidth + Padding);
        var y = S(Padding);
        var width = S(ClientWidth - PanelWidth - (2 * Padding));

        switch (_screen)
        {
            case Screen.Welcome:
                y += canvas.Text("Install Flyleaf", x, y, width, _fontHeading, Ink) + S(10);
                y += canvas.Text("Attach a note to any file or folder, then read it just by hovering in File Explorer.", x, y, width, _fontBody, Ink) + S(12);
                foreach (var line in new[] { "No admin rights needed", "Installs for your account only", "Your notes stay on this PC" })
                {
                    canvas.Line(Teal, S(2), [x + S(2), y + S(9), x + S(6), y + S(13), x + S(13), y + S(5)]);
                    canvas.Text(line, x + S(22), y, width - S(22), _fontSmall, Body);
                    y += S(24);
                }

                break;

            case Screen.Installing:
                DrawProgress(canvas, "Installing Flyleaf", InstallSteps, x, y, width);
                break;

            case Screen.Removing:
                DrawProgress(canvas, "Removing Flyleaf", RemoveSteps, x, y, width);
                break;

            case Screen.Installed:
                y += canvas.Text("Flyleaf is ready", x, y, width, _fontHeading, Ink) + S(2);
                y += canvas.Text("It's running in your system tray.", x, y, width, _fontBody, Body) + S(12);
                y += DrawNumbered(canvas, x, y, width, "1", ("Right-click a file, then choose ", false), ("Show more options", true), (".", false)) + S(8);
                y += DrawNumbered(canvas, x, y, width, "2", ("Choose ", false), ("Add Flyleaf note", true), (" and type your note.", false)) + S(8);
                DrawNumbered(canvas, x, y, width, "3", ("Rest your pointer on the file to read it.", false));
                break;

            case Screen.Blocked:
                y += canvas.Text("Windows blocked Flyleaf", x, y, width, _fontHeading, Ink) + S(10);
                y += canvas.Text("Smart App Control on this PC stopped Flyleaf because this copy isn't code-signed.", x, y, width, _fontBody, Ink) + S(10);
                y += DrawNotice(canvas, x, y, width, "Nothing was left behind. Setup removed everything it added.") + S(10);
                canvas.Runs(x, y, width, _fontBody, _fontBodyBold, Ink, ("What to do:", true), (" ask whoever shared this file for a signed copy.", false));
                break;

            case Screen.Failed:
                y += canvas.Text(_failureTitle, x, y, width, _fontHeading, Ink) + S(10);
                canvas.Text(_failureMessage, x, y, width, _fontBody, Ink);
                break;

            case Screen.Confirm:
                y += canvas.Text("Remove Flyleaf?", x, y, width, _fontHeading, Ink) + S(10);
                y += canvas.Text("Your files aren't affected. Flyleaf never changes them.", x, y, width, _fontBody, Ink) + S(12);
                if (_hasNotes)
                {
                    int boxTop = S(NotesBoxTop), boxHeight = S(68);
                    canvas.RoundedOutline(x, boxTop, width, boxHeight, S(6), Line);
                    canvas.Text("Leave this off to keep them. They'll be there if you reinstall.", x + S(36), boxTop + S(34), width - S(46), _fontTiny, Muted);
                }

                break;

            case Screen.Removed:
                y += canvas.Text("Flyleaf has been removed", x, y, width, _fontHeading, Ink) + S(10);
                y += canvas.Text("The app, its shortcut and its menu item are gone.", x, y, width, _fontBody, Ink) + S(12);
                if (_deletedNotes)
                {
                    canvas.Text("Your notes were deleted too.", x, y, width, _fontBody, Ink);
                }
                else if (_hasNotes)
                {
                    int boxHeight = S(42);
                    canvas.RoundedRect(x, y, width, boxHeight, S(6), Tint);
                    canvas.Text("Your notes are still here", x + S(12), y + S(10), width - S(24), _fontBodyBold, Ink);
                    y += boxHeight + S(12);
                    canvas.Text("If you install Flyleaf again, it picks them up automatically.", x, y, width, _fontSmall, Body);
                }

                break;
        }
    }

    private static void DrawProgress(Canvas canvas, string heading, string[] steps, int x, int y, int width)
    {
        y += canvas.Text(heading, x, y, width, _fontHeading, Ink) + S(16);

        canvas.RoundedRect(x, y, width, S(6), S(3), Line);
        var filled = width * Math.Clamp(_percent, 0, 100) / 100;
        if (filled > 0)
        {
            canvas.RoundedRect(x, y, Math.Max(filled, S(6)), S(6), S(3), Teal);
        }

        y += S(12);
        y += canvas.Text("This usually takes a few seconds.", x, y, width, _fontSmall, Body) + S(16);

        for (var i = 0; i < steps.Length; i++)
        {
            int icon = S(18), rowTop = y;
            if (i < _step || _percent >= 100)
            {
                canvas.Ellipse(x, rowTop, icon, icon, Teal);
                canvas.Line(White, S(2), [x + S(5), rowTop + S(9), x + S(8), rowTop + S(12), x + S(13), rowTop + S(7)]);
                canvas.Text(steps[i], x + S(28), rowTop - S(1), width - S(28), _fontBody, Ink);
            }
            else if (i == _step)
            {
                canvas.Ring(x + S(1), rowTop + S(1), S(16), S(2), Line);
                canvas.Arc(x + S(1), rowTop + S(1), S(16), S(2), Teal, _spinnerAngle, 90);
                canvas.Text(steps[i], x + S(28), rowTop - S(1), width - S(28), _fontBodyBold, Ink);
            }
            else
            {
                canvas.Ring(x + S(1), rowTop + S(1), S(16), S(1), Pending);
                canvas.Text(steps[i], x + S(28), rowTop - S(1), width - S(28), _fontBody, Muted);
            }

            y += S(30);
        }
    }

    private static int DrawNumbered(Canvas canvas, int x, int y, int width, string number, params (string Text, bool Bold)[] runs)
    {
        canvas.Ellipse(x, y, S(20), S(20), Tint);
        canvas.TextCentered(number, x, y, S(20), S(20), _fontTinyBold, Deep);
        return Math.Max(S(20), canvas.Runs(x + S(30), y + S(1), width - S(30), _fontSmall, _fontSmallBold, Ink, runs));
    }

    private static int DrawNotice(Canvas canvas, int x, int y, int width, string text)
    {
        var height = canvas.MeasureText(text, width - S(46), _fontSmall) + S(20);
        canvas.RoundedRect(x, y, width, height, S(6), Tint);
        canvas.Line(Teal, S(2), [x + S(13), y + S(19), x + S(17), y + S(23), x + S(24), y + S(15)]);
        canvas.Text(text, x + S(34), y + S(10), width - S(46), _fontSmall, Ink);
        return height;
    }

    private static void DrawButton(DRAWITEMSTRUCT* item)
    {
        var isPrimary = item->ControlId == IdPrimary;
        var isPressed = (item->ItemState & ODS_SELECTED) != 0;

        // Windows sets ODS_NOFOCUSRECT until the keyboard is used, so the ring only appears for keyboard users.
        var hasFocus = (item->ItemState & ODS_FOCUS) != 0 && (item->ItemState & ODS_NOFOCUSRECT) == 0;
        var bounds = item->Bounds;
        int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;

        nint graphics;
        GdipCreateFromHDC(item->Dc, &graphics);
        GdipSetSmoothingMode(graphics, 4);
        var canvas = new Canvas(item->Dc, graphics);

        canvas.Rect(0, 0, width, height, White);
        if (isPrimary)
        {
            canvas.RoundedRect(0, 0, width - 1, height - 1, S(6), isPressed ? TealPressed : Teal);
            if (hasFocus)
            {
                canvas.RoundedOutline(S(3), S(3), width - S(7), height - S(7), S(4), Mint);
            }
        }
        else
        {
            canvas.RoundedRect(0, 0, width - 1, height - 1, S(6), isPressed ? PressedLight : White);
            canvas.RoundedOutline(0, 0, width - 1, height - 1, S(6), hasFocus ? Teal : ButtonLine);
        }

        GdipDeleteGraphics(graphics);

        SetBkMode(item->Dc, TRANSPARENT);
        canvas.TextCentered(isPrimary ? PrimaryLabel() : SecondaryLabel(), 0, 0, width, height, isPrimary ? _fontBodyBold : _fontBody, isPrimary ? White : Ink);
    }

    private static string PrimaryLabel() => _screen switch
    {
        Screen.Welcome => "Install",
        Screen.Installed => "Done",
        Screen.Confirm => "Remove",
        _ => "Close",
    };

    private static string SecondaryLabel() => _screen == Screen.Confirm ? "Keep Flyleaf" : "Cancel";

    /// <summary>Small drawing helpers: GDI+ for smooth shapes, and GDI for sharp ClearType text.</summary>
    private readonly struct Canvas(nint dc, nint graphics)
    {
        public void Rect(int x, int y, int width, int height, uint color)
        {
            var brush = CreateSolidBrush(Bgr(color));
            var rect = new RECT { Left = x, Top = y, Right = x + width, Bottom = y + height };
            FillRect(dc, &rect, brush);
            DeleteObject(brush);
        }

        public void Ellipse(int x, int y, int width, int height, uint color)
        {
            nint brush;
            GdipCreateSolidFill(Argb(color), &brush);
            GdipFillEllipse(graphics, brush, x, y, width, height);
            GdipDeleteBrush(brush);
        }

        public void Ring(int x, int y, int size, int thickness, uint color)
        {
            nint pen;
            GdipCreatePen1(Argb(color), thickness, 2, &pen);
            GdipDrawEllipse(graphics, pen, x, y, size, size);
            GdipDeletePen(pen);
        }

        public void Arc(int x, int y, int size, int thickness, uint color, float start, float sweep)
        {
            nint pen;
            GdipCreatePen1(Argb(color), thickness, 2, &pen);
            GdipDrawArc(graphics, pen, x, y, size, size, start, sweep);
            GdipDeletePen(pen);
        }

        public void Line(uint color, int thickness, int[] points)
        {
            var values = stackalloc float[points.Length];
            for (var i = 0; i < points.Length; i++)
            {
                values[i] = points[i];
            }

            nint pen;
            GdipCreatePen1(Argb(color), thickness, 2, &pen);
            GdipDrawLines(graphics, pen, values, points.Length / 2);
            GdipDeletePen(pen);
        }

        public void RoundedRect(int x, int y, int width, int height, int radius, uint color)
        {
            var path = RoundedPath(x, y, width, height, radius);
            nint brush;
            GdipCreateSolidFill(Argb(color), &brush);
            GdipFillPath(graphics, brush, path);
            GdipDeleteBrush(brush);
            GdipDeletePath(path);
        }

        public void RoundedOutline(int x, int y, int width, int height, int radius, uint color)
        {
            var path = RoundedPath(x, y, width, height, radius);
            nint pen;
            GdipCreatePen1(Argb(color), Math.Max(1, S(1)), 2, &pen);
            GdipDrawPath(graphics, pen, path);
            GdipDeletePen(pen);
            GdipDeletePath(path);
        }

        private static nint RoundedPath(int x, int y, int width, int height, int radius)
        {
            float diameter = radius * 2;
            nint path;
            GdipCreatePath(0, &path);
            GdipAddPathArc(path, x, y, diameter, diameter, 180, 90);
            GdipAddPathArc(path, x + width - diameter, y, diameter, diameter, 270, 90);
            GdipAddPathArc(path, x + width - diameter, y + height - diameter, diameter, diameter, 0, 90);
            GdipAddPathArc(path, x, y + height - diameter, diameter, diameter, 90, 90);
            GdipClosePathFigure(path);
            return path;
        }

        public int MeasureText(string text, int width, nint font)
        {
            var previous = SelectObject(dc, font);
            var rect = new RECT { Right = width };
            DrawText(dc, text, text.Length, &rect, DT_WORDBREAK | DT_NOPREFIX | DT_CALCRECT);
            SelectObject(dc, previous);
            return rect.Bottom;
        }

        public int Text(string text, int x, int y, int width, nint font, uint color)
        {
            var previous = SelectObject(dc, font);
            SetTextColor(dc, Bgr(color));
            var rect = new RECT { Left = x, Top = y, Right = x + width, Bottom = y + S(400) };
            var height = DrawText(dc, text, text.Length, &rect, DT_WORDBREAK | DT_NOPREFIX);
            SelectObject(dc, previous);
            return height;
        }

        public void TextRight(string text, int x, int y, int width, nint font, uint color)
        {
            var previous = SelectObject(dc, font);
            SetTextColor(dc, Bgr(color));
            var rect = new RECT { Left = x, Top = y, Right = x + width, Bottom = y + S(40) };
            DrawText(dc, text, text.Length, &rect, DT_RIGHT | DT_NOPREFIX | DT_SINGLELINE);
            SelectObject(dc, previous);
        }

        public void TextCentered(string text, int x, int y, int width, int height, nint font, uint color)
        {
            var previous = SelectObject(dc, font);
            SetTextColor(dc, Bgr(color));
            var rect = new RECT { Left = x, Top = y, Right = x + width, Bottom = y + height };
            DrawText(dc, text, text.Length, &rect, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
            SelectObject(dc, previous);
        }

        // Word-wraps text that mixes regular and bold runs, which DrawText can't do by itself.
        public int Runs(int x, int y, int width, nint regular, nint bold, uint color, params (string Text, bool Bold)[] runs)
        {
            SetTextColor(dc, Bgr(color));
            var previous = SelectObject(dc, regular);
            SIZE size;
            GetTextExtentPoint32(dc, "Ag", 2, &size);
            var lineHeight = size.Height;
            int left = 0, top = 0;

            foreach (var run in runs)
            {
                SelectObject(dc, run.Bold ? bold : regular);
                var start = 0;
                while (start < run.Text.Length)
                {
                    var next = run.Text.IndexOf(' ', start);
                    var end = next < 0 ? run.Text.Length : next + 1;
                    var word = run.Text[start..end];

                    GetTextExtentPoint32(dc, word.TrimEnd(), word.TrimEnd().Length, &size);
                    if (left > 0 && left + size.Width > width)
                    {
                        left = 0;
                        top += lineHeight;
                    }

                    var rect = new RECT { Left = x + left, Top = y + top, Right = x + width, Bottom = y + top + lineHeight };
                    DrawText(dc, word, word.Length, &rect, DT_SINGLELINE | DT_NOPREFIX);

                    GetTextExtentPoint32(dc, word, word.Length, &size);
                    left += size.Width;
                    start = end;
                }
            }

            SelectObject(dc, previous);
            return top + lineHeight;
        }
    }

    [DllImport("user32.dll")]
    private static extern int FillRect(nint dc, RECT* rect, nint brush);
}
