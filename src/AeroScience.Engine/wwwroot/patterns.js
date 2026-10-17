const element = (tag, text) => {
  const e = document.createElement(tag);
  if (text) e.textContent = text;
  return e;
};
export function patternEditor(parent, { api, notice, get, set, aircraft }) {
  let preview = null,
    patterns = structuredClone(get().patterns || []),
    undo = [];
  const panel = element("details");
  panel.className = "panel research-panel";
  panel.append(element("summary", "Flight pattern generator"));
  const grid = element("div");
  grid.className = "research-grid";
  panel.append(grid);
  parent.append(panel);
  const field = (name, value, type = "number") => {
    const label = element("label", name),
      input = element("input");
    input.type = type;
    input.value = value ?? "";
    if (type === "number") input.step = "any";
    label.append(input);
    grid.append(label);
    return input;
  };
  const label = element("label", "Pattern"),
    kind = element("select");
  for (const value of [
    "lawnmower",
    "creepingLine",
    "expandingSquare",
    "racetrack",
    "orbit",
    "box",
    "radialSpokes",
    "straightTransect",
  ]) {
    const o = element("option", value.replace(/([A-Z])/g, " $1"));
    o.value = value;
    kind.append(o);
  }
  label.append(kind);
  grid.append(label);
  const lat = field("Center/start latitude", null),
    lon = field("Center/start longitude", null),
    heading = field("Heading (degrees true)", 0),
    length = field("Leg length (m)", 10000),
    width = field("Box width (m)", 5000),
    spacing = field("Track spacing / square step (m)", 1000),
    radius = field("Radius (m)", 2500),
    legs = field("Legs / spokes", 6),
    orbits = field("Orbits", 1),
    alt = field("Target altitude (ft MSL, optional)", null),
    notes = field("Pattern notes", "", "text");
  const clockwise = element("input");
  clockwise.type = "checkbox";
  clockwise.checked = true;
  const direction = element("label", "Clockwise");
  direction.prepend(clockwise);
  grid.append(direction);
  const report = element("p"),
    saved = element("div");
  panel.append(report, saved);
  const action = (name, fn) => {
    const b = element("button", name);
    b.type = "button";
    b.className = "secondary";
    b.onclick = async () => {
      try {
        await fn();
      } catch (e) {
        notice(e.message);
      }
    };
    panel.append(b);
    return b;
  };
  function remember() {
    undo.push(structuredClone({ ...get(), patterns }));
    if (undo.length > 20) undo.shift();
  }
  function apply(data) {
    patterns = data.patterns || [];
    set(data);
    renderSaved();
  }
  const definition = () => {
    if (lat.value === "" || lon.value === "")
      throw Error(
        "Choose a geographic center or enter coordinates. An unset location is not 0,0.",
      );
    return {
      id: preview?.definition.id || crypto.randomUUID(),
      kind: kind.value,
      center: { latitude: Number(lat.value), longitude: Number(lon.value) },
      headingDegrees: Number(heading.value),
      lengthMetres: Number(length.value),
      widthMetres: Number(width.value),
      spacingMetres: Number(spacing.value),
      radiusMetres: Number(radius.value),
      legs: Number(legs.value),
      orbits: Number(orbits.value),
      clockwise: clockwise.checked,
      targetAltitudeFeet: alt.value === "" ? null : Number(alt.value),
      notes: notes.value,
    };
  };
  let generation = 0,
    timer;
  async function generate() {
    const id = ++generation;
    const result = await api("/api/patterns/preview", definition());
    if (id !== generation) return result;
    preview = result;
    report.textContent = `Preview: ${result.waypoints.length} editable points · ${(result.distanceMetres / 1000).toFixed(2)} km route distance. ${result.method}`;
    const current = get();
    set({ ...current, preview: result.waypoints });
    return result;
  }
  grid.addEventListener("input", () => {
    clearTimeout(timer);
    timer = setTimeout(
      () => generate().catch((e) => (report.textContent = e.message)),
      250,
    );
  });
  action("Use live aircraft position", () => {
    const p = aircraft();
    if (!Number.isFinite(p?.latitude))
      throw Error(
        "Live aircraft position unavailable. Enter coordinates or search the map.",
      );
    lat.value = p.latitude;
    lon.value = p.longitude;
    return generate();
  });
  action("Use first waypoint", () => {
    const p = get().waypoints[0];
    if (!p) throw Error("Add or select a waypoint first.");
    lat.value = p.latitude;
    lon.value = p.longitude;
    return generate();
  });
  action("Preview pattern", generate);
  action("Insert pattern", async () => {
    clearTimeout(timer);
    const generated = await generate();
    generation++;
    remember();
    apply({
      ...get(),
      waypoints: [...get().waypoints, ...generated.waypoints],
      patterns: [...patterns, generated.definition],
      preview: [],
    });
    preview = null;
  });
  action("Undo geometry change", () => {
    const previous = undo.pop();
    if (previous) apply(previous);
  });
  function renderSaved() {
    saved.replaceChildren();
    for (const d of patterns) {
      const row = element("div");
      row.className = "plan-row";
      row.append(
        element(
          "strong",
          `${d.kind} · ${d.notes || "Editable generated route"}`,
        ),
      );
      for (const [name, fn] of [
        [
          "Edit parameters",
          () => {
            preview = { definition: d };
            kind.value = d.kind;
            lat.value = d.center.latitude;
            lon.value = d.center.longitude;
            heading.value = d.headingDegrees;
            length.value = d.lengthMetres;
            width.value = d.widthMetres;
            spacing.value = d.spacingMetres;
            radius.value = d.radiusMetres;
            legs.value = d.legs;
            orbits.value = d.orbits;
            alt.value = d.targetAltitudeFeet ?? "";
            notes.value = d.notes ?? "";
            clockwise.checked = d.clockwise;
            report.textContent =
              "Editing saved parameters. Regenerate replaces only this pattern’s tagged points; manual edits to those points will be replaced.";
          },
        ],
        [
          "Regenerate",
          async () => {
            const result = await api(
              "/api/patterns/preview",
              preview?.definition.id === d.id ? definition() : d,
            );
            remember();
            const w = get().waypoints;
            const at = w.findIndex((p) => p.patternId === d.id);
            const kept = w.filter((p) => p.patternId !== d.id);
            kept.splice(at < 0 ? kept.length : at, 0, ...result.waypoints);
            apply({
              ...get(),
              waypoints: kept,
              patterns: patterns.map((p) =>
                p.id === d.id ? result.definition : p,
              ),
              preview: [],
            });
          },
        ],
        [
          "Remove pattern",
          () => {
            remember();
            apply({
              ...get(),
              waypoints: get().waypoints.filter((p) => p.patternId !== d.id),
              patterns: patterns.filter((p) => p.id !== d.id),
              preview: [],
            });
          },
        ],
      ]) {
        const b = element("button", name);
        b.type = "button";
        b.className = "secondary";
        b.onclick = () =>
          Promise.resolve()
            .then(fn)
            .catch((e) => notice(e.message));
        row.append(b);
      }
      saved.append(row);
    }
  }
  renderSaved();
  return { getPatterns: () => patterns, remember };
}
