using TerminalV.Data;
using TerminalV.Extensions;

namespace TerminalV.Cli;

internal static class AddExtensionCommand
{
    public static int Run(IReadOnlyList<string> args)
    {
        CliConsole.Attach();

        string? source = null;
        var enable = true;
        var replace = false;
        string? sha256 = null;
        foreach (var arg in args)
        {
            if (arg is "-h" or "-?" || arg.Equals("--help", StringComparison.OrdinalIgnoreCase))
            {
                WriteHelp();
                return 0;
            }

            if (arg.Equals("--replace", StringComparison.OrdinalIgnoreCase))
            {
                replace = true;
                continue;
            }

            if (arg.Equals("--no-enable", StringComparison.OrdinalIgnoreCase))
            {
                enable = false;
                continue;
            }

            if (arg.StartsWith("--sha256=", StringComparison.OrdinalIgnoreCase))
            {
                sha256 = arg["--sha256=".Length..];
                continue;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                CliConsole.WriteError($"Unknown option: {arg}");
                WriteUsage();
                return 1;
            }

            if (source is not null)
            {
                CliConsole.WriteError("Only one package link or path is accepted.");
                WriteUsage();
                return 1;
            }

            source = arg;
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            WriteUsage();
            return 1;
        }

        try
        {
            var extensionsRoot = Path.Combine(AppPaths.Root, "extensions");
            var dataRoot = Path.Combine(AppPaths.Root, "extension-data");
            var result = ExtensionInstaller.Install(
                source,
                extensionsRoot,
                enable: enable,
                replace: replace,
                expectedSha256: sha256,
                dataRoot: dataRoot,
                log: CliConsole.WriteLine);

            CliConsole.WriteLine(
                $"{(result.Replaced ? "Updated" : "Installed")} {result.Id} ({result.Name} {result.Version}).");
            CliConsole.WriteLine($"Path: {result.TargetDirectory}");
            if (result.ChecksumVerified)
            {
                CliConsole.WriteLine("SHA256 verified.");
            }

            if (result.Enabled)
            {
                CliConsole.WriteLine("Enabled. Restart TerminalV (or reload extensions) to activate.");
            }
            else
            {
                CliConsole.WriteLine("Not enabled. Turn it on in Settings → Extensions, then restart TerminalV.");
            }

            return 0;
        }
        catch (Exception ex)
        {
            CliConsole.WriteError(ex.Message);
            return 1;
        }
    }

    private static void WriteUsage()
        => CliConsole.WriteError("Usage: TerminalV add-extension <https-link | path.zip> [--replace] [--no-enable] [--sha256=<hex>]");

    private static void WriteHelp()
    {
        CliConsole.WriteLine(CliConsole.HelpText(AppVersion.Informational));
        CliConsole.WriteLine(
            """
            add-extension installs a Desktop API v1 package (ZIP with extension.json).

              TerminalV add-extension <link|zip>     Download/install and enable
              TerminalV add-extension <link> --replace  Overwrite an installed id
              TerminalV add-extension <zip> --no-enable  Install without enabling
              TerminalV add-extension <link> --sha256=<hex>  Pin the package hash

            A matching <link>.sha256 sidecar is used automatically when present.
            Restart TerminalV after install to load the package.
            """);
    }
}
