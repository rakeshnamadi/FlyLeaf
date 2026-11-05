using System.Globalization;
using System.Runtime.InteropServices;
using Flyleaf.Core;
using Microsoft.Win32;

namespace Flyleaf;

/// <summary>
/// The note card shown over Explorer: rounded, shadowed and matched to the Windows light or dark theme.
/// It never takes focus and lets clicks pass through.
/// </summary>
internal sealed class NoteTooltip : Form
{
    private const int WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000;
    private const int CS_DROPSHADOW = 0x20000;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34;
    private const int DWMWCP_ROUND = 2;
    private const int FirstWindows11Build = 22000;

    // The longest tip Explorer displays, so that a very long note can't fill the screen.
    private const int MaxBodyLength = 1024;
    private const double FadeStep = 0.2;
    private const int HorizontalPadding = 9;

    private const TextFormatFlags TextFlags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPadding;

    private static readonly bool HasNativeBorder = Environment.OSVersion.Version.Build >= FirstWindows11Build;

    private readonly System.Windows.Forms.Timer _fadeTimer = new() { Interval = 15 };
    private readonly List<Row> _rows = [];
    private readonly List<int> _dividers = [];
    private Theme _theme = Theme.Light;
    private int _accentWidth;

    /// <summary>Creates the window, which stays hidden until a note is shown.</summary>
    public NoteTooltip()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        MinimumSize = Size.Empty;
        Opacity = 0;

