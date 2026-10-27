namespace Zexoro.Configuration;

/// <summary>The commented <c>zexoro.toml</c> that <c>zexoro init</c> writes.</summary>
public static class ConfigTemplate
{
    /// <summary>
    /// The template text (LF line endings). It is a valid configuration; only the files it names
    /// do not exist yet.
    /// </summary>
    public const string Text = """
        # zexoro configuration. Paths are relative to this file.
        # Check it with `zexoro doctor`, then build installers with `zexoro pack`.

        [app]
        id        = "com.example.myapp"      # stable reverse-DNS identity, used to recognize upgrades; never change it
        name      = "My App"                 # display name, also the install directory name
        version   = "0.1.0"                  # Semantic Versioning 2.0.0; `zexoro pack --version 1.2.3` overrides it
        publisher = "Example Ltd."
        icon      = "assets/app.ico"
        # description = "What the application does"
        # url         = "https://example.com"

        [input]
        dir  = "build/win-x64"               # your build output
        main = "MyApp.exe"                   # entry point, relative to dir

        [windows]
        # formats     = ["nsis"]             # package formats to build; nsis is the one implemented so far
        install_scope = "user"               # user | machine
        shortcuts     = ["start-menu"]       # add "desktop" if wanted
        # license     = "LICENSE.txt"        # shown on an installer page
        # sign        = "main"               # main | all: which application files to sign

        # [windows.signing]                  # optional; omit to skip signing
        # certificate = { env = "SIGN_CERT_PATH" }       # PFX file; a literal path also works
        # password    = { env = "SIGN_CERT_PASSWORD" }
        # timestamp   = "http://timestamp.digicert.com"

        # [windows.uninstall]
        # remove = ['%LOCALAPPDATA%\My App']  # extra paths to delete; user data is kept otherwise

        # [updates]                          # optional; installers then include zexoro-updater
        # feed     = "https://example.com/updates/{channel}/feed.json"
        # download = "https://example.com/dl/{file}"     # also {version} and {channel}
        # channel  = "stable"                            # `zexoro pack --channel beta` overrides it
        # keys     = [{ file = "zexoro-update.pub" }]    # from `zexoro keys new`; or the base64 text

        """;
}
