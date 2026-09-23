import assert from "node:assert/strict";
import test from "node:test";
import {
  GATEWAY_DEFAULT_PORT,
  normalizeGatewaySettings,
  gatewayStatusText,
  gatewayConnectHint,
  generateGatewayToken,
} from "../src/gateway-settings.js";

test("gateway settings default to enabled :5454 with empty token", () => {
  assert.deepEqual(normalizeGatewaySettings({}), { enabled: true, port: 5454, token: "" });
  assert.equal(GATEWAY_DEFAULT_PORT, 5454);
});

test("gateway port out of range falls back to 5454, explicit disable respected", () => {
  assert.equal(normalizeGatewaySettings({ gatewayPort: 0 }).port, 5454);
  assert.equal(normalizeGatewaySettings({ gatewayPort: 99999 }).port, 5454);
  assert.equal(normalizeGatewaySettings({ gatewayPort: "abc" }).port, 5454);
  assert.equal(normalizeGatewaySettings({ gatewayPort: 2222 }).port, 2222);
  assert.equal(normalizeGatewaySettings({ gatewayEnabled: false }).enabled, false);
  assert.equal(normalizeGatewaySettings({ gatewayToken: "t" }).token, "t");
});

test("gateway status text prefers listening, then server status", () => {
  assert.equal(gatewayStatusText(null), "Шлюз: нет данных.");
  assert.equal(gatewayStatusText({ enabled: false, listening: false }), "Шлюз отключён.");
  assert.equal(
    gatewayStatusText({ enabled: true, port: 5454, listening: true }),
    "Шлюз слушает ws://0.0.0.0:5454."
  );
  assert.equal(
    gatewayStatusText({ enabled: true, listening: false, status: "Нет прав на прослушивание порта." }),
    "Шлюз: Нет прав на прослушивание порта."
  );
});

test("gateway connect hint warns when tokenless", () => {
  assert.match(gatewayConnectHint(5454, true), /5454 \+ токен/);
  assert.match(gatewayConnectHint(5454, false), /всем в сети/);
});

test("generated token is base64url without padding", () => {
  const token = generateGatewayToken((n) => new Uint8Array(n).fill(7));
  assert.match(token, /^[A-Za-z0-9_-]+$/);
  assert.equal(token.length, 32);
});
