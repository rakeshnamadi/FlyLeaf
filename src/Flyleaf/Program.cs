namespace Flyleaf;

/// <summary>The entry point of the tray application.</summary>
internal static class Program
{
    private const string InstanceMutexName = @"Local\Flyleaf.Tray";
    private const string ShowEventName = @"Local\Flyleaf.Tray.Show";

    /// <summary>Starts the tray app, or asks the copy that is already running to open its window.</summary>
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirstInstance);
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

        if (!isFirstInstance)
        {
            showEvent.Set();
            return;
        }

        ApplicationConfiguration.Initialize();
        using var context = new TrayApplicationContext(showEvent);
        Application.Run(context);
    }
}
