using System.Security.Principal;
using Flyleaf.Core;

namespace FlyWatcher;

/// <summary>
/// Settings for the service. A service does not run as the signed-in user, so the installer passes
/// that user's notes file and SID on the command line. Without arguments the current user is assumed.
/// </summary>
/// <param name="NotesFilePath">The full path of the notes file to serve.</param>
/// <param name="AllowedUser">The only user, other than the service itself, allowed to open the pipe.</param>
/// <param name="PipeName">The name of the pipe to serve.</param>
public sealed record WatcherOptions(string NotesFilePath, SecurityIdentifier AllowedUser, string PipeName = PipeProtocol.PipeName)
{
    /// <summary>Reads <c>--notes &lt;path&gt;</c> and <c>--user-sid &lt;sid&gt;</c> from the command line.</summary>
    /// <param name="args">The command-line arguments.</param>
    /// <returns>The parsed options.</returns>
    public static WatcherOptions Parse(string[] args)
    {
        var notes = NotesRepository.DefaultFilePath;
        var user = WindowsIdentity.GetCurrent().User!;

        for (var i = 0; i < args.Length - 1; i += 2)
        {
            switch (args[i])
            {
                case "--notes":
                    notes = Path.GetFullPath(args[i + 1]);
                    break;
                case "--user-sid":
                    user = new SecurityIdentifier(args[i + 1]);
                    break;
            }
        }

        return new WatcherOptions(notes, user);
    }
}
