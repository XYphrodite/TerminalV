import { t } from "./i18n.js";

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
  if (!gateway || typeof gateway !== "object") return t("Gateway_NoData");
  if (gateway.status && !gateway.listening) return t("Gateway_WithStatus", { status: gateway.status });
  if (gateway.listening) return t("Gateway_Listening", { port: gateway.port ?? GATEWAY_DEFAULT_PORT });
  if (gateway.enabled === false) return t("Gateway_Disabled");
  if (gateway.status) return t("Gateway_WithStatus", { status: gateway.status });
  return t("Gateway_Stopped");
}

export function gatewayConnectHint(port, hasToken) {
  const p = Number.isInteger(port) ? port : GATEWAY_DEFAULT_PORT;
  return hasToken
    ? t("Gateway_HintWithToken", { port: p })
    : t("Gateway_HintNoToken", { port: p });
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
