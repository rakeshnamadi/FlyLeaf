using System.Runtime.InteropServices;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;

namespace Flyleaf;

/// <summary>The entry point of the Store version.</summary>
internal static class Program
{
    private const string InstanceMutexName = @"Local\Flyleaf.Tray";
    private const string ShowEventName = @"Local\Flyleaf.Tray.Show";

    /// <summary>Starts the tray app, or passes the request to the copy that is already running.</summary>
    /// <param name="args">Optionally <c>--note &lt;path&gt;</c> from the Explorer menu, or <c>--remove-menu</c>.</param>
    [STAThread]
    private static void Main(string[] args)
    {
        if (args is ["--remove-menu"])
        {
            ShellMenu.Unregister();
            return;
        }

        var notePath = args is ["--note", var path] && Path.IsPathFullyQualified(path) ? path : null;

        using var mutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirstInstance);
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

        if (!isFirstInstance)
        {
            if (notePath is null || !NoteChannel.TrySend(notePath))
            {
                showEvent.Set();
            }

            return;
        }

        ApplicationConfiguration.Initialize();
        ShellMenu.RegisterIfNotPackaged();

        using var context = new TrayApplicationContext(showEvent);
        using var hoverWatcher = new HoverWatcher();
        using var channel = new NoteChannel(context.OpenNoteAsync);

        if (notePath is not null)
        {
            _ = context.OpenNoteAsync(notePath);
        }
        else if (!WasStartedAtSignIn())
        {
            // Starting at sign-in stays in the tray, but someone who clicks the Start menu entry expects to see something.
            context.ShowMainForm();
        }

        Application.Run(context);
    }

    private static bool WasStartedAtSignIn()
    {
        try
        {
            return AppInstance.GetActivatedEventArgs()?.Kind == ActivationKind.StartupTask;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            // Not running from an installed package, as during development.
            return false;
        }
    }
}
