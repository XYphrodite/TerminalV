using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace TerminalV.Shell;

internal sealed record ShortcutResult(string Destination, string? Path, string? Error);

/// <summary>User-only shortcuts, created on explicit request, never on application startup.</summary>
internal sealed class ShortcutService(
    string? executable,
    Func<Environment.SpecialFolder, string>? folderPath = null,
    Action<string, bool>? notify = null)
{
    public bool Supported => executable is not null && Path.IsPathFullyQualified(executable)
        && string.Equals(Path.GetFileName(executable), "TerminalV.exe", StringComparison.OrdinalIgnoreCase)
        && File.Exists(executable);

    public Task<IReadOnlyList<ShortcutResult>> CreateAsync(bool startMenu, bool desktop)
    {
        // WSH is COM automation. Keep it on one STA thread and off the WebView UI thread,
        // including when a known folder is redirected to a slow network/OneDrive location.
        var completion = new TaskCompletionSource<IReadOnlyList<ShortcutResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                if (!Supported) throw new InvalidOperationException("Не найден TerminalV.exe для ярлыка.");
                if (!startMenu && !desktop) throw new ArgumentException("Выберите хотя бы одно расположение.");
                var results = new List<ShortcutResult>();
                if (startMenu) results.Add(Create("startMenu", Environment.SpecialFolder.Programs));
                if (desktop) results.Add(Create("desktop", Environment.SpecialFolder.DesktopDirectory));
                completion.SetResult(results);
            }
            catch (Exception error) { completion.SetException(error); }
        }) { IsBackground = true, Name = "TerminalV shortcuts" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private ShortcutResult Create(string destination, Environment.SpecialFolder specialFolder)
    {
        string? path = null;
        string? temporary = null;
        object? shell = null;
        object? link = null;
        try
        {
            var folder = folderPath is null ? Environment.GetFolderPath(specialFolder) : folderPath(specialFolder);
            if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
                throw new IOException("Windows не вернула папку для ярлыка.");
            Directory.CreateDirectory(folder);
            path = Path.Combine(folder, "TerminalV.lnk");
            var existed = Path.Exists(path);
            if (existed && (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new IOException("Путь TerminalV.lnk занят папкой или символической ссылкой.");

            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", throwOnError: true)!)!;
            // Work on a private copy; a failed Save must not damage an existing shortcut.
            temporary = Path.Combine(folder, $".TerminalV-{Guid.NewGuid():N}.lnk");
            if (existed) File.Copy(path, temporary);
            link = ((dynamic)shell).CreateShortcut(temporary);
            if (existed)
            {
                var target = (string)((dynamic)link).TargetPath;
                var arguments = (string)((dynamic)link).Arguments;
                if (!string.Equals(target, executable, StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(arguments))
                    throw new IOException("Ярлык TerminalV.lnk уже ведёт к другой копии или содержит аргументы. Переименуйте его и повторите.");
                // Keep any user customizations (working directory, description, hotkey).
            }
            else
            {
                ((dynamic)link).TargetPath = executable!;
                ((dynamic)link).WorkingDirectory = Path.GetDirectoryName(executable)!;
                ((dynamic)link).Description = "Terminal with vertical tabs";
                ((dynamic)link).WindowStyle = 1;
            }
            ((dynamic)link).IconLocation = executable + ",0";
            ((dynamic)link).Save();
            if (!File.Exists(temporary)) throw new IOException("Windows не сохранила ярлык.");
            Marshal.FinalReleaseComObject(link);
            link = null;
            if (existed) File.Replace(temporary, path, null);
            else File.Move(temporary, path); // Do not overwrite a shortcut created concurrently.
            temporary = null;
            try { (notify ?? NotifyShell)(path, !existed); }
            catch (Exception error) { Debug.WriteLine(error); } // The shortcut itself was saved.
            return new(destination, path, null);
        }
        catch (Exception error) { return new(destination, path, error.Message); }
        finally
        {
            if (link is not null) Marshal.FinalReleaseComObject(link);
            if (shell is not null) Marshal.FinalReleaseComObject(shell);
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception error) { Debug.WriteLine(error); }
            }
        }
    }

    private static void NotifyShell(string path, bool created) =>
        SHChangeNotify(created ? 0x2 : 0x2000, 0x5 | 0x2000, path, IntPtr.Zero);

    // SHCNE_CREATE / SHCNE_UPDATEITEM, SHCNF_PATHW | SHCNF_FLUSHNOWAIT.
    // Notify only this link: no Explorer restart, global icon-cache purge or taskbar pinning.
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern void SHChangeNotify(int eventId, uint flags, string item1, IntPtr item2);
}