        _fadeTimer.Tick += (_, _) =>
        {
            Opacity = Math.Min(1, Opacity + FadeStep);
            if (Opacity >= 1)
            {
                _fadeTimer.Stop();
            }
        };
    }

    /// <inheritdoc />
    protected override bool ShowWithoutActivation => true;

    /// <inheritdoc />
    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            parameters.ClassStyle |= CS_DROPSHADOW;
            return parameters;
        }
    }

    /// <inheritdoc />
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // Asks for Windows 11's rounded corners. Older versions ignore this and keep square ones.
        var corner = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
    }

    /// <summary>Shows one or more notes beside the cursor, kept fully on the screen.</summary>
    /// <param name="entries">The notes. There is more than one when files share a name and Explorer hides their extensions.</param>
    /// <param name="cursor">The cursor position.</param>
    public void ShowNotes(IReadOnlyList<NoteEntry> entries, Point cursor)
    {
        _theme = Theme.Current();
        BackColor = _theme.Background;
        if (HasNativeBorder)
        {
            var border = ColorTranslator.ToWin32(_theme.Border);
            _ = DwmSetWindowAttribute(Handle, DWMWA_BORDER_COLOR, ref border, sizeof(int));
        }

        var size = BuildLayout(entries);
        var area = Screen.FromPoint(cursor).WorkingArea;
        var gap = Scale(10);
        var x = Math.Clamp(cursor.X - Scale(10), area.Left, Math.Max(area.Left, area.Right - size.Width));

        // Explorer's own tooltip opens below the cursor, so this card goes above it when there is room.
        var y = cursor.Y - size.Height - gap;
        if (y < area.Top)
        {
            y = cursor.Y + (2 * gap);
        }

        Opacity = 0;
        Bounds = new Rectangle(x, Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - size.Height)), size.Width, size.Height);
        Show();
        Invalidate();
        _fadeTimer.Start();
    }

    /// <summary>Hides the card immediately.</summary>
    public void HideNote()
    {
        _fadeTimer.Stop();
        Opacity = 0;
        Hide();
    }

    private Size BuildLayout(IReadOnlyList<NoteEntry> entries)
    {
        foreach (var row in _rows)
        {
            row.Font.Dispose();
        }

        _rows.Clear();
        _dividers.Clear();
        _accentWidth = Scale(3);

        var left = _accentWidth + Scale(HorizontalPadding);
        var maxTextWidth = Scale(340);
        var bulletIndent = Scale(11);
        var y = Scale(6);
        var widest = 0;

        void Add(string text, Font font, Color color, int x, int width)
        {
            var size = TextRenderer.MeasureText(text, font, new Size(width, 0), TextFlags);
            _rows.Add(new Row(text, font, color, new Rectangle(x, y, width, size.Height)));
            widest = Math.Max(widest, x - left + size.Width);
            y += size.Height;
        }

        // Dates sit on the title row, right-aligned once the card's final width is known.
        var dates = new List<(int RowIndex, int Width)>();

        for (var i = 0; i < entries.Count; i++)
        {
            if (i > 0)
            {
                y += Scale(5);
                _dividers.Add(y);
                y += Scale(5);
            }

            var entry = entries[i];
            var dateFont = NewFont("Segoe UI", 8.5f);
            var date = entry.LastModified.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture);
            var dateWidth = TextRenderer.MeasureText(date, dateFont, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width;
            var dateGap = Scale(12);

            _rows.Add(new Row(date, dateFont, _theme.Muted, new Rectangle(0, y + Scale(1), dateWidth, Scale(18))));
            dates.Add((_rows.Count - 1, dateWidth));

            var title = Path.GetFileName(entry.Path.TrimEnd('\\')) is { Length: > 0 } name ? name : entry.Path;
            Add(title, NewFont("Segoe UI Semibold", 9.5f), _theme.Text, left, maxTextWidth - dateWidth - dateGap);
            widest = Math.Max(widest, Math.Min(widest + dateWidth + dateGap, maxTextWidth));
            y += Scale(2);

            var body = entry.Description.Length > MaxBodyLength
                ? string.Concat(entry.Description.AsSpan(0, MaxBodyLength - 1), "…")
                : entry.Description;

            foreach (var line in body.ReplaceLineEndings("\n").Split('\n'))
            {
                if (line.Trim().Length == 0)
                {
                    y += Scale(3);
                }
                else if (NoteListText.IsBullet(line))
                {
                    _rows.Add(new Row("•", NewFont("Segoe UI", 9.5f), _theme.Accent, new Rectangle(left, y, bulletIndent, Scale(18))));
                    Add(line[NoteListText.Bullet.Length..], NewFont("Segoe UI", 9.5f), _theme.Text, left + bulletIndent, maxTextWidth - bulletIndent);
                }
                else
                {
                    Add(line, NewFont("Segoe UI", 9.5f), _theme.Text, left, maxTextWidth);
                }
            }
        }

        var width = left + widest + Scale(HorizontalPadding);
        foreach (var (rowIndex, dateWidth) in dates)
        {
            var row = _rows[rowIndex];
            _rows[rowIndex] = row with { Bounds = row.Bounds with { X = width - Scale(HorizontalPadding) - dateWidth } };
        }

        return new Size(width, y + Scale(6));
    }

    /// <inheritdoc />
    protected override void OnPaint(PaintEventArgs e)
    {
        using (var accent = new SolidBrush(_theme.Accent))
        {
            e.Graphics.FillRectangle(accent, 0, 0, _accentWidth, Height);
        }

        foreach (var row in _rows)
        {
            TextRenderer.DrawText(e.Graphics, row.Text, row.Font, row.Bounds, row.Color, _theme.Background, TextFlags);
        }

        using var line = new Pen(_theme.Border);
        foreach (var y in _dividers)
        {
            e.Graphics.DrawLine(line, _accentWidth + Scale(HorizontalPadding), y, Width - Scale(HorizontalPadding), y);
        }

        if (!HasNativeBorder)
        {
            e.Graphics.DrawRectangle(line, 0, 0, Width - 1, Height - 1);
        }
    }

    private int Scale(int value) => LogicalToDeviceUnits(value);

    // Sized in pixels from the window's own DPI, so that text is crisp and correctly sized on every monitor.
    private Font NewFont(string family, float points) =>
        new(family, points * DeviceDpi / 72f, GraphicsUnit.Pixel);

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fadeTimer.Dispose();
            foreach (var row in _rows)
            {
                row.Font.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);

    private sealed record Row(string Text, Font Font, Color Color, Rectangle Bounds);

    private sealed record Theme(Color Background, Color Text, Color Muted, Color Accent, Color Border)
    {
        public static readonly Theme Light = new(
            Color.FromArgb(252, 252, 252), Color.FromArgb(27, 27, 27), Color.FromArgb(96, 96, 96), Color.FromArgb(15, 118, 110), Color.FromArgb(222, 222, 222));

        public static readonly Theme Dark = new(
            Color.FromArgb(44, 44, 44), Color.FromArgb(243, 243, 243), Color.FromArgb(170, 170, 170), Color.FromArgb(94, 234, 212), Color.FromArgb(72, 72, 72));

        private static readonly Theme HighContrast = new(
            SystemColors.Info, SystemColors.InfoText, SystemColors.InfoText, SystemColors.Highlight, SystemColors.WindowFrame);

        public static Theme Current()
        {
            if (SystemInformation.HighContrast)
            {
                return HighContrast;
            }

            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is 0 ? Dark : Light;
        }
    }
}
