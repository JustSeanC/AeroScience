import { scientificReadiness } from "./scientific-access.js";
import { routeExportButton } from "./route-export.js";
import { remoteProducts } from "./remote-science.js";
import { ResearchMap } from "./map.js";
import { patternEditor } from "./patterns.js";
import {
  scienceCharts,
  initializeUnits,
  unitControls,
  displayReading,
  syncUnits,
} from "./science-display.js";
import { renderAnalysis } from "./analysis.js";
const $ = (id) => document.getElementById(id);
const aircraftFor = (s) =>
  s?.connection?.state === "connected" &&
  s.telemetry &&
  Date.now() - Date.parse(s.telemetry.timestampUtc) >= -2000 &&
  Date.now() - Date.parse(s.telemetry.timestampUtc) < 6000
    ? s.telemetry
    : null;
const samplePositions = (rows) =>
  Array.from(
    new Map(
      rows
        .filter(
          (r) => Number.isFinite(r.latitude) && Number.isFinite(r.longitude),
        )
        .map((r) => [
          `${r.positionTimestampUtc || r.timestampUtc}:${r.latitude}:${r.longitude}`,
          r,
        ]),
    ).values(),
  ).slice(-500);
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
  scienceMap,
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
  mapPanel.id = "mission-map";
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
  scientificReadiness(control, ctx.api).catch(() => {});
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
  await scientificReadiness(p, ctx.api);
  for (const plan of plans) {
    const row = text("div", undefined, "plan-row");
    row.append(
      text("strong", plan.name),
      text("span", `${plan.template} · ${plan.mode}`),
      button("Edit", () => editPlan(plan)),
      button("Prepare scientific data", async () => {
        const result = await ctx.api(`/api/plans/${plan.id}/prepare`, {});
        alert(result.message);
      }),
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
    routeExportButton(row, () => plan, ctx.api);
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
  const flightRules = select(
    grid,
    "Flight plan rules",
    [
      ["VFR", "VFR"],
      ["IFR", "IFR"],
    ],
    plan?.flightRules || "VFR",
  );
  const cruise = input(
    grid,
    "Flight plan cruising altitude (ft MSL; required for PLN)",
    plan?.cruisingAltitudeFeet,
    "number",
  );
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
  const presetSection = text("details");
  presetSection.append(text("summary", "Apply an instrument preset"));
  configuration.after(presetSection);
  ctx
    .api("/api/presets")
    .then((rows) => {
      for (const preset of rows)
        presetSection.append(
          button(preset.name, async () => {
            const resolved = await ctx.api("/api/presets/resolve", preset);
            if (
              resolved.warnings.length &&
              !confirm(
                resolved.warnings.join("\n") +
                  "\nApply supported instruments only?",
              )
            )
              return;
            configs(configuration, resolved.instruments);
          }),
        );
      if (!rows.length)
        presetSection.append(
          text("p", "Save a reusable configuration from Science Collection."),
        );
    })
    .catch((e) => ctx.notice(e.message));
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
        button("Center on map", () => planMap.center(w)),
        button("↑", () => {
          if (i) {
            pattern.remember();
            [waypoints[i - 1], waypoints[i]] = [waypoints[i], waypoints[i - 1]];
            drawList();
          }
        }),
        button("↓", () => {
          if (i < waypoints.length - 1) {
            pattern.remember();
            [waypoints[i + 1], waypoints[i]] = [waypoints[i], waypoints[i + 1]];
            drawList();
          }
        }),
        button("Remove", () => {
          pattern.remember();
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
          pattern.remember();
          areas.splice(i, 1);
          drawList();
        }),
      );
      list.append(row);
    }
    planMap?.set({
      plan: { waypoints, surveyAreas: areas, departure: departure.value },
      aircraft: aircraftFor(state),
    });
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
      purpose = input(editor, "Waypoint objective", w.objective),
      navigationIdentifier = input(
        editor,
        "Published navigation identifier (optional; not the science name)",
        w.navigationIdentifier,
      );
    lat.step = lon.step = "any";
    editor.append(
      button("Apply waypoint", () => {
        const value = {
          ...w,
          id: w.id || crypto.randomUUID(),
          navigationIdentifier: navigationIdentifier.value || null,
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
        pattern.remember();
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
        pattern.remember();
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
  planMap?.observer?.disconnect();
  planMap?.destroy();
  planMap = new ResearchMap(map, (p) => waypointEditor(-1, p), {
    onWaypointsChange: (value) => {
      pattern.remember();
      waypoints = value;
      drawList();
    },
    onAreasChange: (value) => {
      pattern.remember();
      areas = value;
      drawList();
    },
  });
  let patternValues = structuredClone(plan?.patterns || []);
  const pattern = patternEditor(form, {
    api: ctx.api,
    notice: ctx.notice,
    aircraft: () => aircraftFor(state),
    get: () => ({ waypoints, surveyAreas: areas, patterns: patternValues }),
    set: (value) => {
      waypoints = value.waypoints;
      areas = value.surveyAreas;
      patternValues = value.patterns || patternValues;
      drawList();
      if (value.preview?.length) {
        planMap.set({
          plan: {
            waypoints: [...waypoints, ...value.preview],
            surveyAreas: areas,
          },
        });
        planMap.fit(value.preview);
      }
    },
  });
  departure.onchange = drawList;
  drawList();
  const save = text("button", "Save mission plan", "primary");
  save.type = "submit";
  form.append(save);
  routeExportButton(
    form,
    () => ({
      ...plan,
      id: plan?.id || "00000000-0000-0000-0000-000000000000",
      name: name.value,
      template: template.value,
      mode: mode.value,
      description: description.value,
      objective: objective.value,
      departure: departure.value,
      destination: destination.value,
      waypoints,
      surveyAreas: areas,
      patterns: pattern.getPatterns(),
      flightRules: flightRules.value,
      cruisingAltitudeFeet: cruise.value ? Number(cruise.value) : null,
    }),
    ctx.api,
  );
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
        patterns: pattern.getPatterns(),
        flightRules: flightRules.value,
        cruisingAltitudeFeet: nullable(cruise),
      });
      await planner();
    });
  };
}
async function instruments() {
  await initializeUnits(ctx.api);
  const p = $("instrument-content");
  p.replaceChildren(
    text(
      "p",
      "Atmospheric instruments use actual MSFS values at the user aircraft; remote instruments sample real automatically prepared or advanced imported geographic observations with their original source dates. Acquisition requires a recording mission. Flight telemetry is requested at 1 Hz; no independent calibrated or live-camera measurements are claimed.",
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
  await scientificReadiness(p, ctx.api);
  await unitControls(p, ctx.api, () => instruments());
  const collection = text("div");
  collection.id = "collection-status";
  p.append(collection);
  const presets = text("details");
  presets.append(text("summary", "Reusable instrument presets"));
  p.append(presets);
  const presetList = text("div");
  presets.append(presetList);
  const presetName = input(presets, "Preset name", ""),
    presetDescription = input(presets, "Preset description", "");
  async function presetRows() {
    presetList.replaceChildren();
    const rows = await ctx.api("/api/presets");
    for (const item of rows) {
      const row = text("div", undefined, "plan-row");
      row.append(
        text("strong", item.name),
        text("span", item.description || ""),
      );
      row.append(
        button("Apply", async () => {
          const resolved = await ctx.api("/api/presets/resolve", item);
          if (
            resolved.warnings.length &&
            !confirm(
              resolved.warnings.join("\n") +
                "\nApply supported instruments only?",
            )
          )
            return;
          await ctx.api("/api/instruments", resolved.instruments);
          configs($("live-instrument-config"), resolved.instruments);
        }),
        button("Edit / rename", () => {
          presetName.value = item.name;
          presetDescription.value = item.description || "";
          presetName.dataset.id = item.id;
          configs($("live-instrument-config"), item.instruments);
        }),
        button("Duplicate", async () => {
          await ctx.api("/api/presets", {
            ...item,
            id: crypto.randomUUID(),
            name: item.name + " copy",
          });
          await presetRows();
        }),
        button("Delete preset", async () => {
          if (
            confirm(
              "Delete this reusable preset? Saved mission snapshots remain unchanged.",
            )
          ) {
            await ctx.api(`/api/presets/${item.id}/delete`, {});
            await presetRows();
          }
        }),
      );
      presetList.append(row);
    }
  }
  presets.append(
    button("Save current configuration as preset", async () => {
      await ctx.api("/api/presets", {
        id: presetName.dataset.id || crypto.randomUUID(),
        name: presetName.value,
        description: presetDescription.value,
        instruments: readConfigs($("live-instrument-config")),
      });
      delete presetName.dataset.id;
      await presetRows();
    }),
  );
  for (const recommendation of templates.filter((t) => t.instruments.length))
    presets.append(
      button("Recommended: " + recommendation.name, () =>
        configs($("live-instrument-config"), recommendation.instruments),
      ),
    );
  await presetRows();
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
  const scienceMapPanel = text("article", undefined, "panel research-panel");
  scienceMapPanel.append(text("h2", "Science observation map"));
  p.append(scienceMapPanel);
  scienceMap?.destroy();
  scienceMap = new ResearchMap(scienceMapPanel);
  scienceMap.set({
    track,
    markers,
    plan: livePlan || {},
    samples: samplePositions(scienceRows),
    aircraft: aircraftFor(state),
  });
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
  const remote = text("div");
  remote.id = "remote-product-browser";
  p.append(remote);
  try {
    await remoteProducts(remote, ctx.api, state?.activeMission?.id || null);
  } catch (error) {
    remote.append(text("p", error.message));
  }
  if (scienceRows.length) {
    chart.replaceChildren();
    scienceCharts(chart, scienceRows);
  }
  lastFetch = 0;
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
  syncUnits(next.research?.units);
  guidance($("flight-guidance"), next.research?.guidance);
  const p = $("instrument-values");
  if (p) {
    const opened = new Set(
      [...p.querySelectorAll("details[open]")].map((d) => d.dataset.instrument),
    );
    p.replaceChildren();
    p.className = "science-cards";
    let previousGroup;
    for (const i of next.research?.instruments || []) {
      const card = text("article", undefined, "panel science-card");
      const d = definitions.find((d) => d.id === i.instrumentId);
      const isRemote = d?.measurements.length === 0;
      const group=isRemote?"Remote-Sensing Instruments":"Atmospheric Instruments";
      if(group!==previousGroup){p.append(text("h2",group));previousGroup=group;}
      card.append(
        text("h3", d?.name || i.instrumentId),
        text("span", i.enabled ? i.quality : "Disabled", "science-state"),
      );
      for (const v of i.measurements) {
        const label =
          d?.measurements.find((m) => m.id === v.measurementId)?.name ||
          v.measurementId;
        const reading = displayReading(v.measurementId, v.value, v.unit);
        card.append(
          text("p", label, "secondary-label"),
          text(
            "div",
            i.quality === "valid" && v.quality === "valid"
              ? `${n(reading.value, reading.decimals)} ${reading.unit}`
              : "Unavailable",
            "science-value",
          ),
        );
      }
      card.append(
        text(
          "p",
          `Every ${i.intervalMs / 1000} s · ${i.acquisitionSets} acquisition sets · ${i.observationCount} ${isRemote ? "saved products" : "measurement values"}`,
        ),
      );
      const details = text("details");
      details.dataset.instrument = i.instrumentId;
      details.open = opened.has(i.instrumentId);
      details.append(
        text("summary", "Acquisition diagnostics"),
        text(
          "p",
          `Last successful: ${i.lastSuccessfulUtc || "None"} · Actual interval: ${n(i.actualAverageIntervalSeconds)} s · Rejected: ${i.rejectedObservations} · Unexpected missing: ${i.missingIntervals}`,
        ),
        text(
          "p",
          i.message ||
            "Simulator-reported values; no independent calibrated measurements.",
        ),
      );
      card.append(details);
      if (isRemote)
        card.append(
          text(
            "p",
            i.message || "Import supported real geographic data to collect.",
          ),
          button("View Data", async () => {
            const selector=$("remote-product-browser")?.querySelector('select[aria-label="Product instrument"]');
            if(selector){selector.value=i.instrumentId;selector.dispatchEvent(new Event("change"));}
            $("remote-product-browser")?.scrollIntoView({
              behavior: "smooth",
              block: "start",
            });
          }),
        );
      p.append(card);
    }
  }
  const collection = $("collection-status");
  const collectionKey = `${next.activeMission?.id}|${next.research?.collectionStatus}|${next.research?.intentionallyPaused}`;
  if (collection && collection.dataset.key !== collectionKey) {
    collection.dataset.key = collectionKey;
    collection.replaceChildren(
      text(
        "strong",
        `Science collection: ${next.research?.collectionStatus || "stopped"}`,
      ),
    );
    if (next.activeMission)
      collection.append(
        button(
          next.research?.intentionallyPaused
            ? "Resume science collection"
            : "Pause science collection",
          async () =>
            ctx.api(`/api/missions/${state.activeMission.id}/science/state`, {
              paused: !state.research?.intentionallyPaused,
            }),
        ),
      );
    collection.append(
      text(
        "p",
        "Pausing science keeps flight telemetry recording and preserves the current datasets and configuration.",
      ),
    );
  }
  let summary = $("science-control-summary");
  if (!summary) {
    summary = text("article", undefined, "panel research-panel");
    summary.id = "science-control-summary";
    $("control").append(summary);
  }
  const instruments = next.research?.instruments || [];
  summary.replaceChildren(
    text("h2", "Science Collection"),
    text(
      "p",
      `${next.research?.collectionStatus || "stopped"} · ${instruments.filter((i) => i.enabled).length} instruments enabled · ${instruments.reduce((n, i) => n + i.acquisitionSets, 0)} acquisition sets · ${instruments.reduce((n, i) => n + i.observationCount, 0)} measurement values`,
    ),
  );
  const link = text("a", "View Science");
  link.href = "#science";
  summary.append(link);
  if (next.activeMission && livePlan) {
    summary.append(
      text("h3", "Active Research Route"),
      text(
        "p",
        `${livePlan.patterns?.map((p) => p.kind).join(", ") || "Custom route"} · ${livePlan.waypoints?.length || 0} planned waypoints · Current position: ${next.telemetry ? "available" : "unavailable"}`,
      ),
    );
    const mapLink = text("a", "View Mission Map");
    mapLink.href = "#control";
    mapLink.onclick = () => {
      liveMap?.fit();
      $("mission-map").scrollIntoView({ behavior: "smooth", block: "start" });
    };
    summary.append(mapLink);
    if (activeRouteState?.revision)
      summary.append(
        text(
          "p",
          `Next observed waypoint: ${livePlan.waypoints?.[activeRouteState.nextWaypoint]?.name || "End of route"} · observed in order within 1,000 m; review full analysis after flight.`,
        ),
      );
  }
  if (liveId !== next.activeMission?.id) {
    liveId = next.activeMission?.id;
    track = [];
    markers = [];
    scienceRows = [];
    cursor = 0;
    trackCursor = 0;
    livePlan = null;
    activeRouteState = null;
    lastFetch = 0;
  }
  const t = next.telemetry,
    fresh =
      t &&
      next.connection.state === "connected" &&
      Date.now() - Date.parse(t.timestampUtc) < 6000;
  scienceMap?.set({
    track,
    markers,
    plan: livePlan || {},
    aircraft: aircraftFor(next),
    samples: samplePositions(scienceRows),
  });
  liveMap?.set({
    track,
    markers,
    samples: samplePositions(scienceRows),
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
let livePlan = null,
  activeRouteState = null;
async function refreshLive(id) {
  const [points, marks, research, science, navigation] = await Promise.all([
    ctx.api(
      `/api/missions/${id}/track?limit=1500${trackCursor ? `&after=${trackCursor}` : ""}`,
    ),
    ctx.api(`/api/missions/${id}/markers?limit=200`),
    ctx.api(`/api/missions/${id}/research`),
    ctx.api(`/api/missions/${id}/science?after=${cursor}&limit=1000`),
    ctx.api("/api/maps/active"),
  ]);
  if (liveId !== id) return;
  track = trackCursor ? [...track, ...points.points] : points.points;
  if (track.length > 3000)
    track = track.filter((_, i) => i % 2 === 0 || i === track.length - 1);
  trackCursor = points.nextSequence;
  markers = marks;
  livePlan = research.planSnapshot;
  activeRouteState = navigation;
  scienceRows = [...scienceRows, ...science.observations].slice(-3000);
  cursor = science.next;
  liveMap?.set({
    track,
    markers,
    samples: samplePositions(scienceRows),
    plan: livePlan || {},
    aircraft: aircraftFor(state),
  });
  if ($("science-charts") && !document.querySelector(".expanded-chart[open]")) {
    $("science-charts").replaceChildren();
    scienceCharts($("science-charts"), scienceRows);
  }
}
function showScience(parent, rows, label) {
  parent.replaceChildren(text("p", label));
  scienceCharts(parent, rows);
  const raw = text("details");
  raw.append(text("summary", "Original canonical observation rows"));
  parent.append(raw);
  const list = text("div", undefined, "observation-table");
  for (const r of rows)
    list.append(
      text(
        "p",
        `${r.timestampUtc} · ${r.measurementId} ${n(r.value, 8)} ${r.unit} · ${r.quality} · ${r.provenance}`,
      ),
    );
  raw.append(list);
}
export async function researchDetail(body, id, mission) {
  await initializeUnits(ctx.api);
  const review = await ctx.api(`/api/missions/${id}/review`);
  const protection = await ctx.api(`/api/missions/${id}/protection`);
  const protectedLabel = text(
    "strong",
    protection.isProtected ? "Protected mission" : "Mission is not pinned",
  );
  const protectMissionButton=button(
      protection.isProtected ? "Remove Protection" : "Protect Mission",
      async () => {
        const result = await ctx.api(`/api/missions/${id}/protection`, {
          protected: !protection.isProtected,
        });
        protection.isProtected = result.isProtected;
        protectedLabel.textContent = result.isProtected
          ? "Protected mission"
          : "Mission is not pinned";
        protectMissionButton.textContent=result.isProtected?"Remove Protection":"Protect Mission";
      },
    );
  body.append(protectedLabel,protectMissionButton);
  if (review.scienceAvailable)
    try {
      await remoteProducts(body, ctx.api, id);
    } catch (error) {
      body.append(text("p", "Remote products unavailable: " + error.message));
    }
  if (review.research.planSnapshot) {
    body.append(text("h3", "Planned Route — immutable recording snapshot"));
    routeExportButton(body, () => review.research.planSnapshot, ctx.api);
    body.append(
      text(
        "p",
        "Recorded Flight exports below contain actual saved telemetry; planned-route exports preserve the original mission geometry.",
      ),
    );
  }
  await renderAnalysis(body, id, review.research.planSnapshot, ctx.api);
  body.append(
    text("h3", "Research review"),
    text("p", "Recording source: " + (mission.source || "Unavailable")),
    text(
      "p",
      `${review.research.template} · ${review.research.objective || "No objective supplied"}`,
    ),
  );
  for (const pause of review.sciencePauses || [])
    body.append(
      text(
        "p",
        `Science Collection deliberately paused: ${pause.startedUtc} to ${pause.endedUtc || "unknown end"} · ${pause.seconds == null ? "duration unknown" : n(pause.seconds) + " s"}${pause.interrupted ? " · recording interrupted" : ""}. Flight recording remained independent.`,
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
        `${s.measurementId}: ${s.count} measurement values · min ${n(displayReading(s.measurementId, s.minimum, s.unit).value, 3)} · max ${n(displayReading(s.measurementId, s.maximum, s.unit).value, 3)} · mean ${n(displayReading(s.measurementId, s.mean, s.unit).value, 3)} ${displayReading(s.measurementId, 0, s.unit).unit}`,
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
  scienceMap?.set({ track, markers, plan: livePlan || {}, aircraft: null });
}
