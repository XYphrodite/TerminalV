// Progress state for the update bar. Kept free of DOM so the mapping from a
// host "update" message to bar/text/percent is unit-testable without a browser.

export function downloadPercent(received, total) {
  if (typeof received !== "number" || received < 0) return null;
  if (typeof total !== "number" || total <= 0) return null;
  return Math.min(100, Math.round((100 * received) / total));
}

/**
 * Turn an update message into what the bar should show.
 * bar: "hidden" | "indeterminate" | "percent" | "full"
 */
export function updateProgressView(message, texts) {
  const status = message?.status;
  if (status === "downloading") {
    const percent = downloadPercent(message.received, message.total);
    if (percent === null) {
      return { bar: "indeterminate", percent: 0, text: texts.download };
    }
    return {
      bar: percent >= 100 ? "full" : "percent",
      percent,
      text: texts.downloadPercent.replace("{percent}", String(percent)),
    };
  }
  if (status === "verifying") {
    return { bar: "full", percent: 100, text: texts.verifying };
  }
  return null;
}
