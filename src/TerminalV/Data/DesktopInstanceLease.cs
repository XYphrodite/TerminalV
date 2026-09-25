using System.IO;

namespace TerminalV.Data;

internal sealed class DesktopInstanceLease : IDisposable
{
    private readonly FileStream _lockFile;

    private DesktopInstanceLease(FileStream lockFile) => _lockFile = lockFile;

    public static DesktopInstanceLease? TryAcquire(string dataDirectory)
    {
        try
        {
            // Scope ownership to the shared data directory, including other Windows
            // logon sessions. The OS releases the handle if the desktop crashes.
            return new DesktopInstanceLease(new FileStream(
                Path.Combine(dataDirectory, "desktop.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException ex) when (ex is DirectoryNotFoundException) { throw; }
        catch (IOException)
        {
            return null;
        }
    }

    // Keep the file: removing it could race another desktop acquiring ownership.
    public void Dispose() => _lockFile.Dispose();
}
