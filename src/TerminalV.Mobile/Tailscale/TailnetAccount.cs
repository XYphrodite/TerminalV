using System.Net.Http;
using System.Text.Json;
using TerminalV.Ssh;

namespace TerminalV.Mobile.Tailscale;

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
        await Task.CompletedTask;
        throw new PlatformNotSupportedException("Вход через Tailscale доступен в Android-клиенте.");
#endif
    }
    public async Task StartAsync(TailscaleOptions options, CancellationToken ct = default)
    {
        var status = await StatusAsync(true, ct);
        for (var i = 0; !status.Running && i < 5; i++)
        {
            await Task.Delay(500, ct);
            status = await StatusAsync(false, ct);
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
        throw new PlatformNotSupportedException();
#endif
    }
    private async Task<JsonElement> RequestAsync(string ip, int port, bool pair, CancellationToken ct)
    {
        using var transport = GatewayTransport.CreateHandler(DialAsync)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(pair ? 65 : 5));
        using var request = new HttpRequestMessage(pair ? HttpMethod.Post : HttpMethod.Get, $"http://{ip}:{port}/terminalv/{(pair ? "pair" : "info")}");
        request.Headers.TryAddWithoutValidation("Authorization", "Tailscale");
        using var response = await transport.SendAsync(request, timeout.Token);
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden) throw new InvalidOperationException("Доступ не подтверждён на ПК. Повторите запрос и нажмите «Разрешить» на компьютере.");
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(timeout.Token);
        if (text.Length > 8192) throw new IOException("Некорректный ответ шлюза.");
        var result = JsonSerializer.Deserialize<JsonElement>(text);
        if (result.GetProperty("protocol").GetString() != "terminalv-tailnet-v1") throw new IOException("Обновите TerminalV на ПК.");
        return result;
    }
    public async Task<List<TailnetComputer>> FindComputersAsync(TailnetStatus status, int port, CancellationToken ct)
    {
        var found = new List<TailnetComputer>();
        // Keep discovery bounded; no shell commands or port scanning.
        await Parallel.ForEachAsync(status.Peers.Take(100), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (peer, token) =>
        {
            try
            {
                var info = await RequestAsync(peer.Ip, port, false, token);
                lock (found) found.Add(new(peer.Id, info.GetProperty("name").GetString() ?? peer.Name, peer.Ip, port, info.GetProperty("approved").GetBoolean()));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { }
        });
        return found.OrderBy(p => p.Name).ToList();
    }
    public async Task PairAsync(TailnetComputer computer, CancellationToken ct) => await RequestAsync(computer.Ip, computer.Port, true, ct);
    public async Task LogoutAsync(CancellationToken ct)
    {
#if ANDROID
        await _native.AccountAsync("logout", ct);
#else
        await Task.CompletedTask;
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
