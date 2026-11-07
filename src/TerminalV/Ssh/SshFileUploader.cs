using System.IO;
using System.Text;
using Renci.SshNet;

namespace TerminalV.Ssh;

/// <summary>
/// Uploads pasted or picked files over SFTP when connected directly via SSH
/// (without the TerminalV desktop gateway). Also provides strict file-extension
/// sanitization shared with the HTTP gateway upload endpoint.
/// </summary>
public static class SshFileUploader
{
    public const long MaxUploadBytes = 256L * 1024 * 1024;

    public static string SanitizeExtension(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return ".bin";

        var trimmed = fileName.Trim();
        var slash = trimmed.LastIndexOfAny(['/', '\\']);
        if (slash >= 0)
            trimmed = trimmed[(slash + 1)..];

        var dot = trimmed.LastIndexOf('.');
        if (dot < 0 || dot == trimmed.Length - 1)
            return ".bin";

        var raw = trimmed[(dot + 1)..];
        var sb = new StringBuilder(Math.Min(raw.Length, 12));
        foreach (var ch in raw)
        {
            if (ch is >= 'a' and <= 'z')
                sb.Append(ch);
            else if (ch is >= 'A' and <= 'Z')
                sb.Append((char)(ch + 32));
            else if (ch is >= '0' and <= '9')
                sb.Append(ch);
            else
                return ".bin";

            if (sb.Length > 12)
                return ".bin";
        }

        return sb.Length == 0 ? ".bin" : "." + sb;
    }

    public static async Task<string> UploadAsync(SshConnectionOptions options, Stream content,
        string? fileName = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(content);
        options.Validate();

        var ext = SanitizeExtension(fileName);
        var generatedName = $"paste-{Guid.NewGuid():N}{ext}";
        var connectionInfo = BuildConnectionInfo(options);

        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var sftp = new SftpClient(connectionInfo);
            sftp.Connect();
            try
            {
                string remoteDir;
                if (SafeExists(sftp, "/tmp"))
                {
                    remoteDir = "/tmp/TerminalV-pastes";
                }
                else
                {
                    var home = string.IsNullOrWhiteSpace(sftp.WorkingDirectory) ? "." : sftp.WorkingDirectory.TrimEnd('/');
                    remoteDir = string.IsNullOrEmpty(home) ? "/TerminalV-pastes" : $"{home}/.terminalv-pastes";
                }

                if (!SafeExists(sftp, remoteDir))
                {
                    try { sftp.CreateDirectory(remoteDir); } catch { }
                }

                var sftpPath = $"{remoteDir.TrimEnd('/')}/{generatedName}";
                using (var remoteStream = sftp.Create(sftpPath))
                {
                    var buffer = new byte[65536];
                    long total = 0;
                    int read;
                    while ((read = content.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        total += read;
                        if (total > MaxUploadBytes)
                            throw new InvalidOperationException("Файл слишком большой (максимум 256 МБ).");
                        remoteStream.Write(buffer, 0, read);
                    }
                    remoteStream.Flush();
                }

                return FormatShellPath(sftpPath);
            }
            finally
            {
                try { if (sftp.IsConnected) sftp.Disconnect(); } catch { }
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    internal static string FormatShellPath(string sftpPath)
    {
        // Windows OpenSSH SFTP reports drive roots as /C:/Users/...
        if (sftpPath.Length >= 4 && sftpPath[0] == '/' && char.IsAsciiLetter(sftpPath[1]) && sftpPath[2] == ':' && sftpPath[3] == '/')
            return sftpPath[1..].Replace('/', '\\');
        return sftpPath;
    }

    private static bool SafeExists(SftpClient sftp, string path)
    {
        try { return sftp.Exists(path); }
        catch { return false; }
    }

    private static ConnectionInfo BuildConnectionInfo(SshConnectionOptions options)
    {
        var password = options.Password ?? "";
        var authMethods = new List<AuthenticationMethod>();

        if (!string.IsNullOrEmpty(password))
        {
            authMethods.Add(new PasswordAuthenticationMethod(options.Username, password));
            if (SshKeyboardAuthentication.Create(options.Username, password) is AuthenticationMethod kbd)
                authMethods.Add(kbd);
        }

        if (!string.IsNullOrWhiteSpace(options.PrivateKeyContent))
        {
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(options.PrivateKeyContent));
            var pk = string.IsNullOrEmpty(options.PrivateKeyPassphrase)
                ? new PrivateKeyFile(ms)
                : new PrivateKeyFile(ms, options.PrivateKeyPassphrase);
            authMethods.Add(new PrivateKeyAuthenticationMethod(options.Username, pk));
        }
        else if (!string.IsNullOrWhiteSpace(options.PrivateKeyPath))
        {
            if (!File.Exists(options.PrivateKeyPath))
                throw new FileNotFoundException("Private key file not found.", options.PrivateKeyPath);
            var pk = string.IsNullOrEmpty(options.PrivateKeyPassphrase)
                ? new PrivateKeyFile(options.PrivateKeyPath)
                : new PrivateKeyFile(options.PrivateKeyPath, options.PrivateKeyPassphrase);
            authMethods.Add(new PrivateKeyAuthenticationMethod(options.Username, pk));
        }

        if (authMethods.Count == 0)
            authMethods.Add(new PasswordAuthenticationMethod(options.Username, password));

        return new ConnectionInfo(options.Host, options.Port, options.Username, [.. authMethods])
        {
            Timeout = options.ConnectTimeout
        };
    }
}
