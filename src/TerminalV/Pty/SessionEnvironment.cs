using System.ComponentModel;
using System.Collections;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace TerminalV.Pty;

// Each new shell gets the current Windows user/machine environment, not the
// long-lived host's stale PATH. Windows expands the registry variables; we then
// retain process-only variables (e.g. a launcher-provided SSH_AUTH_SOCK).
// Never modify the host's environment or inject commands into existing shells.
internal sealed class SessionEnvironment : SafeHandleZeroOrMinusOneIsInvalid
{
    private SessionEnvironment(string block) : base(ownsHandle: true)
        => SetHandle(Marshal.StringToHGlobalUni(block));

    public static SessionEnvironment Create()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query | TokenAccessLevels.Duplicate);
        // bInherit=true keeps the stale process PATH on Windows. Build from the
        // current user/machine state first, and merge only missing process keys.
        if (!CreateEnvironmentBlock(out var block, identity.AccessToken, inherit: false))
        {
            var error = Marshal.GetLastWin32Error();
            block.Dispose();
            throw new Win32Exception(error, "Не удалось получить актуальное окружение Windows для новой сессии.");
        }
        using (block)
        {
            var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var pointer = block.DangerousGetHandle();
            while (Marshal.PtrToStringUni(pointer) is { Length: > 0 } entry)
            {
                // Hidden drive-directory variables have names like "=C:".
                var separator = entry.IndexOf('=', 1);
                if (separator > 0) variables[entry[..separator]] = entry[(separator + 1)..];
                pointer = IntPtr.Add(pointer, checked((entry.Length + 1) * sizeof(char)));
            }
            GC.KeepAlive(block);
            foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            {
                var name = (string)entry.Key;
                if (!name.Equals("PATH", StringComparison.OrdinalIgnoreCase))
                    variables.TryAdd(name, (string)entry.Value!);
            }
            // StringToHGlobalUni adds the second terminating NUL.
            return new SessionEnvironment(string.Join('\0', variables.Select(pair => pair.Key + "=" + pair.Value)) + '\0');
        }
    }

    protected override bool ReleaseHandle()
    {
        Marshal.FreeHGlobal(handle);
        return true;
    }

    private sealed class WindowsEnvironment : SafeHandleZeroOrMinusOneIsInvalid
    {
        private WindowsEnvironment() : base(ownsHandle: true) { }
        protected override bool ReleaseHandle() => DestroyEnvironmentBlock(handle);
    }

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out WindowsEnvironment environment,
        SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);
}
