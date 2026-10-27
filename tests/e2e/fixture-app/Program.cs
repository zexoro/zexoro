// Proves that an installed copy runs: writes %LOCALAPPDATA%\ZexoroE2E\ran.txt ($XDG_DATA_HOME/ZexoroE2E on Linux; user data, which
// uninstall keeps) and %LOCALAPPDATA%\ZexoroE2E\cache\cache.txt (listed in [windows.uninstall] remove).
// With --wait it then stays running, so the tests can check that installers refuse to replace it.
var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZexoroE2E");
Directory.CreateDirectory(Path.Combine(data, "cache"));
File.WriteAllText(Path.Combine(data, "cache", "cache.txt"), "cache\n");
File.WriteAllText(Path.Combine(data, "ran.txt"), AppContext.BaseDirectory + "\n");

if (args is ["--wait"])
{
    Thread.Sleep(TimeSpan.FromMinutes(10));
}
