// Pure helpers for the "Remote access" (mirror gateway) settings section.
// Kept side-effect free so node:test can cover them without a DOM.

export const GATEWAY_DEFAULT_PORT = 5454;

export function normalizeGatewaySettings(raw = {}) {
  const port = Number(raw.gatewayPort);
  return {
    enabled: raw.gatewayEnabled !== false,
    port: Number.isInteger(port) && port >= 1 && port <= 65535 ? port : GATEWAY_DEFAULT_PORT,
    token: typeof raw.gatewayToken === "string" ? raw.gatewayToken : "",
  };
}

export function gatewayStatusText(gateway) {
  if (!gateway || typeof gateway !== "object") return "Шлюз: нет данных.";
  if (gateway.status && !gateway.listening) return `Шлюз: ${gateway.status}`;
  if (gateway.listening) return `Шлюз слушает ws://0.0.0.0:${gateway.port ?? GATEWAY_DEFAULT_PORT}.`;
  if (gateway.enabled === false) return "Шлюз отключён.";
  if (gateway.status) return `Шлюз: ${gateway.status}`;
  return "Шлюз остановлен.";
}

export function gatewayConnectHint(port, hasToken) {
  const p = Number.isInteger(port) ? port : GATEWAY_DEFAULT_PORT;
  return hasToken
    ? `В TerminalV Mobile: шлюз, ws://<этот-ПК>:${p} + токен выше.`
    : `В TerminalV Mobile: шлюз, ws://<этот-ПК>:${p}. Без токена доступен всем в сети!`;
}

// randomSource(n) exists for deterministic tests; default is WebCrypto.
export function generateGatewayToken(randomSource) {
  const bytes = randomSource
    ? randomSource(24)
    : (() => {
        const a = new Uint8Array(24);
        crypto.getRandomValues(a);
        return a;
      })();
  let binary = "";
  for (const b of bytes) binary += String.fromCharCode(b);
  return btoa(binary).replaceAll("+", "-").replaceAll("/", "_").replace(/=+$/, "");
}
