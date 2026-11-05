namespace Flyleaf;

/// <summary>Owns the tray icon and the main window. The window is only created when first opened.</summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly Icon _icon;
    private readonly RegisteredWaitHandle _showRegistration;
    private readonly SynchronizationContext _uiContext;
    private MainForm? _mainForm;

    /// <summary>Creates the tray icon and starts listening for requests from a second launch.</summary>
    /// <param name="showEvent">Signalled when another launch wants the window opened.</param>
    public TrayApplicationContext(EventWaitHandle showEvent)
    {
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _icon = LoadAppIcon();

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowMainForm());
        menu.Items.Add("Exit", null, async (_, _) => await ExitAppAsync());
        menu.Items[0].Font = new Font(menu.Items[0].Font, FontStyle.Bold);

        _trayIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "Flyleaf",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                ShowMainForm();
            }
        };

        _showRegistration = ThreadPool.RegisterWaitForSingleObject(
            showEvent,
            (_, _) => _uiContext.Post(_ => ShowMainForm(), null),
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    /// <summary>Opens the main window on the note for a file or folder.</summary>
    /// <param name="path">The full path of the file or folder.</param>
    /// <returns>A task that completes when the note is open.</returns>
    public async Task OpenNoteAsync(string path)
    {
        ShowMainForm();
        await _mainForm!.OpenPathAsync(path);
    }

    /// <summary>Opens the main window, creating it the first time.</summary>
    public void ShowMainForm()
    {
        _mainForm ??= new MainForm(_icon);

        _mainForm.Show();
        if (_mainForm.WindowState == FormWindowState.Minimized)
        {
            _mainForm.WindowState = FormWindowState.Normal;
        }

        _mainForm.Activate();
    }

    private async Task ExitAppAsync()
    {
        if (_mainForm is not null && !await _mainForm.PrepareToExitAsync())
        {
            return;
        }

        _trayIcon.Visible = false;
        _mainForm?.Dispose();
        ExitThread();
    }

    private static Icon LoadAppIcon() =>
        Environment.ProcessPath is { } path && Icon.ExtractAssociatedIcon(path) is { } icon ? icon : SystemIcons.Application;

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _showRegistration.Unregister(null);
            _trayIcon.Dispose();
            _icon.Dispose();
        }

        base.Dispose(disposing);
    }
}
