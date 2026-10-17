const ns = "http://www.w3.org/2000/svg";
const node = (tag, attrs = {}, text) => {
  const n = document.createElementNS(ns, tag);
  for (const [k, v] of Object.entries(attrs)) n.setAttribute(k, v);
  if (text !== undefined) n.textContent = text;
  return n;
};
const valid = (p) =>
  Number.isFinite(p?.latitude) && Number.isFinite(p?.longitude);
const delta = (a, b) => ((a - b + 540) % 360) - 180;
// Self-contained WGS84 grid, no tiles, tracking requests or internet dependency. Display reduction never changes saved coordinates.
export class ResearchMap {
  constructor(parent, onPoint) {
    this.svg = node("svg", {
      viewBox: "0 0 800 400",
      role: "img",
      "aria-label":
        "Offline WGS84 mission map; planned route, actual track and markers",
    });
    this.svg.classList.add("research-map");
    this.zoom = 1;
    this.pan = { x: 0, y: 0 };
    this.data = {};
    this.onPoint = onPoint;
    const controls = document.createElement("div");
    controls.className = "map-tools";
    for (const [label, action] of [
      ["Zoom in", () => (this.zoom *= 1.5)],
      ["Zoom out", () => (this.zoom = Math.max(0.5, this.zoom / 1.5))],
      [
        "Fit map",
        () => {
          this.zoom = 1;
          this.pan = { x: 0, y: 0 };
        },
      ],
    ]) {
      const b = document.createElement("button");
      b.textContent = label;
      b.type = "button";
      b.className = "secondary";
      b.onclick = () => {
        action();
        this.draw();
      };
      controls.append(b);
    }
    this.coordinate = document.createElement("p");
    this.coordinate.className = "footnote";
    parent.append(controls, this.svg, this.coordinate);
    let start;
    this.svg.addEventListener("pointerdown", (e) => {
      start = { x: e.clientX, y: e.clientY, pan: { ...this.pan } };
      this.svg.setPointerCapture(e.pointerId);
    });
    this.svg.addEventListener("pointermove", (e) => {
      if (!start) return;
      const r = this.svg.getBoundingClientRect();
      this.pan = {
        x: start.pan.x + ((e.clientX - start.x) * 800) / r.width,
        y: start.pan.y + ((e.clientY - start.y) * 400) / r.height,
      };
      this.draw();
    });
    this.svg.addEventListener("pointerup", (e) => {
      if (
        start &&
        Math.hypot(e.clientX - start.x, e.clientY - start.y) < 4 &&
        this.onPoint &&
        this.projection
      ) {
        const r = this.svg.getBoundingClientRect(),
          p = this.projection;
        const latitude =
          p.lat -
          (((e.clientY - r.top) * 400) / r.height - 200 - this.pan.y) / p.scale;
        const longitude =
          ((p.lon +
            (((e.clientX - r.left) * 800) / r.width - 400 - this.pan.x) /
              (p.scale * p.cos) +
            540) %
            360) -
          180;
        if (Math.abs(latitude) <= 90) this.onPoint({ latitude, longitude });
      }
      start = null;
    });
  }
  set(data) {
    this.data = data;
    this.draw();
  }
  draw() {
    const {
      track = [],
      plan = {},
      markers = [],
      samples = [],
      aircraft = null,
    } = this.data;
    const points = [
      ...track,
      ...(plan.waypoints || []),
      ...(plan.surveyAreas || []).flatMap((a) => a.vertices),
      ...markers,
      ...samples,
      aircraft,
    ].filter(valid);
    const lon = points[0]?.longitude ?? 0,
      lat = points.length
        ? points.reduce((a, p) => a + p.latitude, 0) / points.length
        : 0,
      cos = Math.max(0.05, Math.cos((lat * Math.PI) / 180));
    const width = Math.max(
        0.02,
        ...points.map((p) => Math.abs(delta(p.longitude, lon)) * cos * 2),
      ),
      height = Math.max(
        0.02,
        ...points.map((p) => Math.abs(p.latitude - lat) * 2),
      );
    const scale = Math.min(650 / width, 300 / height) * this.zoom;
    this.projection = { lat, lon, cos, scale };
    const xy = (p) => [
      400 + delta(p.longitude, lon) * cos * scale + this.pan.x,
      200 - (p.latitude - lat) * scale + this.pan.y,
    ];
    this.svg.replaceChildren(
      node("rect", { width: 800, height: 400, fill: "#0c1923" }),
    );
    const grid = Math.pow(
      10,
      Math.floor(Math.log10(Math.max(width, height) / 5)),
    );
    const step = Number.isFinite(grid) ? Math.max(0.001, grid) : 1;
    // Limit line generation under large pans/zooms.
    for (let i = -8; i <= 8; i++) {
      const la = Math.floor(lat / step) * step + i * step,
        y = xy({ latitude: la, longitude: lon })[1];
      this.svg.append(
        node("line", { x1: 0, x2: 800, y1: y, y2: y, stroke: "#25404d" }),
        node(
          "text",
          { x: 8, y: y - 3, fill: "#87a4b5", "font-size": 11 },
          `${la.toFixed(3)}°`,
        ),
      );
      const lo = lon + (i * step) / cos,
        x = xy({ latitude: lat, longitude: lo })[0];
      this.svg.append(
        node("line", { x1: x, x2: x, y1: 0, y2: 400, stroke: "#25404d" }),
      );
    }
    const line = (items, color, dash = "", polygon = false) => {
      const v = items.filter(valid);
      if (v.length)
        this.svg.append(
          node(polygon ? "polygon" : "polyline", {
            points: v.map((p) => xy(p).join(",")).join(" "),
            fill: polygon ? "#8468ba22" : "none",
            stroke: color,
            "stroke-width": 2,
            "stroke-dasharray": dash,
          }),
        );
    };
    for (const area of plan.surveyAreas || [])
      line(area.vertices, "#bf9af0", "5 4", true);
    line(plan.waypoints || [], "#f2c876", "7 5");
    line(track, "#50dcc7");
    const point = (p, color, label, r = 4) => {
      if (!valid(p)) return;
      const [x, y] = xy(p),
        g = node("g");
      g.append(
        node("circle", { cx: x, cy: y, r, fill: color }),
        node(
          "title",
          {},
          `${label} · ${p.latitude}, ${p.longitude}${p.timestampUtc ? " · " + p.timestampUtc : ""}`,
        ),
      );
      this.svg.append(g);
    };
    for (const p of samples) point(p, "#a4bdf3", "Science sample", 2);
    for (const p of plan.waypoints || []) point(p, "#f2c876", p.name);
    for (const p of markers) point(p, "#eb9bbd", p.category || "Marker", 6);
    if (valid(aircraft)) {
      const [x, y] = xy(aircraft);
      this.svg.append(
        node("path", {
          d: "M0 -11 L7 9 L0 5 L-7 9 Z",
          fill: "#fff",
          transform: `translate(${x} ${y}) rotate(${aircraft.headingDegrees || 0})`,
        }),
      );
    }
    this.svg.append(
      node("text", { x: 765, y: 22, fill: "#fff", "font-size": 16 }, "N ↑"),
    );
    this.coordinate.textContent =
      "Offline WGS84 grid · Gold: planned · Turquoise: actual · Pink: markers · Blue: science · North up. Drag to pan. Display points may be reduced; exports retain full precision." +
      (this.onPoint ? " Click map to add a waypoint." : "");
  }
}
