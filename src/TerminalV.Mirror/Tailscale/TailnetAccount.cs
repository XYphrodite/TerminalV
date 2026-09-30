using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using TerminalV.Ssh;

namespace TerminalV.Mirror.Tailscale;

public sealed record TailnetPeer(string Id, string Name, string Ip);
public sealed record TailnetStatus(bool Running, string State, string AuthUrl, string Login, List<TailnetPeer> Peers);
public sealed record TailnetComputer(string Id, string Name, string Ip, int Port, bool Approved);

public sealed class TailnetAccount : ITailscaleConnector
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
#if ANDROID
    private readonly AndroidTailnetAccount _native = new();
#endif
    public bool IsRunning { get; private set; }
    public string? LastError { get; private set; }
    public async Task<TailnetStatus> StatusAsync(bool begin, CancellationToken ct)
    {
#if ANDROID
        var raw = await _native.AccountAsync(begin ? "begin" : "status", ct);
        var status = JsonSerializer.Deserialize<TailnetStatus>(raw, Json) ?? throw new IOException("Не удалось прочитать состояние Tailscale.");
        IsRunning = status.Running;
        return status;
#else
        var statusWin = await GetWindowsStatusAsync(ct).ConfigureAwait(false);
        IsRunning = statusWin.Running;
        LastError = null;
        return statusWin;
#endif
    }
    public async Task StartAsync(TailscaleOptions options, CancellationToken ct = default)
    {
        var status = await StatusAsync(true, ct).ConfigureAwait(false);
        for (var i = 0; !status.Running && i < 5; i++)
        {
            await Task.Delay(500, ct).ConfigureAwait(false);
            status = await StatusAsync(false, ct).ConfigureAwait(false);
        }
        if (!status.Running) throw new InvalidOperationException("Войдите через Tailscale в настройках подключения.");
        var selected = Preferences.Default.Get("tailnetComputerId", "");
        if (!string.IsNullOrEmpty(selected))
        {
            var peer = status.Peers.FirstOrDefault(p => p.Id == selected)
                ?? throw new InvalidOperationException("Выбранный ПК недоступен. Проверьте Tailscale на компьютере или выберите другой ПК.");
            var port = Preferences.Default.Get("tailnetComputerPort", 5454);
            Preferences.Default.Set("gatewayUrl", $"ws://{peer.Ip}:{port}");
            if (!string.IsNullOrWhiteSpace(peer.Name)) Preferences.Default.Set("tailnetComputerName", peer.Name);
        }
    }
    public Task<Stream> DialAsync(string host, int port, CancellationToken ct = default)
    {
#if ANDROID
        return _native.DialAsync(host, port, ct);
#else
        // System Tailscale routes 100.x/fd7a directly via OS TUN; no tsnet needed.
        return DialDirectAsync(host, port, ct);
#endif
    }

    private static async Task<Stream> DialDirectAsync(string host, int port, CancellationToken ct)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(host, port, ct).ConfigureAwait(false);
            return new NetworkStream(tcp.Client, ownsSocket: true);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

