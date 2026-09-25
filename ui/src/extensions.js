// Trusted modules execute in the application's WebView. This API is not a sandbox.
export function createExtensionRuntime({ post, createSurface, notify = () => {}, changed = () => {},
  log = console.error, loadModule = (url) => import(/* @vite-ignore */ url), timeoutMs = 35000,
  activationTimeoutMs = 15000 }) {
  const pending = new Map(), scopes = new Map();
  let catalog = [], sequence = 0, closed = false;
  const session = globalThis.crypto.randomUUID();

  function request(type, extensionId = null, fields = {}) {
    if (closed) return Promise.reject(new Error("Extension runtime is closed."));
    const requestId = `${session}:${++sequence}`;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        pending.delete(requestId);
        reject(new Error("Extension request timed out."));
      }, timeoutMs);
      pending.set(requestId, { extensionId, resolve, reject, timer });
      try { post({ ...fields, type, requestId, extensionId }); }
      catch (error) { settle(requestId, error); }
    });
  }

  function settle(requestId, error, result) {
    const waiter = pending.get(requestId);
    if (!waiter) return;
    pending.delete(requestId);
    clearTimeout(waiter.timer);
    if (error) waiter.reject(error instanceof Error ? error : new Error(String(error)));
    else waiter.resolve(result);
  }

  function report(id, error) {
    log(`[extension ${id}]`, error);
    const item = catalog.find(item => item.id === id);
    if (item) item.uiError = error?.message || String(error);
    changed();
  }

  function ensure(scope) {
    if (scope.controller.signal.aborted || closed) throw new Error("Extension UI is disposed.");
  }

  function track(scope, disposable) {
    ensure(scope);
    scope.subscriptions.push(disposable);
    return disposable;
  }

  async function release(scope) {
    if (scope.controller.signal.aborted) return;
    scope.controller.abort();
    for (const [id, waiter] of pending) {
      if (waiter.extensionId === scope.id) settle(id, new Error("Extension UI is disposed."));
    }
    scope.listeners.clear();
    scope.commands.clear();
    const cleanup = Promise.all(scope.subscriptions.splice(0).reverse().map(async item => {
      try { await (typeof item === "function" ? item() : item.dispose()); }
      catch (error) { log(`[extension ${scope.id}] cleanup`, error); }
    }));
    let timer;
    try {
      await Promise.race([cleanup, new Promise(resolve => { timer = setTimeout(resolve, 3000); })]);
    } finally { clearTimeout(timer); }
  }

  function apiFor(scope) {
    const surface = (location, options) => {
      ensure(scope);
      return track(scope, createSurface(scope.id, location, options));
    };
    const call = (type, fields) => {
      ensure(scope);
      return request(type, scope.id, fields);
    };
    return Object.freeze({
      id: scope.id, apiVersion: 1, signal: scope.controller.signal, subscriptions: scope.subscriptions,
      host: Object.freeze({
        invoke: (method, args = null) => call("extensions:invoke", { method, args }),
        on(event, listener) {
          ensure(scope);
          if (typeof event !== "string" || !event || typeof listener !== "function")
            throw new TypeError("An event name and listener are required.");
          const listeners = scope.listeners.get(event) ?? new Set();
          scope.listeners.set(event, listeners);
          listeners.add(listener);
          return track(scope, { dispose: () => listeners.delete(listener) });
        }
      }),
      storage: Object.freeze({
        get: key => call("extensions:storage", { method: "get", args: { key } }),
        set: (key, value) => call("extensions:storage", { method: "set", args: { key, value } }),
        delete: key => call("extensions:storage", { method: "set", args: { key, value: null } })
      }),
      ui: Object.freeze({
        createSidebarPanel: options => surface("sidebar", options),
        createSettingsPanel: options => surface("settings", options),
        showNotification: message => { ensure(scope); notify(scope.id, String(message)); }
      }),
      commands: Object.freeze({
        register(id, title, handler) {
          ensure(scope);
          if (typeof id !== "string" || !id || typeof title !== "string" || !title
            || typeof handler !== "function" || scope.commands.has(id))
            throw new Error("Commands require a unique id, a title and a handler.");
          scope.commands.set(id, { id, title, handler });
          changed();
          return track(scope, { dispose() { scope.commands.delete(id); changed(); } });
        },
        execute: (id, ...args) => execute(scope.id, id, ...args)
      })
    });
  }

  async function activate(item) {
    if (closed || !item.active || !item.uiUrl || scopes.has(item.id)) return;
    const scope = { id: item.id, controller: new AbortController(), subscriptions: [],
      listeners: new Map(), commands: new Map() };
    scopes.set(item.id, scope);
    let timer;
    const activation = (async () => {
      const module = await loadModule(item.uiUrl);
      ensure(scope);
      if (typeof module.activate !== "function") throw new Error("UI module must export activate(api).");
      const cleanup = await module.activate(apiFor(scope));
      if (cleanup) {
        if (typeof cleanup !== "function" && typeof cleanup.dispose !== "function")
          throw new TypeError("activate() must return a function, a disposable, or nothing.");
        if (scope.controller.signal.aborted) await (typeof cleanup === "function" ? cleanup() : cleanup.dispose());
        else track(scope, cleanup);
      }
    })();
    try {
      await Promise.race([activation, new Promise((_, reject) => {
        timer = setTimeout(() => reject(new Error("Extension UI activation timed out.")), activationTimeoutMs);
      })]);
    } catch (error) {
      report(item.id, error);
      await release(scope);
    } finally { clearTimeout(timer); }
  }

  async function sync(items) {
    if (closed) return;
    if (!Array.isArray(items)) throw new TypeError("Invalid extension catalog.");
    catalog = items.map(item => ({ ...item, uiError: catalog.find(old => old.id === item.id)?.uiError }));
    changed();
    await Promise.all(catalog.map(activate));
    changed();
  }

  async function execute(extensionId, commandId, ...args) {
    const scope = scopes.get(extensionId);
    if (!scope) throw new Error("Extension UI is not loaded.");
    ensure(scope);
    const command = scope.commands.get(commandId);
    if (!command) throw new Error("Unknown extension command.");
    return command.handler(...args);
  }

  return {
    get catalog() { return catalog.map(item => ({ ...item })); },
    commands(id) { return Array.from(scopes.get(id)?.commands.values() ?? [], ({ id, title }) => ({ id, title })); },
    execute,
    async refresh() { await sync(await request("extensions:list")); },
    async setEnabled(extensionId, enabled) {
      await sync(await request("extensions:set-enabled", extensionId, { enabled }));
    },
    openFolder: () => request("extensions:open-folder"),
    receive(message) {
      if (closed) return false;
      if (message?.type === "extensions:result") {
        const waiter = pending.get(message.requestId);
        if (waiter && waiter.extensionId === (message.extensionId ?? null))
          settle(message.requestId, message.error, message.result);
        return true;
      }
      if (message?.type === "extensions:event") {
        const scope = scopes.get(message.extensionId);
        if (!scope || scope.controller.signal.aborted) return true;
        for (const listener of Array.from(scope.listeners.get(message.event) ?? [])) {
          try { Promise.resolve(listener(message.data)).catch(error => report(scope.id, error)); }
          catch (error) { report(scope.id, error); }
        }
        return true;
      }
      return false;
    },
    async dispose() {
      if (closed) return;
      closed = true;
      for (const id of pending.keys()) settle(id, new Error("Extension runtime is closed."));
      await Promise.all(Array.from(scopes.values(), release));
      scopes.clear();
    }
  };
}
