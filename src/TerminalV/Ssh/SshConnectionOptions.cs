namespace TerminalV.Ssh;

/// <summary>
/// Connection options for SSH. Supports direct OpenSSH via SSH.NET and TerminalV gateway WebSocket.
/// </summary>
public sealed class SshConnectionOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "";
    public string? Password { get; set; }
    public string? PrivateKeyPath { get; set; }
    public string? PrivateKeyContent { get; set; }
    public string? PrivateKeyPassphrase { get; set; }
    public string? GatewayUrl { get; set; }
    public string? GatewayToken { get; set; }
    /// <summary>
    /// Mirror mode: attach to a live desktop ConPTY session with this id
    /// instead of opening a proxied SSH shell. Requires <see cref="GatewayUrl"/>
    /// pointing at a TerminalV mirror gateway; host/user/password are unused.
    /// </summary>
    public string? MirrorSessionId { get; set; }
    public TailscaleOptions Tailscale { get; set; } = new();
    public bool UseTailscale => Tailscale.Enabled;
    public string TerminalType { get; set; } = "xterm-256color";
    public int Columns { get; set; } = 80;
    public int Rows { get; set; } = 24;
    public bool AutoReconnect { get; set; } = true;
    public int MaxReconnectAttempts { get; set; } = 5;
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(30);
    public bool UseGateway => !string.IsNullOrWhiteSpace(GatewayUrl);
    public void Validate()
    {
        var mirror = UseGateway && !string.IsNullOrWhiteSpace(MirrorSessionId);
        if (!mirror)
        {
            if (string.IsNullOrWhiteSpace(Host) || Host.Any(char.IsControl) || Host.Length > 253)
                throw new ArgumentException("Укажите корректный хост.", nameof(Host));
            if (string.IsNullOrWhiteSpace(Username) || Username.Any(char.IsControl) || Username.Length > 128)
                throw new ArgumentException("Укажите имя пользователя.", nameof(Username));
            if (Host.Contains(' ') || Host.Contains('\0'))
                throw new ArgumentException("Хост содержит недопустимые символы.", nameof(Host));
        }
        if (Port is < 1 or > 65535)
            throw new ArgumentException("Порт должен быть 1..65535.", nameof(Port));
        if (TerminalType is null || string.IsNullOrWhiteSpace(TerminalType) || TerminalType.Any(char.IsControl) || TerminalType.Length > 64)
            throw new ArgumentException("Некорректный тип терминала.", nameof(TerminalType));
        if (Columns is < 1 or > 1000 || Rows is < 1 or > 1000)
            throw new ArgumentException("Размер терминала вне диапазона 1..1000.", nameof(Columns));
        var hasPassword = !string.IsNullOrEmpty(Password);
        var hasKey = !string.IsNullOrWhiteSpace(PrivateKeyPath) || !string.IsNullOrWhiteSpace(PrivateKeyContent);
        if (!hasPassword && !hasKey && !UseGateway)
            throw new ArgumentException("Укажите пароль или приватный ключ для аутентификации.");
        if (PrivateKeyPath is not null && PrivateKeyPath.Any(c => c == '\0'))
            throw new ArgumentException("Некорректный путь к ключу.", nameof(PrivateKeyPath));
        if (GatewayUrl is not null)
        {
            if (!Uri.TryCreate(GatewayUrl, UriKind.Absolute, out var u) ||
                (u.Scheme != Uri.UriSchemeWs && u.Scheme != Uri.UriSchemeWss && u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
                throw new ArgumentException("Некорректный URL шлюза.", nameof(GatewayUrl));
        }
        Tailscale.Validate();
    }
    public SshConnectionOptions Clone()
    {
        var c = (SshConnectionOptions)MemberwiseClone();
        c.Tailscale = Tailscale.Clone();
        return c;
    }
}
