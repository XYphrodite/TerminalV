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

Mirror mode (desktop session duplication): point `GatewayUrl` at a TerminalV mirror gateway (`ws://pc:5454`, served by `src/TerminalV/Gateway/GatewayServer`) and set `MirrorSessionId` to a live desktop session id. The handshake carries `sessionId`, the server attaches the live ConPTY (snapshot replay + live stream) instead of proxying SSH. Server bookkeeping frames (`attached`/`created`/`sessions`/`cwd`, see `GatewayProtocol`) never reach the terminal. One `GatewaySshSession` = one mirrored tab (own WebSocket); `GatewayControlClient` (control handshake, no session) lists (`list` → `sessions`), creates (`create` → `attached`) and kills desktop sessions. Auth: `Authorization: Bearer <token>` (desktop: Settings → Remote access); empty server token allows LAN without auth.

Requires: `dotnet add package SSH.NET` (Renci.SshNet / SSH.NET). Reflection fallback allows build without package.
