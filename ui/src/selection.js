// Read visible rows before xterm joins soft-wrapped lines. Screen padding
// would otherwise become whitespace in the middle of one clipboard line.
export function getPlainSelection(term) {
  const range = term.getSelectionPosition();
  if (!range) {
    return "";
  }

  const lines = [];
  const buffer = term.buffer.active;
  for (let y = range.start.y; y <= range.end.y; y += 1) {
    // Selection coordinates are zero-based; the end column is exclusive.
    // An end at column zero doesn't include that row.
    if (y === range.end.y && range.end.x === 0 && y !== range.start.y) {
      break;
    }
    const line = buffer.getLine(y);
    if (!line) {
      continue;
    }
    const start = y === range.start.y ? range.start.x : 0;
    const end = y === range.end.y ? range.end.x : term.cols;
    lines.push(line.translateToString(true, start, end).replace(/\u00a0/g, " ").trim());
  }
  return lines.join("\r\n");
}
