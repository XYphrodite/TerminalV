using System.Text.Json;
using TerminalV.Extensibility;

namespace HelloExtension;

public sealed class HelloExtension : ITerminalVExtension
{
    private IExtensionContext _context = null!;
    private Task _background = Task.CompletedTask;
    private CancellationTokenSource? _stop;
    private readonly object _gate = new();
    private int _count;

    public Task ActivateAsync(IExtensionContext context, CancellationToken lifetime)
    {
        _context = context;
        int.TryParse(context.Storage.Get("count"), out _count);
        _stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _background = HeartbeatAsync(_stop.Token);
        context.Log("Hello extension activated.");
        return Task.CompletedTask;
    }

    public Task<JsonElement?> InvokeAsync(string method, JsonElement? args, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            switch (method)
            {
                case "getState": break;
                case "increment":
                    _count++;
                    _context.Storage.Set("count", _count.ToString());
                    _context.Publish("changed", State());
                    break;
                default: throw new ArgumentException($"Unknown method: {method}");
            }
            return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(State()));
        }
    }

    private object State() => new { count = _count, updatedAt = DateTimeOffset.UtcNow };

    private async Task HeartbeatAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
                lock (_gate) _context.Publish("changed", State());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { _context.Log(ex.Message); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_stop is null) return;
        await _stop.CancelAsync();
        await _background;
        _stop.Dispose();
        _stop = null;
    }
}
