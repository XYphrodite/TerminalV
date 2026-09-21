namespace TerminalV.Ssh;

/// <summary>
/// Вшитый Tailscale (tsnet) — userspace без системного VPN слота.
/// Не конфликтует с Happ/любым другим VPN на телефоне: TUN внутри процесса.
/// </summary>
public sealed class TailscaleOptions
{
    /// <summary>Включить вшитый tsnet вместо системного tailscaled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Auth key из admin.tailscale.com → Settings → Keys → Generate auth key (одноразовый, ephemerальный).</summary>
    public string? AuthKey { get; set; }

    /// <summary>Hostname в tailnet (например terminalv-mobile). Пусто = auto.</summary>
    public string? Hostname { get; set; }

    /// <summary>Control URL, обычно https://controlplane.tailscale.com. Пусто = default.</summary>
    public string? ControlUrl { get; set; }

    /// <summary>Путь к state dir внутри app (Android: filesDir). Пусто = default temp.</summary>
    public string? StateDir { get; set; }

    /// <summary>Log verbosity 0..2.</summary>
    public int LogVerbosity { get; set; }

    public void Validate()
    {
        if (!Enabled) return;
        if (!string.IsNullOrWhiteSpace(AuthKey) && AuthKey!.Any(char.IsControl))
            throw new ArgumentException("Некорректный Tailscale auth key.", nameof(AuthKey));
        if (AuthKey is not null && AuthKey.Length > 1024)
            throw new ArgumentException("Auth key слишком длинный.", nameof(AuthKey));
        if (Hostname is not null)
        {
            if (Hostname.Any(char.IsControl) || Hostname.Length > 63)
                throw new ArgumentException("Некорректный hostname.", nameof(Hostname));
            if (Hostname.Contains(' ') || Hostname.Contains('\0'))
                throw new ArgumentException("Hostname содержит недопустимые символы.", nameof(Hostname));
        }
        if (ControlUrl is not null && !string.IsNullOrWhiteSpace(ControlUrl))
        {
            if (!Uri.TryCreate(ControlUrl, UriKind.Absolute, out var u) ||
                (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp))
                throw new ArgumentException("Некорректный Control URL.", nameof(ControlUrl));
        }
        if (StateDir is not null && StateDir.Any(c => c == '\0'))
            throw new ArgumentException("Некорректный StateDir.", nameof(StateDir));
    }

    public TailscaleOptions Clone() => (TailscaleOptions)MemberwiseClone();
}
