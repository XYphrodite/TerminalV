// The fixture always injects a private pipe and no-op host starter. These names
// deliberately cannot address the user's host even if a test forgets to do so.
namespace TerminalV.Host
{
    internal static class SessionHost
    {
        public const string PipeName = "TerminalV.Tests.NoDefaultHost";
        public const string MutexName = "TerminalV.Tests.NoDefaultHost";
    }
}
namespace TerminalV.Diagnostics
{
    internal static class Diag
    {
        public static void Log(string category, string message, string? id) { }
    }
}