#if !ANDROID
    private static string FindTailscaleExe()
    {
        var candidates = new List<string>();
        // PATH lookup: try bare name first (Process.Start will resolve via PATH)
        // but we need to verify existence for error message; check common install locations.
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrEmpty(pf)) candidates.Add(Path.Combine(pf, "Tailscale", "tailscale.exe"));
        if (!string.IsNullOrEmpty(pf86)) candidates.Add(Path.Combine(pf86, "Tailscale", "tailscale.exe"));
        var envPf = Environment.GetEnvironmentVariable("ProgramFiles");
        if (!string.IsNullOrEmpty(envPf)) candidates.Add(Path.Combine(envPf, "Tailscale", "tailscale.exe"));
        var envPf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
        if (!string.IsNullOrEmpty(envPf86)) candidates.Add(Path.Combine(envPf86, "Tailscale", "tailscale.exe"));
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        // Fallback to PATH - let Process.Start try "tailscale" or "tailscale.exe"
        return "tailscale.exe";
    }

    private static async Task<TailnetStatus> GetWindowsStatusAsync(CancellationToken ct)
    {
        var exe = FindTailscaleExe();
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("status");
        psi.ArgumentList.Add("--json");
        string output;
        string error;
        int exitCode;
        try
        {
            using var proc = Process.Start(psi) ?? throw new IOException("Не удалось запустить tailscale status.");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            var outTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var errTask = proc.StandardError.ReadToEndAsync(cts.Token);
            try { await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { try { proc.Kill(entireProcessTree: true); } catch { } throw; }
            catch (OperationCanceledException) { try { proc.Kill(entireProcessTree: true); } catch { } throw new TimeoutException("tailscale status не ответил за 8с. Проверьте что Tailscale запущен."); }
            output = await outTask.ConfigureAwait(false);
            error = await errTask.ConfigureAwait(false);
            exitCode = proc.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 2)
        {
            throw new PlatformNotSupportedException("Tailscale не установлен. Установите с https://tailscale.com/download и войдите в аккаунт.");
        }
        catch (PlatformNotSupportedException) { throw; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new IOException($"Не удалось получить статус Tailscale: {ex.Message}", ex);
        }

        if (exitCode != 0 && string.IsNullOrWhiteSpace(output))
        {
            var msg = string.IsNullOrWhiteSpace(error) ? $"tailscale status завершился с кодом {exitCode}" : error.Trim();
            // When tailscaled not running, status may still return JSON with BackendState; treat empty as error
            if (msg.Contains("not running", StringComparison.OrdinalIgnoreCase) || msg.Contains("No such file", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Служба Tailscale не запущена. Запустите Tailscale и войдите.");
            throw new IOException(msg);
        }

        if (string.IsNullOrWhiteSpace(output))
            throw new IOException("Пустой ответ от tailscale status.");

        try
        {
            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;
            var backendState = root.TryGetProperty("BackendState", out var bs) ? bs.GetString() ?? "" : "";
            var authUrl = root.TryGetProperty("AuthURL", out var au) ? au.GetString() ?? "" : "";
            var running = string.Equals(backendState, "Running", StringComparison.OrdinalIgnoreCase);
            string login = "";
            if (root.TryGetProperty("Self", out var self) && self.ValueKind == JsonValueKind.Object)
            {
                string? uidStr = null;
                if (self.TryGetProperty("UserID", out var uidEl))
                {
                    uidStr = uidEl.ValueKind switch
                    {
                        JsonValueKind.Number => uidEl.GetInt64().ToString(),
                        JsonValueKind.String => uidEl.GetString(),
                        _ => uidEl.ToString().Trim('"')
                    };
                }
                if (!string.IsNullOrEmpty(uidStr) && root.TryGetProperty("User", out var users) && users.ValueKind == JsonValueKind.Object
                    && users.TryGetProperty(uidStr!, out var userEl) && userEl.ValueKind == JsonValueKind.Object
                    && userEl.TryGetProperty("LoginName", out var loginEl))
                {
                    login = loginEl.GetString() ?? "";
                }
            }

            var peers = new List<TailnetPeer>();
            if (root.TryGetProperty("Peer", out var peerObj) && peerObj.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in peerObj.EnumerateObject())
                {
                    var p = prop.Value;
                    if (p.ValueKind != JsonValueKind.Object) continue;
                    if (p.TryGetProperty("Online", out var onlineEl) && !onlineEl.GetBoolean()) continue;
                    // Keep windows peers like tsnet.go; also allow unknown OS (some versions omit OS)
                    if (p.TryGetProperty("OS", out var osEl))
                    {
                        var os = osEl.GetString();
                        if (!string.IsNullOrEmpty(os) && !string.Equals(os, "windows", StringComparison.OrdinalIgnoreCase))
                            continue;
                    }
                    if (!p.TryGetProperty("TailscaleIPs", out var ipsEl) || ipsEvLength(ipsEl) == 0) continue;
                    string ip = "";
                    if (ipsEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var ipEl in ipsEl.EnumerateArray())
                        {
                            var s = ipEl.GetString() ?? "";
                            if (s.Contains('.')) { ip = s; break; }
                        }
                        if (string.IsNullOrEmpty(ip) && ipsEl.GetArrayLength() > 0) ip = ipsEl[0].GetString() ?? "";
                    }
                    if (string.IsNullOrEmpty(ip)) continue;
                    string name = p.TryGetProperty("HostName", out var hn) ? hn.GetString() ?? prop.Name : prop.Name;
                    // Use map key as stable ID (tailscale node key like "nXXX"), fallback to ID field
                    string id = prop.Name;
                    if (p.TryGetProperty("ID", out var idEl))
                    {
                        var rawId = idEl.ValueKind switch
                        {
                            JsonValueKind.Number => idEl.GetInt64().ToString(),
                            JsonValueKind.String => idEl.GetString() ?? "",
                            _ => idEl.ToString().Trim('"')
                        };
                        if (!string.IsNullOrWhiteSpace(rawId)) id = rawId;
                    }
                    peers.Add(new TailnetPeer(id, name, ip));
                }
            }

            return new TailnetStatus(running, backendState, authUrl, login, peers);
        }
        catch (JsonException ex)
        {
            throw new IOException($"Некорректный JSON от tailscale status: {ex.Message}", ex);
        }
    }

    private static int ipsEvLength(JsonElement el) => el.ValueKind == JsonValueKind.Array ? el.GetArrayLength() : 0;
#endif

    private async Task<JsonElement> RequestAsync(string ip, int port, bool pair, CancellationToken ct)
    {
#if ANDROID
        using var transport = GatewayTransport.CreateHandler(DialAsync)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(pair ? 65 : 5));
        using var request = new HttpRequestMessage(pair ? HttpMethod.Post : HttpMethod.Get, $"http://{ip}:{port}/terminalv/{(pair ? "pair" : "info")}");
        request.Headers.TryAddWithoutValidation("Authorization", "Tailscale");
        using var response = await transport.SendAsync(request, timeout.Token).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden) throw new InvalidOperationException("ПК не разрешил подключение.");
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
        if (text.Length > 8192) throw new IOException("Некорректный ответ шлюза.");
        var result = JsonSerializer.Deserialize<JsonElement>(text);
        if (result.GetProperty("protocol").GetString() != "terminalv-tailnet-v1") throw new IOException("Обновите TerminalV на ПК.");
        return result;
