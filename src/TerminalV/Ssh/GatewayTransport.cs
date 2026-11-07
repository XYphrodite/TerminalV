using System.IO;
using System.Net.Http;
using System.Net.WebSockets;

namespace TerminalV.Ssh;

public static class GatewayTransport
{
    public static HttpMessageInvoker? CreateHandler(Func<string, int, CancellationToken, Task<Stream>>? dial)
    {
        if (dial is null) return null;
        return new HttpMessageInvoker(new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false,
            ConnectCallback = async (context, ct) => await dial(context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct)
        });
    }
    public static HttpClient CreateHttpClient(Func<string, int, CancellationToken, Task<Stream>>? dial, TimeSpan timeout)
    {
        var handler = dial is null
            ? new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }
            : new SocketsHttpHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                ConnectCallback = async (context, ct) => await dial(context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct).ConfigureAwait(false)
            };
        return new HttpClient(handler, disposeHandler: true) { Timeout = timeout };
    }
    public static Task ConnectAsync(ClientWebSocket ws, Uri uri, HttpMessageInvoker? transport, CancellationToken ct)
        => transport is null ? ws.ConnectAsync(uri, ct) : ws.ConnectAsync(uri, transport, ct);
}
