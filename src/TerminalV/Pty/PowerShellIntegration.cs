using System.IO;
using System.Text;

namespace TerminalV.Pty;

internal static class PowerShellIntegration
{
    // EncodedCommand is UTF-16LE in both Windows PowerShell 5.1 and PowerShell 7.
    // The startup command runs AFTER normal profiles; no profile file is edited.
    public static string CommandLine(string executable, string? directory = null, string? startupCommand = null)
    {
        var name = Path.GetFileNameWithoutExtension(executable);
        if (!name.Equals("powershell", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("pwsh", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(startupCommand))
                throw new ArgumentException("Стартовая команда поддерживается только для PowerShell и cmd.");
            return $"\"{executable}\"";
        }

        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(Script(directory, startupCommand)));
        return $"\"{executable}\" -NoLogo -NoExit -EncodedCommand {encoded}";
    }

    internal static string Script(string? directory = null, string? startupCommand = null)
    {
        var location = string.IsNullOrEmpty(directory) ? "" :
            "Set-Location -LiteralPath '" + directory.Replace("'", "''") + "' -ErrorAction " +
            (string.IsNullOrWhiteSpace(startupCommand) ? "SilentlyContinue" : "Stop") + "\n";
        return location + """
            if (-not (Get-Variable -Name __TerminalVOriginalPrompt -Scope Global -ErrorAction SilentlyContinue)) {
                $global:__TerminalVOriginalPrompt = $function:prompt
                function global:prompt {
                    # Invoke first: custom prompts must see the previous command's $? and LASTEXITCODE.
                    $terminalVPrompt = & $global:__TerminalVOriginalPrompt
                    try {
                        $terminalVLocation = $executionContext.SessionState.Path.CurrentLocation
                        if ($terminalVLocation.Provider.Name -eq 'FileSystem') {
                            # The host writes Unicode to ConPTY without changing the user's output encoding.
                            $Host.UI.Write(([char]27 + ']9;9;"' + $terminalVLocation.ProviderPath + '"' + [char]7))
                        }
                    } catch { }
                    $terminalVPrompt
                }
            }
            """ + (string.IsNullOrWhiteSpace(startupCommand) ? "" : "\n" + startupCommand + "\n");
    }
}
