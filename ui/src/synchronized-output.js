// Compatibility for xterm 5.5: DEC synchronized output (mode 2026).
// Grok brackets redraws with this mode, including through Windows ConPTY.
// Keep parsing input, but display the completed frame instead of its erasures.
// xterm 6 has native support; remove this addon when migrating the viewport.
// The private render hook is isolated here because 5.5 has no public pause API.
export class SynchronizedOutputAddon {
  activate(term) {
    if ("synchronizedOutputMode" in term.modes) return;
    const core = term._core?.coreService;
    const rendering = term._core?._renderService;
    if (!core || typeof rendering?._renderRows !== "function") return;

    let pending = null;
    const active = () => core.decPrivateModes.synchronizedOutput === true;
    const cancel = () => {
      if (pending) clearTimeout(pending.timer);
      pending = null;
    };
    const finish = () => {
      const redraw = active();
      cancel();
      core.decPrivateModes.synchronizedOutput = false;
      if (redraw) term.refresh(0, term.rows - 1);
    };
    const begin = () => {
      if (active()) return; // Repeated BSU must not extend the safety timeout.
      cancel();
      const frame = { modes: core.decPrivateModes, timer: null };
      frame.modes.synchronizedOutput = true;
      pending = frame;
      frame.timer = setTimeout(() => {
        if (pending !== frame) return;
        // RIS, DECSTR and term.reset() replace the modes object. An old timeout
        // must not end a new frame or refresh a reset terminal unnecessarily.
        if (core.decPrivateModes === frame.modes) finish();
        else cancel();
      }, 1000);
    };

    const guard = original => function (...args) {
      if (active()) return;
      // Native resets can end a frame without an explicit end marker.
      cancel();
      return original.apply(this, args);
    };
    const original = rendering._renderRows;
    const renderRows = guard(original);
    // Gate the actual paint, including animation frames queued before BSU.
    // This works for both DOM and WebGL without touching visibility pausing.
    rendering._renderRows = renderRows;
    // DOM focus/selection callbacks also repaint directly, outside the service.
    // Preserve their state changes while withholding only the actual drawing.
    let restoreRenderer = () => {};
    const watchRenderer = renderer => {
      restoreRenderer();
      restoreRenderer = () => {};
      if (!renderer) return;
      const draw = renderer.renderRows;
      const guarded = guard(draw);
      renderer.renderRows = guarded;
      restoreRenderer = () => {
        if (renderer.renderRows === guarded) renderer.renderRows = draw;
      };
    };
    watchRenderer(rendering._renderer.value);
    const originalSetRenderer = rendering.setRenderer;
    const setRenderer = function (renderer) {
      watchRenderer(renderer);
      return originalSetRenderer.call(this, renderer);
    };
    rendering.setRenderer = setRenderer;
    const subscriptions = [
      term.parser.registerCsiHandler({ prefix: "?", final: "h" }, params => {
        if (params.includes(2026)) begin();
        return false; // Let xterm process other modes in the same sequence.
      }),
      term.parser.registerCsiHandler({ prefix: "?", final: "l" }, params => {
        if (params.includes(2026)) finish();
        return false;
      }),
      term.parser.registerCsiHandler({ prefix: "?", intermediates: "$", final: "p" }, params => {
        if (params.length !== 1 || params[0] !== 2026) return false;
        term.input(`\x1b[?2026;${active() ? 1 : 2}$y`, false);
        return true;
      })
    ];
    this.cleanup = () => {
      const redraw = active();
      cancel();
      delete core.decPrivateModes.synchronizedOutput;
      for (const subscription of subscriptions) subscription.dispose();
      if (rendering._renderRows === renderRows) rendering._renderRows = original;
      if (rendering.setRenderer === setRenderer) rendering.setRenderer = originalSetRenderer;
      restoreRenderer();
      if (redraw && !term._core._isDisposed) term.refresh(0, term.rows - 1);
    };
  }

  dispose() {
    this.cleanup?.();
    this.cleanup = null;
  }
}
