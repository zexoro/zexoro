namespace Zexoro.Updater;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "apply")
        {
            // A current directory inside the install would keep 'app' from being renamed.
            Directory.SetCurrentDirectory(Path.GetTempPath());
        }

        return UpdaterApp.Run(args, Console.Out, Console.Error, UpdaterEnvironment.System());
    }
}
