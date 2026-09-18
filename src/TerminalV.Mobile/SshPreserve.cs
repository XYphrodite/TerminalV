using Renci.SshNet;

namespace TerminalV.Mobile;

// Preserve SSH.NET assembly from trimming (SshNetSession uses reflection)
internal static class SshPreserve
{
    // This method is never called, but the reference keeps Renci.SshNet in the assembly store
    private static void Keep()
    {
        _ = typeof(SshClient);
        _ = typeof(ShellStream);
        _ = typeof(PrivateKeyFile);
        _ = typeof(PasswordAuthenticationMethod);
        _ = typeof(PrivateKeyAuthenticationMethod);
        _ = typeof(ConnectionInfo);
    }
}
