# SSH Service for TerminalV (MAUI + WPF)

Direct OpenSSH via SSH.NET `SshClient` + `ShellStream` and TerminalV gateway `ClientWebSocket`.

- Host/port/user/auth: password, PrivateKeyPath/PrivateKeyContent + passphrase.
- Terminal type: `xterm-256color` (default), cols/rows clamped 1..1000, resize via window-change or gateway JSON `{type:"resize"}`.
- Reconnect: exponential backoff, AutoReconnect + MaxReconnectAttempts, also triggered on ReadLoop fault.

Usage:

```csharp
var opts = new SshConnectionOptions { Host="example.com", Port=22, Username="user", Password="...", TerminalType="xterm-256color", Columns=120, Rows=30 };
var svc = new SshService();
var session = svc.Create("s1", opts);
session.DataReceived += data => Console.Write(data);
await session.ConnectAsync();
await session.WriteAsync("ls\n");
await session.ResizeAsync(100, 30);
await session.ReconnectAsync();
```

Gateway mode: set `GatewayUrl` (ws:// or wss:// or http(s) converted) + optional `GatewayToken`. Then `GatewaySshSession` uses `ClientWebSocket` with query params + JSON handshake.

Requires: `dotnet add package SSH.NET` (Renci.SshNet / SSH.NET). Reflection fallback allows build without package.
