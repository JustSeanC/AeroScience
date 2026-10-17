import { ResearchMap } from "./map.js";
const $ = (id) => document.getElementById(id);
const n = (v, d = 2) =>
  Number.isFinite(v)
    ? v.toLocaleString(undefined, { maximumFractionDigits: d })
    : "Unavailable";
const text = (tag, value, cls) => {
  const e = document.createElement(tag);
  e.textContent = value;
  if (cls) e.className = cls;
  return e;
};
const button = (label, action) => {
  const b = text("button", label, "secondary");
  b.type = "button";
  b.onclick = async () => {
    b.disabled = true;
    try {
      await action();
    } catch (e) {
      ctx.notice(e.message);
    } finally {
      b.disabled = false;
    }
  };
  return b;
};
let ctx,
  definitions = [],
  templates = [],
  plans = [],
  planOffset = 0,
  editing = null,
  state = null,
  track = [],
  markers = [],
  scienceRows = [],
  liveId = null,
  cursor = 0,
  trackCursor = 0,
  busy = false,
  lastFetch = 0,
  planMap,
  liveMap,
  waypoints = [],
  areas = [];
function input(parent, label, value = "", type = "text") {
  const wrap = text("label", label),
    e = document.createElement(type === "textarea" ? "textarea" : "input");
  if (type !== "textarea") e.type = type;
  e.value = value ?? "";
  wrap.append(e);
  parent.append(wrap);
  return e;
}
function select(parent, label, options, value) {
  const wrap = text("label", label),
    e = document.createElement("select");
  for (const [id, name] of options) {
    const o = text("option", name);
    o.value = id;
    e.append(o);
  }
  e.value = value;
  e.setAttribute("aria-label", label);
  wrap.append(e);
  parent.append(wrap);
  return e;
}
function configs(parent, selected = []) {
  parent.replaceChildren();
  for (const d of definitions) {
    const row = text("div", undefined, "instrument-config"),
      enabled = document.createElement("input");
    enabled.type = "checkbox";
    enabled.checked = !!selected.find((c) => c.instrumentId === d.id)?.enabled;
    enabled.dataset.instrument = d.id;
    const label = text("label", d.name);
    label.prepend(enabled);
    const interval = input(
      row,
      "Interval (seconds)",
      (selected.find((c) => c.instrumentId === d.id)?.intervalMs ?? 1000) /
        1000,
      "number",
    );
    interval.min = 1;
    interval.max = 60;
    interval.step = 1;
    interval.dataset.interval = d.id;
    row.prepend(label);
    parent.append(row);
  }
}
function readConfigs(parent) {
  return definitions.map((d) => ({
    instrumentId: d.id,
    enabled: parent.querySelector(`[data-instrument="${d.id}"]`).checked,
    intervalMs:
      Number(parent.querySelector(`[data-interval="${d.id}"]`).value) * 1000,
  }));
}
export function initializeResearch(context) {
  ctx = context;
  const control = $("control"),
    guidance = text("article", undefined, "panel research-panel");
  guidance.id = "flight-guidance";
  control.append(guidance);
  const mapPanel = text("article", undefined, "panel research-panel");
  mapPanel.append(text("h2", "Mission map"));
  liveMap = new ResearchMap(mapPanel);
  control.append(mapPanel);
  const markerPanel = text("article", undefined, "panel research-panel");
  markerPanel.append(text("h2", "Observation markers & in-flight notes"));
  const category = select(
    markerPanel,
    "Category",
    [
      "observation",
      "start-survey",
      "end-survey",
      "interesting-weather",
      "finding",
      "equipment-note",
      "note",
    ].map((v) => [v, v]),
    "observation",
  );
  category.id = "marker-category";
  const note = input(markerPanel, "Notes", "", "textarea");
  note.id = "marker-note";
  note.maxLength = 4000;
  markerPanel.append(
    button("Add observation marker", async () => {
      if (!state?.activeMission) throw Error("Start a mission first.");
      await ctx.api(`/api/missions/${state.activeMission.id}/markers`, {
        category: category.value,
        notes: note.value,
      });
      note.value = "";
      lastFetch = 0;
    }),
    button("Save in-flight note", async () => {
      if (!state?.activeMission) throw Error("Start a mission first.");
      await ctx.api(`/api/missions/${state.activeMission.id}/notes`, {
        phase: "in-flight",
        text: note.value,
      });
      note.value = "";
    }),
  );
  control.append(markerPanel);
  $("instruments").append(text("div", undefined, "research-panel"));
  $("instruments").lastChild.id = "instrument-content";
}
export async function loadResearch(page) {
  if (!definitions.length) {
    [definitions, templates] = await Promise.all([
      ctx.api("/api/instruments"),
      ctx.api("/api/templates"),
    ]);
  }
  if (page === "planner") await planner();
  if (page === "instruments") await instruments();
}
async function planner() {
  plans = await ctx.api(`/api/plans?limit=50&offset=${planOffset}`);
  const p = $("planner-content");
  p.replaceChildren(text("h2", "Saved mission plans"));
  for (const plan of plans) {
    const row = text("div", undefined, "plan-row");
    row.append(
      text("strong", plan.name),
      text("span", `${plan.template} · ${plan.mode}`),
      button("Edit", () => editPlan(plan)),
      button("Record this plan", async () => {
        const estimate = await ctx.api("/api/science/estimate", {
          instruments: plan.instruments,
          durationMinutes: plan.estimatedDurationMinutes,
        });
        if (
          estimate.capacityWarning &&
          !confirm(
            "Estimated science data plus safety reserve exceed free capacity. Review storage or continue with this estimate?",
          )
        )
          return;
        await ctx.api("/api/missions", {
          name: plan.name,
          mode: plan.mode,
          planId: plan.id,
        });
        location.hash = "#control";
      }),
      button("Delete plan", async () => {
        if (
          confirm(
            "Delete this saved plan? Recording snapshots remain unchanged.",
          )
        ) {
          await ctx.api(`/api/plans/${plan.id}/delete`, {});
          await planner();
        }
      }),
    );
    p.append(row);
  }
  p.append(
    button("New mission plan", () => editPlan(null)),
    button("Newer plans", async () => {
      planOffset = Math.max(0, planOffset - 50);
      await planner();
    }),
    text("span", `Plan page ${planOffset / 50 + 1}`),
    button("Older plans", async () => {
      if (plans.length === 50) {
        planOffset += 50;
        await planner();
      }
    }),
  );
  const editor = text("div", undefined, "panel research-panel");
  editor.id = "plan-editor";
  p.append(editor);
  editPlan(editing);
}
function editPlan(plan) {
  editing = plan;
  waypoints = structuredClone(plan?.waypoints || []);
  areas = structuredClone(plan?.surveyAreas || []);
  const e = $("plan-editor");
  e.replaceChildren(
    text("h2", plan ? "Edit mission plan" : "New mission plan"),
  );
  const form = document.createElement("form");
  form.id = "plan-form";
  e.append(form);
  const grid = text("div", undefined, "research-grid");
  form.append(grid);
  const name = input(grid, "Plan name", plan?.name);
  name.required = true;
  name.maxLength = 100;
  name.id = "plan-name";
  const template = select(
    grid,
    "Template",
    templates.map((t) => [t.id, t.name]),
    plan?.template || "generalResearch",
  );
  const mode = select(
    grid,
    "Science mode",
    [
      ["universal", "Universal — Fly Anything"],
      ["realistic", "Realistic — Authentic Research"],
    ],
    plan?.mode || "universal",
  );
  const objective = input(grid, "Objective", plan?.objective),
    description = input(grid, "Description", plan?.description, "textarea"),
    aircraft = input(
      grid,
      "Intended aircraft (descriptive only)",
      plan?.intendedAircraft,
    ),
    departure = input(grid, "Departure", plan?.departure),
    destination = input(grid, "Destination", plan?.destination);
  const low = input(
      grid,
      "Minimum planned altitude (ft MSL)",
      plan?.minimumAltitudeFeet,
      "number",
    ),
    high = input(
      grid,
      "Maximum planned altitude (ft MSL)",
      plan?.maximumAltitudeFeet,
      "number",
    ),
    duration = input(
      grid,
      "Estimated duration (minutes)",
      plan?.estimatedDurationMinutes,
      "number",
    );
  duration.min = 1;
  duration.max = 43200;
  const notes = input(
    form,
    "Preflight notes",
    plan?.preflightNotes,
    "textarea",
  );
  notes.maxLength = 4000;
  form.append(
    text(
      "p",
      "Realistic Mode has no aircraft whitelist. Advanced instrument limits and calibration procedures are reserved for a future release.",
      "footnote",
    ),
  );
  const configuration = text("div");
  form.append(configuration);
  configs(configuration, plan?.instruments || []);
  template.onchange = () => {
    const t = templates.find((t) => t.id === template.value);
    objective.value = t.objective;
    configs(configuration, t.instruments);
  };
  const estimate = text("p", undefined, "footnote");
  form.append(
    button("Estimate scientific storage", async () => {
      const v = await ctx.api("/api/science/estimate", {
        instruments: readConfigs(configuration),
        durationMinutes: duration.value ? Number(duration.value) : null,
      });
      estimate.textContent = `Estimated science storage: ${v.estimatedBytes === null ? "Supply duration" : n(v.estimatedBytes / 1048576) + " MiB"} · Available: ${v.availableBytes === null ? "Unknown" : n(v.availableBytes / 1073741824) + " GiB"}${v.capacityWarning ? " · CAPACITY WARNING" : ""}. ${v.basis} ${v.limitations.join(" ")}`;
    }),
    estimate,
  );
  form.append(text("h3", "Waypoints & survey areas"));
  const list = text("div");
  form.append(list);
  const drawList = () => {
    list.replaceChildren();
    waypoints.forEach((w, i) => {
      const row = text("div", undefined, "waypoint-row");
      row.append(
        text(
          "strong",
          `${i + 1}. ${w.name} · ${n(w.latitude, 6)}, ${n(w.longitude, 6)}`,
        ),
        button("Edit", () => waypointEditor(i)),
        button("↑", () => {
          if (i) {
            [waypoints[i - 1], waypoints[i]] = [waypoints[i], waypoints[i - 1]];
            drawList();
          }
        }),
        button("↓", () => {
          if (i < waypoints.length - 1) {
            [waypoints[i + 1], waypoints[i]] = [waypoints[i], waypoints[i + 1]];
            drawList();
          }
        }),
        button("Remove", () => {
          waypoints.splice(i, 1);
          drawList();
        }),
      );
      list.append(row);
    });
    for (let i = 0; i < areas.length; i++) {
      const area = areas[i];
      const row = text("div");
      row.append(
        text("span", `${area.name}: ${area.vertices.length} vertices`),
        button("Edit area", () => areaEditor(i)),
        button("Remove area", () => {
          areas.splice(i, 1);
          drawList();
        }),
      );
      list.append(row);
    }
    planMap?.set({ plan: { waypoints, surveyAreas: areas } });
  };
  const editor = text("div", undefined, "research-grid");
  form.append(editor);
  function waypointEditor(index, position = {}) {
    editor.replaceChildren();
    const w = waypoints[index] || position;
    const title = input(
        editor,
        "Waypoint name",
        w.name || `Point ${waypoints.length + 1}`,
      ),
      lat = input(editor, "Latitude (degrees north)", w.latitude, "number"),
      lon = input(editor, "Longitude (degrees east)", w.longitude, "number"),
      alt = input(
        editor,
        "Target altitude (ft MSL, optional)",
        w.targetAltitudeFeet,
        "number",
      ),
      instruction = input(editor, "Instructions", w.instructions),
      purpose = input(editor, "Waypoint objective", w.objective);
    lat.step = lon.step = "any";
    editor.append(
      button("Apply waypoint", () => {
        const value = {
          name: title.value,
          latitude: Number(lat.value),
          longitude: Number(lon.value),
          targetAltitudeFeet: alt.value ? Number(alt.value) : null,
          instructions: instruction.value,
          objective: purpose.value,
        };
        if (
          !lat.value ||
          !lon.value ||
          Math.abs(value.latitude) > 90 ||
          Math.abs(value.longitude) > 180
        )
          throw Error("Enter valid latitude and longitude.");
        if (index >= 0) waypoints[index] = value;
        else waypoints.push(value);
        editor.replaceChildren();
        drawList();
      }),
    );
  }
  function areaEditor(index = -1) {
    editor.replaceChildren();
    const area = areas[index];
    const title = input(editor, "Area name", area?.name),
      vertices = input(
        editor,
        "Vertices, one latitude,longitude per line",
        (area?.vertices || [])
          .map((p) => `${p.latitude},${p.longitude}`)
          .join("\n"),
        "textarea",
      );
    editor.append(
      button("Apply survey area", () => {
        const points = vertices.value
          .trim()
          .split(/\n/)
          .map((line) => {
            const [latitude, longitude] = line.split(",").map(Number);
            return { latitude, longitude };
          });
        if (
          points.length < 3 ||
          points.some(
            (p) =>
              !Number.isFinite(p.latitude) ||
              !Number.isFinite(p.longitude) ||
              Math.abs(p.latitude) > 90 ||
              Math.abs(p.longitude) > 180,
          )
        )
          throw Error("A polygon needs at least three valid coordinate pairs.");
        const value = { name: title.value, vertices: points };
        if (index >= 0) areas[index] = value;
        else areas.push(value);
        editor.replaceChildren();
        drawList();
      }),
    );
  }
  form.append(
    button("Add waypoint", () => waypointEditor(-1)),
    button("Add survey polygon", () => areaEditor()),
  );
  const map = text("div");
  form.append(map);
  planMap = new ResearchMap(map, (p) => waypointEditor(-1, p));
  drawList();
  const save = text("button", "Save mission plan", "primary");
  save.type = "submit";
  form.append(save);
  form.onsubmit = (event) => {
    event.preventDefault();
    ctx.command(save, async () => {
      const nullable = (e) => (e.value ? Number(e.value) : null);
      editing = await ctx.api("/api/plans", {
        id: plan?.id || crypto.randomUUID(),
        name: name.value,
        template: template.value,
        mode: mode.value,
        description: description.value,
        objective: objective.value,
        intendedAircraft: aircraft.value,
        departure: departure.value,
        destination: destination.value,
        minimumAltitudeFeet: nullable(low),
        maximumAltitudeFeet: nullable(high),
        estimatedDurationMinutes: nullable(duration),
        preflightNotes: notes.value,
        instruments: readConfigs(configuration),
        waypoints,
        surveyAreas: areas,
      });
      await planner();
    });
  };
}
async function instruments() {
  const p = $("instrument-content");
  p.replaceChildren(
    text(
      "p",
      "Actual MSFS atmospheric values at the user aircraft. Simulator-reported, not independent calibrated real-world measurements. Acquisition requires a recording mission. Source currently requested at 1 Hz.",
    ),
  );
  const sourceLabel = text(
    "p",
    state?.connection?.source === "simulated"
      ? "SIMULATED TEST FIXTURE — no simulator connection. Development tests only."
      : "Source: MSFS 2024 standard SimConnect atmosphere",
    "source-notice",
  );
  p.append(sourceLabel);
  const configuration = text("div");
  configuration.id = "live-instrument-config";
  p.append(configuration);
  configs(configuration, state?.research?.instruments || []);
  p.append(
    button("Apply instrument configuration", async () => {
      await ctx.api("/api/instruments", readConfigs(configuration));
    }),
  );
  const values = text("div");
  values.id = "instrument-values";
  p.append(values);
  const chart = text("div");
  chart.id = "science-charts";
  p.append(chart);
  p.append(text("h2", "Saved scientific datasets"));
  const datasetList = text("div");
  p.append(datasetList);
  let offset = 0;
  async function datasets() {
    const data = await ctx.api(
      `/api/science/datasets?limit=20&offset=${offset}`,
    );
    datasetList.replaceChildren();
    if (!data.available) datasetList.append(text("p", data.error));
    for (const d of data.datasets) {
      const row = text("article", undefined, "panel research-panel");
      row.append(
        text("h3", `${d.missionName} · ${d.instrumentId}`),
        text("p", `${d.observationCount} observations · ${d.createdUtc}`),
        button("View dataset observations", async () => {
          const result = await ctx.api(
            `/api/science/datasets/${d.id}/observations?limit=1000`,
          );
          const holder = text("div");
          row.append(holder);
          showScience(
            holder,
            result.observations,
            "First 1,000 observations; continue through pages for all saved values.",
          );
          let after = result.next;
          const more = button("Next observation page", async () => {
            const next = await ctx.api(
              `/api/science/datasets/${d.id}/observations?after=${after}&limit=1000`,
            );
            after = next.next;
            showScience(
              holder,
              next.observations,
              "Next page · full saved values",
            );
          });
          row.append(more);
        }),
      );
      datasetList.append(row);
    }
  }
  p.append(
    button("Previous dataset page", async () => {
      offset = Math.max(0, offset - 20);
      await datasets();
    }),
    button("Next dataset page", async () => {
      offset += 20;
      await datasets();
    }),
  );
  await datasets();
  renderResearch(state);
}
function guidance(parent, g) {
  parent.replaceChildren(text("h2", "Flight Guidance · read only"));
  if (!g || g.quality !== "valid") {
    parent.append(text("p", `Unavailable${g ? " · " + g.quality : ""}`));
    return;
  }
  parent.append(text("p", `Autopilot: ${g.autopilot}`));
  const grid = text("div", undefined, "research-grid");
  for (const [label, m] of [
    ["Lateral", g.lateral],
    ["Vertical", g.vertical],
    ["Speed", g.speed],
  ])
    grid.append(
      text(
        "div",
        `${label}: ${m.mode || "Unavailable"}${Number.isFinite(m.target) ? " · " + n(m.target, 3) + " " + m.unit : ""}${m.armed ? " · Armed: " + m.armed : ""}${m.status ? " · " + m.status : ""}`,
      ),
    );
  parent.append(grid, text("p", `${g.timestampUtc} · ${g.source}`, "footnote"));
}
export function renderResearch(next) {
  if (!next) return;
  state = next;
  guidance($("flight-guidance"), next.research?.guidance);
  const p = $("instrument-values");
  if (p) {
    p.replaceChildren();
    for (const i of next.research?.instruments || []) {
      const card = text("article", undefined, "panel research-panel");
      card.append(
        text(
          "h3",
          definitions.find((d) => d.id === i.instrumentId)?.name ||
            i.instrumentId,
        ),
        text(
          "p",
          `${i.enabled ? "Enabled" : "Disabled"} · ${i.quality} · Configured ${i.intervalMs / 1000}s · Actual interval ${n(i.actualAverageIntervalSeconds)}s · Last successful ${i.lastSuccessfulUtc || "none"} · ${i.observationCount} saved · ${i.rejectedObservations} rejected · ${i.missingIntervals} missing intervals`,
        ),
      );
      for (const v of i.measurements)
        card.append(
          text(
            "p",
            `${v.measurementId}: ${i.quality === "valid" ? n(v.value, 5) : "Unavailable"} ${v.unit} · ${v.provenance} · ${v.quality}`,
          ),
        );
      if (i.message) card.append(text("p", i.message));
      p.append(card);
    }
  }
  if (liveId !== next.activeMission?.id) {
    liveId = next.activeMission?.id;
    track = [];
    markers = [];
    scienceRows = [];
    cursor = 0;
    trackCursor = 0;
    livePlan = null;
    lastFetch = 0;
  }
  const t = next.telemetry,
    fresh =
      t &&
      next.connection.state === "connected" &&
      Date.now() - Date.parse(t.timestampUtc) < 6000;
  liveMap?.set({
    track,
    markers,
    samples: scienceRows
      .filter((v) => v.measurementId === "temperature")
      .slice(-1000),
    plan: livePlan || {},
    aircraft: fresh ? t : null,
  });
  if (liveId && !busy && Date.now() - lastFetch > 4000) {
    busy = true;
    lastFetch = Date.now();
    refreshLive(liveId)
      .catch((e) => ctx.notice(e.message))
      .finally(() => (busy = false));
  }
}
let livePlan = null;
async function refreshLive(id) {
  const [points, marks, research, science] = await Promise.all([
    ctx.api(
      `/api/missions/${id}/track?limit=1500${trackCursor ? `&after=${trackCursor}` : ""}`,
    ),
    ctx.api(`/api/missions/${id}/markers?limit=200`),
    ctx.api(`/api/missions/${id}/research`),
    ctx.api(`/api/missions/${id}/science?after=${cursor}&limit=1000`),
  ]);
  if (liveId !== id) return;
  track = trackCursor ? [...track, ...points.points] : points.points;
  if (track.length > 3000)
    track = track.filter((_, i) => i % 2 === 0 || i === track.length - 1);
  trackCursor = points.nextSequence;
  markers = marks;
  livePlan = research.planSnapshot;
  scienceRows = [...scienceRows, ...science.observations].slice(-3000);
  cursor = science.next;
  liveMap?.set({
    track,
    markers,
    samples: scienceRows
      .filter((v) => v.measurementId === "temperature")
      .slice(-1000),
    plan: livePlan || {},
    aircraft: state.telemetry,
  });
  if ($("science-charts")) {
    $("science-charts").replaceChildren();
    scienceCharts($("science-charts"), scienceRows);
  }
}
function scienceCharts(parent, rows) {
  const specs = [
    [
      "temperature",
      "altitudeMslFeet",
      "Temperature vs altitude",
      "degC",
      "ft MSL",
    ],
    ["pressure", "altitudeMslFeet", "Pressure vs altitude", "inHg", "ft MSL"],
    ["wind-speed", "altitudeMslFeet", "Wind speed vs altitude", "kt", "ft MSL"],
    ["temperature", "timestampUtc", "Temperature over time", "degC", "UTC"],
  ];
  for (const [id, xkey, title, unit, xunit] of specs) {
    const card = text("article", undefined, "panel research-panel");
    card.append(text("h3", title));
    const points = rows
      .filter((r) => r.measurementId === id && r[xkey] != null)
      .map((r) => ({
        x: xkey === "timestampUtc" ? Date.parse(r[xkey]) : r[xkey],
        y: r.value,
        t: r.timestampUtc,
      }))
      .filter((p) => Number.isFinite(p.x) && Number.isFinite(p.y));
    if (!points.length) {
      card.append(
        text("p", "No valid saved observations in this display page."),
      );
      parent.append(card);
      continue;
    }
    const minX = Math.min(...points.map((p) => p.x)),
      maxX = Math.max(...points.map((p) => p.x)),
      minY = Math.min(...points.map((p) => p.y)),
      maxY = Math.max(...points.map((p) => p.y));
    const ns = "http://www.w3.org/2000/svg",
      svg = document.createElementNS(ns, "svg");
    svg.setAttribute("viewBox", "0 0 600 180");
    svg.setAttribute("role", "img");
    svg.setAttribute("aria-label", title);
    for (const p of points) {
      const c = document.createElementNS(ns, "circle");
      c.setAttribute("cx", 20 + ((p.x - minX) / (maxX - minX || 1)) * 560);
      c.setAttribute("cy", 160 - ((p.y - minY) / (maxY - minY || 1)) * 140);
      c.setAttribute("r", 2);
      c.setAttribute("fill", "#50dcc7");
      const tip = document.createElementNS(ns, "title");
      tip.textContent = `${p.t}: ${p.y} ${unit}`;
      c.append(tip);
      svg.append(c);
    }
    card.append(
      svg,
      text(
        "p",
        `Y ${n(minY, 4)} – ${n(maxY, 4)} ${unit} · X ${xkey === "timestampUtc" ? new Date(minX).toISOString() + " – " + new Date(maxX).toISOString() : n(minX) + " – " + n(maxX)} ${xunit} · ${points.length} actual observations in this bounded display. No interpolation.`,
        "footnote",
      ),
    );
    parent.append(card);
  }
}
function showScience(parent, rows, label) {
  parent.replaceChildren(text("p", label));
  scienceCharts(parent, rows);
  const list = text("div", undefined, "observation-table");
  for (const r of rows)
    list.append(
      text(
        "p",
        `${r.timestampUtc} · ${r.measurementId} ${n(r.value, 8)} ${r.unit} · ${r.quality} · ${r.provenance}`,
      ),
    );
  parent.append(list);
}
export async function researchDetail(body, id, mission) {
  const review = await ctx.api(`/api/missions/${id}/review`);
  body.append(
    text("h3", "Research review"),
    text("p", "Recording source: " + (mission.source || "Unavailable")),
    text(
      "p",
      `${review.research.template} · ${review.research.objective || "No objective supplied"}`,
    ),
  );
  const f = review.flight;
  body.append(
    text(
      "p",
      `Mach ${n(f.minimumMach, 3)} – ${n(f.maximumMach, 3)} · Mean ${n(f.meanMach, 3)} · ${f.machSamples} values · IAS ${n(f.minimumIasKnots)} – ${n(f.maximumIasKnots)} kt · Actual average interval ${n(f.actualAverageIntervalSeconds, 3)}s · ${f.gapEvents} recorded gap events`,
    ),
  );
  if (review.research.planSnapshot)
    body.append(
      text(
        "p",
        `Plan snapshot: ${review.research.planSnapshot.name} · Preflight notes: ${review.research.planSnapshot.preflightNotes || "None"}`,
      ),
    );
  for (const c of review.research.instruments)
    body.append(
      text(
        "p",
        `${c.instrumentId}: ${c.enabled ? "enabled" : "disabled"} · ${c.intervalMs / 1000}s`,
      ),
    );
  for (const d of review.research.diagnostics || [])
    body.append(
      text(
        "p",
        `${d.instrumentId}: ${d.observationCount} saved, ${d.rejectedObservations} rejected, ${d.missingIntervals} missing intervals, actual interval ${n(d.actualAverageIntervalSeconds)}s`,
      ),
    );
  for (const d of review.datasets)
    body.append(
      text(
        "p",
        `Dataset ${d.datasetId}: ${d.available ? "Available" : "Unavailable / cleared"} · Library ${d.libraryId}`,
      ),
    );
  if (!review.scienceAvailable)
    body.append(
      text(
        "p",
        `Science Library unavailable: ${review.scienceError}. Flight history remains accessible.`,
      ),
    );
  for (const s of review.measurements)
    body.append(
      text(
        "p",
        `${s.measurementId}: n=${s.count}, min ${n(s.minimum, 6)}, max ${n(s.maximum, 6)}, mean ${n(s.mean, 6)} ${s.unit}`,
      ),
    );
  const [path, marks] = await Promise.all([
    ctx.api(`/api/missions/${id}/track?limit=1500`),
    ctx.api(`/api/missions/${id}/markers?limit=200`),
  ]);
  const map = text("div");
  body.append(map);
  const mapped = new ResearchMap(map);
  mapped.set({
    track: path.points,
    markers: marks,
    plan: review.research.planSnapshot || {},
  });
  body.append(
    text(
      "p",
      `Map: ${path.points.length} display points from ${path.totalSamples} saved samples${path.downsampled ? " (display reduced)" : ""}. Full coordinates remain in exports.`,
      "footnote",
    ),
  );
  let markerOffset = 0;
  const markerList = text("div");
  body.append(markerList);
  async function markerPage() {
    const list = await ctx.api(
      `/api/missions/${id}/markers?limit=200&offset=${markerOffset}`,
    );
    markerList.replaceChildren(
      ...list.map((m) =>
        text("p", `${m.timestampUtc} · ${m.category} · ${m.notes || ""}`),
      ),
    );
  }
  await markerPage();
  body.append(
    button("Newer markers", async () => {
      markerOffset = Math.max(0, markerOffset - 200);
      await markerPage();
    }),
    button("Older markers", async () => {
      markerOffset += 200;
      await markerPage();
    }),
  );
  const notes = text("div");
  body.append(notes);
  let offset = 0;
  async function loadNotes() {
    const rows = await ctx.api(
      `/api/missions/${id}/notes?limit=100&offset=${offset}`,
    );
    notes.replaceChildren(
      ...rows.map((v) => text("p", `${v.timestampUtc} [${v.phase}] ${v.text}`)),
    );
  }
  await loadNotes();
  body.append(
    button("Newer notes", async () => {
      offset = Math.max(0, offset - 100);
      await loadNotes();
    }),
    button("Older notes", async () => {
      offset += 100;
      await loadNotes();
    }),
  );
  const note = input(body, "Postflight note", "", "textarea");
  body.append(
    button("Save postflight note", async () => {
      await ctx.api(`/api/missions/${id}/notes`, {
        text: note.value,
        phase: "postflight",
      });
      note.value = "";
      await loadNotes();
    }),
  );
  if (review.scienceAvailable) {
    const holder = text("div");
    body.append(holder);
    let after = 0;
    const first = await ctx.api(`/api/missions/${id}/science?limit=1000`);
    after = first.next;
    showScience(
      holder,
      first.observations,
      "First 1,000 observations. Complete exports include every saved row.",
    );
    mapped.set({
      track: path.points,
      markers: marks,
      plan: review.research.planSnapshot || {},
      samples: first.observations.slice(0, 1000),
    });
    body.append(
      button("Next science page", async () => {
        const next = await ctx.api(
          `/api/missions/${id}/science?after=${after}&limit=1000`,
        );
        after = next.next;
        showScience(holder, next.observations, "Next observation page");
      }),
    );
  }
  if (mission.status !== "recording")
    for (const [format, label] of [
      ["flight.csv", "Flight telemetry CSV"],
      ["science.csv", "Scientific observations CSV"],
      ["events.csv", "Events CSV"],
      ["gpx", "Actual track GPX"],
      ["kml", "Planned / actual KML"],
      ["report.txt", "Research summary"],
    ]) {
      const a = text("a", label, "secondary");
      a.href = `/api/missions/${id}/export/${format}`;
      a.download = "";
      body.append(a);
    }
}

export function invalidateResearch() {
  if ($("flight-guidance")) guidance($("flight-guidance"), null);
  if ($("instrument-values"))
    $("instrument-values").replaceChildren(
      text(
        "p",
        "Live measurements unavailable. Recorded science and flight history are preserved.",
      ),
    );
  liveMap?.set({ track, markers, plan: livePlan || {}, aircraft: null });
}
