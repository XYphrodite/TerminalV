using System.Diagnostics;

var directory = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
var exe = Path.Combine(directory, "TerminalV.exe");
if (!File.Exists(exe))
{
    Console.Error.WriteLine("TerminalV.exe not found next to TerminalV.com.");
    return 1;
}

if (args.Length == 0)
{
    Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
    return 0;
}

var start = new ProcessStartInfo(exe)
{
    UseShellExecute = false,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    RedirectStandardInput = true,
    CreateNoWindow = true
};
foreach (var arg in args)
{
    start.ArgumentList.Add(arg);
}

using var process = Process.Start(start);
if (process is null)
{
    Console.Error.WriteLine("Failed to start TerminalV.exe.");
    return 1;
}

using var stdout = Console.OpenStandardOutput();
using var stderr = Console.OpenStandardError();
var copyOut = process.StandardOutput.BaseStream.CopyToAsync(stdout);
var copyErr = process.StandardError.BaseStream.CopyToAsync(stderr);
process.WaitForExit();
copyOut.GetAwaiter().GetResult();
copyErr.GetAwaiter().GetResult();
return process.ExitCode;
