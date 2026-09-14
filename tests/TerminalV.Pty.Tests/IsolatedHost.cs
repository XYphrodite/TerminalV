using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

// A private transport fixture. It never starts a shell or touches TerminalV.Host.
internal sealed class IsolatedHost : IDisposable
{
    public string PipeName { get; } = "TerminalV.Profile.Test." + Guid.NewGuid().ToString("N");
    public ConcurrentQueue<JsonElement> Requests { get; } = new();
    private readonly NamedPipeServerStream _pipe;
    private readonly Task _serve;

    public IsolatedHost(bool supportsProfiles, bool supportsWsl = false)
    {
        _pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        _serve = Task.Run(async () =>
        {
            try
            {
                await _pipe.WaitForConnectionAsync();
                using var reader = new StreamReader(_pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
                using var writer = new StreamWriter(_pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                while (await reader.ReadLineAsync() is { } line)
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement.Clone();
                    Requests.Enqueue(root);
                    switch (root.GetProperty("type").GetString())
                    {
                        case "list":
                            await writer.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(new {
                                type = "list", ids = Array.Empty<string>(), launchProfilesSupported = supportsProfiles,
                                wslLaunchSupported = supportsWsl }));
                            break;
                        case "attach":
                            await writer.WriteLineAsync(JsonSerializer.Serialize(new {
                                type = "data", id = root.GetProperty("id").GetString(), data = "cached\a" }));
                            break;
                    }
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        });
    }

    public void Dispose()
    {
        _pipe.Dispose();
        _serve.Wait(TimeSpan.FromSeconds(5));
    }
}
