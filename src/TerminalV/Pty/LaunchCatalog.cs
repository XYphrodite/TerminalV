using System.Diagnostics;
using System.IO;
using System.Text;

namespace TerminalV.Pty;

internal sealed record LaunchTarget(string Id, string Title, string Shell, string? WslDistribution = null);
internal sealed record LaunchCatalogResult(IReadOnlyList<LaunchTarget> Targets, string? Notice);

internal static class LaunchCatalog
{
    public static async Task<LaunchCatalogResult> DiscoverAsync(CancellationToken cancellationToken)
    {
        var targets = new List<LaunchTarget>();
        foreach (var (shell, title) in new[] { ("powershell", "Windows PowerShell"), ("pwsh", "PowerShell 7"), ("cmd", "Командная строка") })
        {
            try { ShellResolver.Resolve(shell: shell); targets.Add(new(shell, title, shell)); }
            catch (FileNotFoundException) { }
        }
        if (!File.Exists(WslSupport.Executable))
            return new(targets, "WSL не установлен. Установленные дистрибутивы появятся здесь после обновления списка.");

        // This query only enumerates installed distributions; it never launches or installs Linux.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var process = new Process { StartInfo = new ProcessStartInfo(WslSupport.Executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.Unicode, StandardErrorEncoding = Encoding.Unicode,
            ArgumentList = { "--list", "--quiet" }
        } };
        try
        {
            process.Start();
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(output, error, process.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
            if (process.ExitCode != 0)
                return new(targets, "Не удалось получить список WSL. Проверьте WSL в Windows и обновите список.");
            var names = WslSupport.ParseDistributions(await output.ConfigureAwait(false));
            targets.AddRange(names.Select(name => new LaunchTarget("wsl:" + name, name, "wsl", name)));
            return new(targets, names.Length == 0 ? "Дистрибутивы WSL не найдены. После установки обновите список." : null);
        }
        catch (OperationCanceledException)
        {
            // Stop only our enumeration process, never a distribution or another terminal.
            try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
            if (cancellationToken.IsCancellationRequested) throw;
            return new(targets, "WSL не ответил за 5 секунд. Можно повторить обновление списка.");
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new(targets, "WSL недоступен. Проверьте его установку и обновите список.");
        }
    }
}

internal static class WslSupport
{
    public static string Executable => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wsl.exe");

    public static bool IsDistributionName(string? value) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 256 && value == value.Trim() && !value.StartsWith('-') &&
        value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');

    public static string[] ParseDistributions(string output) => output.TrimStart('\uFEFF')
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(name => name.Trim())
        .Where(IsDistributionName)
        .Where(name => !name.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase) &&
            !name.StartsWith("rancher-desktop", StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToArray();

    public static string CommandLine(string executable, string? distribution)
    {
        if (!IsDistributionName(distribution)) throw new ArgumentException("Некорректное имя дистрибутива WSL.");
        // WSL treats quotes as part of a distro name (microsoft/WSL#9792).
        // The strict name allowlist keeps this an option-free single token, with no intermediate shell.
        return $"\"{executable}\" --distribution {distribution} --cd ~";
    }
}
