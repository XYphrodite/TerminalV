// Small, local SVG vocabulary. Never interpolate session titles or other host data into markup.
const paths = {
  search: ["M10.5 17a6.5 6.5 0 1 0 0-13 6.5 6.5 0 0 0 0 13Z", "m16 16 4 4"],
  settings: ["M4 7h16M4 17h16", "M8 4v6M16 14v6"],
  sidebar: ["M4 4h16v16H4z", "M9 4v16"],
  terminal: ["m6 7 5 5-5 5", "M13 17h5"],
  plus: ["M12 5v14M5 12h14"],
  close: ["m6 6 12 12M18 6 6 18"],
  more: ["M5 12h.01M12 12h.01M19 12h.01"],
  folder: ["M3 7V5h7l2 3h9v11H3z"],
  hidden: ["m3 3 18 18", "M10.5 5.1 12 5c6 0 10 7 10 7s-1.1 1.9-3.2 3.7M6.2 6.2C3.5 8.1 2 12 2 12s4 7 10 7c1.7 0 3.2-.6 4.5-1.3", "M9.9 9.9a3 3 0 0 0 4.2 4.2"],
  profiles: ["M4 5h16v14H4z", "M4 9h16", "m8 12 2 2-2 2M13 16h3"],
  edit: ["m15 4 5 5-11 11H4v-5z", "m12 7 5 5"],
  linux: ["M8 9V6a4 4 0 0 1 8 0v3l3 8-3 4H8l-3-4z", "m10 10 2 2 2-2M10 6h.01M14 6h.01"],
  right: ["M3 4h18v16H3z", "M12 4v16"],
  down: ["M3 4h18v16H3z", "M3 12h18"],
  detach: ["M9 3H3v18h18v-6", "M14 3h7v7M21 3 11 13"],
  chevron: ["m8 10 4 4 4-4"],
  restore: ["m9 4-5 5 5 5", "M4 9h10a6 6 0 0 1 0 12"],
  check: ["m5 12 4 4L19 6"],
  image: ["M3 3h18v18H3z", "m3 16 6-6 5 5 3-3 4 4", "M16 7h.01"],
  up: ["m6 14 6-6 6 6"],
  arrowDown: ["m6 10 6 6 6-6"]
};

export function icon(name) {
  const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
  for (const [key, value] of Object.entries({ viewBox: "0 0 24 24", fill: "none", stroke: "currentColor",
    "stroke-width": "1.6", "stroke-linecap": "round", "stroke-linejoin": "round", "aria-hidden": "true", focusable: "false" })) {
    svg.setAttribute(key, value);
  }
  svg.classList.add("ui-icon");
  for (const d of paths[name] || paths.terminal) {
    const path = document.createElementNS(svg.namespaceURI, "path");
    path.setAttribute("d", d);
    svg.append(path);
  }
  return svg;
}

export function mountIcons(root) {
  for (const element of root.querySelectorAll("[data-icon]")) element.replaceChildren(icon(element.dataset.icon));
}