#else
        // Windows system Tailscale: 100.x is routable via OS TUN, no custom dial needed. Use plain HttpClient.
        // But also support transport via DialDirectAsync for consistency (loopback not needed).
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(pair ? 65 : 5) };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(pair ? 65 : 5));
        using var request = new HttpRequestMessage(pair ? HttpMethod.Post : HttpMethod.Get, $"http://{ip}:{port}/terminalv/{(pair ? "pair" : "info")}");
        request.Headers.TryAddWithoutValidation("Authorization", "Tailscale");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden) throw new InvalidOperationException("ПК не разрешил подключение. Подтвердите телефон в окне TerminalV на ПК.");
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        if (text.Length > 8192) throw new IOException("Некорректный ответ шлюза.");
        var result = JsonSerializer.Deserialize<JsonElement>(text);
        if (!result.TryGetProperty("protocol", out var proto) || proto.GetString() != "terminalv-tailnet-v1") throw new IOException("Обновите TerminalV на ПК до 0.7.16+.");
        return result;
#endif
    }
    public async Task<List<TailnetComputer>> FindComputersAsync(TailnetStatus status, int port, CancellationToken ct)
    {
        var found = new List<TailnetComputer>();
        // Keep discovery bounded; no shell commands or port scanning.
        await Parallel.ForEachAsync(status.Peers.Take(100), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (peer, token) =>
        {
            try
            {
                var info = await RequestAsync(peer.Ip, port, false, token).ConfigureAwait(false);
                lock (found) found.Add(new(peer.Id, info.GetProperty("name").GetString() ?? peer.Name, peer.Ip, port, info.GetProperty("approved").GetBoolean()));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { }
        });
        return found.OrderBy(p => p.Name).ToList();
    }
    public async Task PairAsync(TailnetComputer computer, CancellationToken ct) => await RequestAsync(computer.Ip, computer.Port, true, ct).ConfigureAwait(false);
    public async Task LogoutAsync(CancellationToken ct)
    {
#if ANDROID
        await _native.AccountAsync("logout", ct).ConfigureAwait(false);
#else
        await Task.CompletedTask.ConfigureAwait(false);
        // System Tailscale logout is OS-wide: do not call `tailscale logout` here.
        // Just clear local tailnet pairing state; OS login stays.
#endif
        IsRunning = false;
    }
    public Task StopAsync() => Task.CompletedTask;
    public void Dispose()
    {
#if ANDROID
        _native.Dispose();
#endif
    }
}
