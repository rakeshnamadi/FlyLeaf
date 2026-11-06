using System.Runtime.InteropServices;
using FlySetup;

// Setup is usually run from a Downloads folder, so never load a DLL planted beside it.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

NativeMethods.SetDefaultDllDirectories(NativeMethods.LOAD_LIBRARY_SEARCH_SYSTEM32);

// --preview <screen> shows one dialog and changes nothing, for checking the design and taking screenshots.
if (args is ["--preview", var name])
{
    return Enum.TryParse<Screen>(name, ignoreCase: true, out var screen) ? SetupWindow.Run(screen, isPreview: true) : 2;
}

int exitCode;
if (args.Contains("--silent"))
{
    // No prompts, for scripted installs and automated tests.
    exitCode = RunSilently();
}
else
{
    exitCode = SetupWindow.Run(UserInstaller.HasPayload ? Screen.Welcome : Screen.Confirm, isPreview: false);
}

UserInstaller.FinishRemovingItself();
return exitCode;

static int RunSilently()
{
    try
    {
        if (UserInstaller.HasPayload)
        {
            UserInstaller.Install((_, _) => { });
        }
        else
        {
            UserInstaller.Uninstall(deleteNotes: false, (_, _) => { });
        }

        return 0;
    }
    catch (Exception ex) when (UserInstaller.IsExpectedFailure(ex))
    {
        return 1;
    }
}
