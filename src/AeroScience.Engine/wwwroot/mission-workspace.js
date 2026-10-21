import {layerPopup} from './dialog-ui.js';
import { ResearchMap } from "./map.js";
import { planningArea, planningDistance, displayReading } from "./science-display.js";
import {table as scientificTable} from './workstation-ui.js';
const el = (tag, text) => {
  const n = document.createElement(tag);
  if (text !== undefined) n.textContent = text;
  return n;
};
const action = (parent, name, fn) => {
  const b = el("button", name);
  b.type = "button";
  b.className = "secondary";
  b.onclick = () =>
    Promise.resolve()
      .then(fn)
      .catch((error) => {
        let p = parent.querySelector(".workspace-error");
        if (!p) {
          p = el("p");
          p.className = "workspace-error";
          p.setAttribute("role", "alert");
          parent.append(p);
        }
        p.textContent = error.message;
      });
  parent.append(b);
  return b;
};
const link = (parent, name, url) => {
  const a = el("a", name);
  a.href = url;
  a.className = "button secondary";
  parent.append(a);
  return a;
};
const format = (value, digits = 2) =>
  Number.isFinite(value)
    ? value.toLocaleString(undefined, { maximumFractionDigits: digits })
    : "Unavailable";
function table(parent, rows) {
  const t = el("table");
  for (const [name, value] of rows) {
    const tr = el("tr");
    tr.append(el("th", name), el("td", value));
    t.append(tr);
  }
  parent.append(t);
  return t;
}

export function missionWorkspace(parent, id, mission, api, review) {
  const existing = [...parent.childNodes],
    root = el("section"),
    nav = el("div"),
    panels = {};
  root.className = "mission-workspace";
  nav.setAttribute("role", "tablist");
  nav.setAttribute("aria-label", "Mission research workspace");
  root.append(nav);
  parent.replaceChildren(root);
  let stopReplay = () => {},
    active = "overview";
  const loaded = new Set(["overview"]);
  const builders = {
    replay: (p) =>
      replay(p, id, mission, api, review, (stop) => {
        stopReplay = stop;
      }),
    coverage: (p) => coverage(p, id, api, review),
    imagery: (p) => imagery(p, id, api),
    analysis: (p) => atmosphere(p, id, api),
    export: (p) => exports(p, id),
  };
  for (const title of [
    "Overview",
    "Replay",
    "Coverage",
    "Imagery",
    "Analysis",
    "Export",
  ]) {
    const key = title.toLowerCase(),
      button = el("button", title),
      panel = el("section");
    button.type = "button";
    button.id = `workspace-tab-${key}`;
    button.setAttribute("role", "tab");
    button.setAttribute("aria-controls", `workspace-panel-${key}`);
    button.setAttribute("aria-selected", String(key === active));
    panel.id = `workspace-panel-${key}`;
    panel.setAttribute("role", "tabpanel");
    panel.setAttribute("aria-labelledby", button.id);
    panel.hidden = key !== active;
    panels[key] = panel;
    nav.append(button);
    root.append(panel);
    button.onclick = async () => {
      if (active === "replay") stopReplay();
      active = key;
      for (const b of nav.children)
        b.setAttribute("aria-selected", String(b === button));
      for (const [k, p] of Object.entries(panels)) p.hidden = k !== key;
      if (!loaded.has(key)) {
        loaded.add(key);
        try {
          await builders[key]?.(panel);
        } catch (error) {
          loaded.delete(key);
          panel.replaceChildren(el("p", error.message));
        }
      }
      window.dispatchEvent(new Event("resize"));
    };
  }
  panels.overview.append(...existing);
  return panels;
}
export async function replay(parent, id, mission, api, review, setStop) {
  parent.append(
    el("h3", "Recorded mission replay"),
    el(
      "p",
      "Replay uses actual saved times. Playback changes this view only. Gaps remain gaps; missing observations are never filled.",
    ),
  );
  const tools = el("div");
  tools.className = "map-tools";
  parent.append(tools);
  const speed = el("select");
  speed.setAttribute("aria-label", "Playback speed");
  for (const rate of [0.5, 1, 2, 5, 10]) {
    const o = el("option", rate + "×");
    o.value = rate;
    speed.append(o);
  }
  speed.value = 1;
  tools.append(speed);
  const timeline = el("input");
  timeline.type = "range";
  timeline.min = Date.parse(mission.startedUtc);
  timeline.max = Date.parse(mission.endedUtc ?? mission.startedUtc);
  timeline.step = 100;
  timeline.value = timeline.min;
  timeline.setAttribute("aria-label", "Recorded mission time");
  parent.append(timeline);
  const status = el("p");
  status.setAttribute("role", "status");
  status.setAttribute('aria-label','Replay status');
  parent.append(status);
  const mapPanel = el("div");
  parent.append(mapPanel);
  const map = new ResearchMap(mapPanel, null, {
    aircraftLabel: "Recorded aircraft",
  });
  const readings = el("div"),
    acquisitions = el("div");
  parent.append(readings, acquisitions);
  let page = await api(`/api/missions/${id}/replay`),
    index = 0,
    timer,
    clock = Number(timeline.min),
    lastTick,
    lastAcquisition,
    acquisitionGeneration = 0,
    seekGeneration = 0;
  const stop = () => {
    clearInterval(timer);
    timer = null;
    play.textContent = "Play";
  };
  setStop(stop);
  async function draw() {
    const sample = page.samples[index];
    if (!sample) {
      status.textContent = "No saved sample at or after this time.";
      return;
    }
    const f = sample.frame;
    const previous = page.samples[index - 1]?.frame,
      broken =
        previous &&
        Date.parse(f.timestampUtc) - Date.parse(previous.timestampUtc) > 5000;
    status.textContent = `Saved sample ${f.timestampUtc} · ${f.aircraft}${broken ? " · recording gap before this sample" : ""} · cursor ${new Date(clock).toISOString()}`;
    const recent = page.samples
      .slice(Math.max(0, index - 499), index + 1)
      .map((s, i, a) => ({
        latitude: s.frame.latitude,
        longitude: s.frame.longitude,
        breakBefore:
          i > 0 &&
          Date.parse(s.frame.timestampUtc) -
            Date.parse(a[i - 1].frame.timestampUtc) >
            5000,
      }));
    map.set({
      plan: review.research.planSnapshot,
      aircraft: { ...f, headingDegrees: f.headingDegrees },
      track: recent,
    });
    readings.replaceChildren();
    table(readings, [
      ["Altitude MSL", format(f.altitudeMslFeet) + " ft"],
      ["Groundspeed", format(f.groundspeedKnots) + " kt"],
      ["Temperature", format(f.atmosphere?.temperatureCelsius) + " °C"],
      ["Pressure", format(f.atmosphere?.pressureInHg) + " inHg"],
      [
        "Ground / environment source times",
        "See each saved auxiliary reading below",
      ],
    ]);
    for (const r of f.environment?.readings ?? [])
      readings.append(
        el(
          "p",
          `${r.simulatorVariable}: ${format(r.value, 4)} ${r.unit} · ${r.quality} · ${r.timestampUtc}`,
        ),
      );
    if (!f.environment)
      readings.append(
        el(
          "p",
          "Ground and environment readings unavailable in this saved sample.",
        ),
      );
    if (lastAcquisition !== sample.sequence) {
      lastAcquisition = sample.sequence;
      const own = ++acquisitionGeneration;
      try {
        const [available,context] = await Promise.all([
          api(`/api/missions/${id}/replay/acquisitions?at=${encodeURIComponent(f.timestampUtc)}`).catch(error=>({error:error.message})),
          api(`/api/missions/${id}/replay/context?at=${encodeURIComponent(f.timestampUtc)}`)
        ]);
        const products=Array.isArray(available)?available:[];
        if (own !== acquisitionGeneration || !parent.isConnected) return;
        acquisitions.replaceChildren(
          el("h4", "Saved acquisitions within ±3 seconds"),
        );
        acquisitions.append(el('p',`Instrument state: ${context.instruments.scienceState} · configuration time ${context.instruments.configurationUtc??'not archived'}`));
        for(const c of context.instruments.instruments??[])if(c.enabled)acquisitions.append(el('p',`${c.instrumentId} · ${c.intervalMs/1000}s · ${c.payload?.profile.name??'profile not archived'}`));
        if(context.error||available.error)acquisitions.append(el('p','Scientific library unavailable: '+(context.error??available.error)));
        for(const row of (context.observations??[]).slice(0,200))acquisitions.append(el('p',`${row.measurementId}: ${format(row.value,5)} ${row.unit} · ${row.timestampUtc} · ${row.quality}`));
        if(context.observations?.length>200)acquisitions.append(el('p','Observation alignment window truncated at 200 values; full observations remain in research exports.'));
        map.set({
          ...map.data,
          footprints: products
            .slice(0, 100)
            .map((p) => ({
              vertices: p.footprint.boundary,
              label: `${p.instrumentId} · actual packet ${p.flightPacketUtc} · source ${p.sourceObservationUtc ?? "unknown date"}`,
            })),
        });
        for (const p of products.slice(0, 100))
          link(
            acquisitions,
            `${p.instrumentId} · ${p.flightPacketUtc} · original source ${p.sourceObservationUtc ?? "date unknown"}`,
            `/api/science/products/${p.id}/export/json`,
          );
      } catch (error) {
        map.set({...map.data,footprints:[]});
        acquisitions.textContent =
          "Scientific library unavailable: " + error.message;
      }
    }
  }
  async function seek(utc) {
    stop();
    const own=++seekGeneration;
    const selected = await api(
      `/api/missions/${id}/replay?at=${encodeURIComponent(new Date(utc).toISOString())}`,
    );
    if(own!==seekGeneration||!parent.isConnected)return;
    page=selected;
    index = 0;
    clock = utc;
    timeline.value = utc;
    await draw();
  }
  async function next() {
    if (index + 1 < page.samples.length) index++;
    else if (page.hasMore) {
      page = await api(`/api/missions/${id}/replay?after=${page.nextSequence}`);
      index = 0;
    } else {
      stop();
      return;
    }
    clock = Date.parse(page.samples[index].frame.timestampUtc);
    timeline.value = clock;
    await draw();
  }
  let playbackBusy=false;
  const play = action(tools, "Play", () => {
    if (timer) {
      stop();
      return;
    }
    lastTick = performance.now();
    play.textContent = "Pause";
    timer = setInterval(async () => {
      if(playbackBusy)return;playbackBusy=true;
      try{
      if (!parent.isConnected || parent.hidden || parent.closest("dialog")?.open===false) {
        stop();
        return;
      }
      const now = performance.now();
      clock += (now - lastTick) * Number(speed.value);
      lastTick = now;
      timeline.value = clock;
      const following = page.samples[index + 1];
      if (following && Date.parse(following.frame.timestampUtc) <= clock) {
        while (
          index + 1 < page.samples.length &&
          Date.parse(page.samples[index + 1].frame.timestampUtc) <= clock
        )
          index++;
        await draw();
      } else if (!following) {
        if (page.hasMore) {
          const saved = clock;
          await next();
          clock = Math.max(saved, clock);
        } else stop();
      } else if (
        Date.parse(following.frame.timestampUtc) -
          Date.parse(page.samples[index].frame.timestampUtc) >
        5000
      )
        status.textContent =
          "Recording gap · last known position retained; no intermediate science observation exists.";
      }catch(error){status.textContent="Replay unavailable: "+error.message;stop();}finally{playbackBusy=false;}
    }, 100);
  });
  action(tools, "Previous sample", async () => {
    stop();
    if (index > 0) {
      index--;
      clock = Date.parse(page.samples[index].frame.timestampUtc);
      await draw();
    } else {
      const utc = Date.parse(page.samples[0].frame.timestampUtc);
      page = await api(
        `/api/missions/${id}/replay?at=${encodeURIComponent(new Date(utc - 60000).toISOString())}&limit=500`,
      );
      index = Math.max(
        0,
        page.samples.findLastIndex(
          (s) => Date.parse(s.frame.timestampUtc) < utc,
        ),
      );
      clock = Date.parse(page.samples[index].frame.timestampUtc);
      await draw();
    }
  });
  action(tools, "Next sample", () => {
    stop();
    return next();
  });
  timeline.onchange = () => seek(Number(timeline.value));
  const jumps = el("div");
  parent.append(jumps);
  let eventAfter = 0,
    acquisitionOffset = 0,
    noteOffset = 0;
  const events = el("div"),
    products = el("div"),
    notes = el("div");
  jumps.append(events, products, notes);
  action(events, "Load next events", async () => {
    const rows = await api(
      `/api/missions/${id}/replay/events?after=${eventAfter}`,
    );
    events.querySelectorAll(".jump-row").forEach((n) => n.remove());
    for (const r of rows) {
      const b = action(
        events,
        `${r.timestampUtc} · ${r.kind}: ${r.message}`,
        () => seek(Date.parse(r.timestampUtc)),
      );
      b.classList.add("jump-row");
      eventAfter = r.id;
    }
  });
  action(products, "Load next acquisitions", async () => {
    const page = await api(
      `/api/science/products/page?missionId=${id}&offset=${acquisitionOffset}`,
    );
    products.querySelectorAll(".jump-row").forEach((n) => n.remove());
    for (const p of page.items) {
      const b = action(
        products,
        `${p.instrumentId} · ${p.flightPacketUtc}`,
        () => seek(Date.parse(p.flightPacketUtc)),
      );
      b.classList.add("jump-row");
    }
    acquisitionOffset += page.items.length;
  });
  action(notes, "Load next notes and markers", async () => {
    const rows = await Promise.all(
      ["notes", "markers"].map((kind) =>
        api(`/api/missions/${id}/${kind}?limit=100&offset=${noteOffset}`),
      ),
    );
    notes.querySelectorAll(".jump-row").forEach((n) => n.remove());
    for (const r of rows.flat()) {
      const b = action(
        notes,
        `${r.timestampUtc} · ${r.text ?? r.notes ?? r.category}`,
        () => seek(Date.parse(r.timestampUtc)),
      );
      b.classList.add("jump-row");
    }
    noteOffset += 100;
  });
  await draw();
}
async function runJob(parent, job, api, done) {
  const status = el("p", "Queued");
  status.setAttribute("role", "status");
  status.setAttribute('aria-label','Scientific analysis status');
  parent.append(status);
  const cancel = action(parent, "Cancel analysis", () =>
    api(`/api/analysis/jobs/${job.id}/cancel`, {}),
  );
  while (parent.isConnected) {
    const current = await api(`/api/analysis/jobs/${job.id}`);
    status.textContent = `${current.state} · ${current.completed}${current.total ? " / " + current.total : ""} · ${current.message ?? ""}`;
    if (["Completed", "Failed", "Cancelled"].includes(current.state)) {
      cancel.remove();
      if (current.state === "Completed") await done(current);
      return;
    }
    await new Promise((resolve) => setTimeout(resolve, 700));
  }
}
export async function coverage(parent, id, api, review) {
  parent.append(
    el("h3", "Scientific coverage"),
    el(
      "p",
      "Compare modeled acquisition footprints with saved valid native-pixel support. Planned-route adherence remains a separate analysis. Source scenes and dates are evaluated separately.",
    ),
  );
  const label = el("label", "Instrument"),
    choice = el("select");
  choice.setAttribute("aria-label", "Coverage instrument");
  for (const [value, name] of [
    ["multispectral-imager", "Multispectral / NDVI"],
    ["thermal-imager", "Thermal"],
    ["terrain-scanner", "Terrain"],
    ["hyperspectral-imager", "Hyperspectral"],
  ]) {
    const o = el("option", name);
    o.value = value;
    choice.append(o);
  }
  label.append(choice);
  parent.append(label);
  const bandLabel = el("label", "Specific saved band (optional)"),
    band = el("input");
  bandLabel.append(band);
  parent.append(bandLabel);
  const results = el("div");
  parent.append(results);
  let map;
  action(parent, "Calculate coverage", async () => {
    results.replaceChildren();
    const job = await api(`/api/missions/${id}/coverage`, {
      instrumentId: choice.value,
      bandId: band.value || null,
    });
    await runJob(results, job, api, async (done) => {
      for (const artifact of done.artifacts) {
        link(
          results,
          "Download coverage analysis and geographic polygons",
          `/api/science/artifacts/${artifact}`,
        );
        const catalog = await api(`/api/missions/${id}/artifacts`);
        if ((catalog.find((a) => a.id === artifact)?.bytes ?? Infinity) > 8 * 1024 * 1024) {
          const summary = await api(`/api/science/artifacts/${artifact}/metadata`);
          results.append(el("p", `Detailed polygons exceed the 8 MiB interactive-map limit. Download the full analysis above. Valid scientific support: ${planningArea(summary.validDataSquareMetres)} · ${format(summary.validDataSurveyPercent)}% of the saved survey.`));
          continue;
        }
        const data = await api(`/api/science/artifacts/${artifact}`);
        results.append(
          el(
            "p",
            `Modeled geometry: ${planningArea(data.geometricSquareMetres)} · survey denominator: ${data.surveySquareMetres == null ? "unavailable (no survey polygon)" : planningArea(data.surveySquareMetres)} · geometric coverage ${format(data.geometricSurveyPercent)}%`,
          ),
        );
        const mapPanel = el("div");
        results.append(mapPanel);
        map?.destroy();
        map = new ResearchMap(mapPanel);
        map.set({ plan: review.research.planSnapshot });
        const layers = {};
        layers["Modeled geometric coverage"] = window.L.geoJSON(
          data.geometricGeoJson,
          { style: { color: "#8aa7ef", fillOpacity: 0.12 } },
        ).addTo(map.map);
        if (data.gapsGeoJson)
          layers["Geometric gaps"] = window.L.geoJSON(data.gapsGeoJson, {
            style: { color: "#edb384", fillOpacity: 0.12 },
          });
        if (data.validDataGeoJson)
          layers["All valid scientific support (independent dates)"] = window.L.geoJSON(data.validDataGeoJson, {
            style: { color: "#52dbc0", fillOpacity: 0.2 },
          });
        if (data.validDataGapsGeoJson)
          layers["Gaps in valid scientific support"] = window.L.geoJSON(data.validDataGapsGeoJson, {
            style: { color: "#ed8181", fillOpacity: 0.2 },
          });
        results.append(el("p", `Union of valid scientific support: ${planningArea(data.validDataSquareMetres)} · ${format(data.validDataSurveyPercent)}% of the survey. This combines geographic support only; source dates and measurements remain separate.`));
        for (const s of data.sources) {
          results.append(
            el(
              "p",
              `${s.band} · source ${s.sourceId} · ${s.savedValidSamples} saved valid samples · ${planningArea(s.validSupportSquareMetres)} · ${format(s.surveyPercent)}% of survey`,
            ),
          );
          layers[`Valid ${s.band} · ${s.sourceId.slice(0, 8)}`] =
            window.L.geoJSON(s.validSupportGeoJson, {
              style: { color: "#52dbc0", fillOpacity: 0.3 },
            }).addTo(map.map);
        }
        layerPopup(window.L.control.layers(null, layers).addTo(map.map));
        const detail = el("details");
        detail.append(
          el("summary", "Methods, denominator and uncertainty"),
          el("p", data.methods.join(" ")),
        );
        results.append(detail);
      }
    });
  });
}
export async function imagery(parent, id, api) {
  parent.append(
    el("h3", "Geographic scientific imagery"),
    el(
      "p",
      "Mission Sample Composite uses only saved valid native pixels. Retained Source Mosaic uses verified historical originals in the mission region; it is not an aircraft-collected image. Each source/scene/date remains separate.",
    ),
  );
  const products = await api(`/api/science/products/page?missionId=${id}`),
    sources = await api("/api/science/sources");
  const sourceLabel = el("label", "Retained source"),
    source = el("select");
  source.setAttribute("aria-label", "Composite source");
  for (const item of sources) {
    const s = item.source;
    const o = el(
      "option",
      `${s.manifest.name} · ${s.manifest.observationUtc ?? "date unknown"}`,
    );
    o.value = s.id;
    source.append(o);
  }
  sourceLabel.append(source);
  parent.append(sourceLabel);
  if (products.items[0]) source.value = products.items[0].sourceId;
  const bandLabel = el("label", "Scientific band or view"),
    band = el("select");
  band.setAttribute("aria-label", "Composite band");
  bandLabel.append(band);
  parent.append(bandLabel);
  function bands() {
    band.replaceChildren();
    const s = sources.find((i) => i.source.id === source.value)?.source;
    const ids =
      s?.manifest.kind === "thermal"
        ? ["temperature","temperature-celsius"]
        : s?.manifest.kind === "terrain"
          ? ["elevation"]
          : s?.manifest.kind === "multispectral"
            ? [
                "natural-color",
                "false-color",
                "ndvi",
                ...(s.manifest.bands || [])
                  .filter((b) => b.id !== "SCL")
                  .map((b) => b.id),
              ]
            : (s?.manifest.bands || [])
                .filter((b) => !b.badBand)
                .map((b) => b.id);
    for (const value of ids || []) {
      const b = s.manifest.bands.find((b) => b.id === value),
        o = el(
          "option",
          value + (b?.wavelengthNm ? ` · ${b.wavelengthNm} nm` : ""),
        );
      o.value = value;
      band.append(o);
    }
  }
  source.onchange = bands;
  bands();
  const modeLabel = el("label", "Product type"),
    mode = el("select");
  for (const [value, name] of [
    ["mission-samples", "Mission Sample Composite"],
    ["retained-source", "Retained Source Mosaic"],
  ]) {
    const o = el("option", name);
    o.value = value;
    mode.append(o);
  }
  modeLabel.append(mode);
  parent.append(modeLabel);
  const selectionLabel = el("label", "Repeated-pixel selection"),
    selection = el("select");
  for (const value of ["last", "first"]) {
    const o = el("option", value + " actual saved acquisition");
    o.value = value;
    selection.append(o);
  }
  selectionLabel.append(selection);
  parent.append(selectionLabel);
  const combineLabel=el('label'),combine=el('input');combine.type='checkbox';combine.checked=true;combineLabel.append(combine,' Combine compatible retained regions of this same scene');parent.append(combineLabel);
  const jobPanel = el("div"),
    list = el("div"),
    display = el("div");
  parent.append(jobPanel, list, display);
  let map, overlay;
  async function refresh() {
    const artifacts = await api(`/api/missions/${id}/artifacts`);
    list.replaceChildren();
    for (const a of artifacts.filter((a) => a.format === "tif")) {
      const row = el("div");
      row.className = "artifact-row";
      row.append(
        el(
          "span",
          `${a.kind} · ${new Date(a.createdUtc).toLocaleString()} · ${(a.bytes / 1048576).toFixed(2)} MiB`,
        ),
      );
      link(row, "GeoTIFF", `/api/science/artifacts/${a.id}`);
      link(
        row,
        "Metadata and lineage",
        `/api/science/artifacts/${a.id}/metadata`,
      );
      action(row, "View on map", async () => {
        const p = await api(`/api/science/artifacts/${a.id}/preview`);
        if (!map) {
          map = new ResearchMap(display);
        }
        overlay?.remove();
        overlay = window.L.imageOverlay(
          p.image,
          [
            [p.bounds.south, p.bounds.west],
            [p.bounds.north, p.bounds.east],
          ],
          { opacity: 0.8 },
        ).addTo(map.map);
        map.map.fitBounds(overlay.getBounds());
        let legend = display.querySelector(".raster-legend");
        if (!legend) {
          legend = el("p");
          legend.className = "raster-legend";
          display.append(legend);
        }
        legend.textContent = `${p.bands.join(" / ")} · ${format(p.minimum, 5)} – ${format(p.maximum, 5)} ${p.units.join(" / ")} · transparent missing data. ${p.method}`;
      });
      list.append(row);
    }
  }
  action(parent, "Generate geographic product", async () => {
    const job = await api(`/api/missions/${id}/composites`, {
      sourceId: source.value,
      bandId: band.value,
      mode: mode.value,
      selection: selection.value,
      combineCompatibleRegions:combine.checked,
    });
    await runJob(jobPanel, job, api, refresh);
  });
  action(parent, "Refresh completed products", refresh);
  await refresh();
}
export async function atmosphere(parent, id, api) {
  parent.append(el("h3", "Atmospheric and terrain analysis"));
  const result = await api(`/api/missions/${id}/atmospheric-analysis`);
  parent.append(
    el(
      "p",
      `${result.totalSamples} recorded samples · ${result.gaps} gaps${result.downsampled ? " · chart points thinned for bounded display" : ""}`,
    ),
  );
  const controls = el("div"),
    canvas = el("canvas"),
    description = el("p");
  const figure=el('figure');figure.className='scientific-chart';figure.append(el('figcaption','Recorded atmospheric and terrain profile'),canvas);const accessibleValues=el('div');parent.append(controls, figure, description, accessibleValues);
  canvas.setAttribute("aria-label", "Recorded atmospheric and terrain profile");
  canvas.setAttribute("role", "img");
  const convert = (property, value) => {
    const category = {temperatureCelsius:"temperature",pressureInHg:"pressure",densitySlugsPerCubicFoot:"density",windKnots:"wind-speed",altitudeMetres:"altitude",groundAltitudeMetres:"altitude",clearanceMetres:"altitude",distanceMetres:"distance"}[property];
    if (!category) return {value,unit:property==="groundSlopeDegrees"?"degrees":"K/km"};
    return displayReading(category, Number.isFinite(value) && category==="altitude" ? value/.3048 : value, category==="altitude"?"ft":"");
  };
  const x = el("select");
  x.setAttribute("aria-label", "Profile horizontal axis");
  for (const [value, title] of [
    ["altitudeMetres", "Altitude MSL (m)"],
    ["distanceMetres", "Along-track distance (m)"],
  ]) {
    const o = el("option", `${title.split(" (")[0]} (${convert(value, 0).unit})`);
    o.value = value;
    x.append(o);
  }
  controls.append(x);
  const y = el("select");
  y.setAttribute("aria-label", "Profile variable");
  for (const [value, title] of [
    ["temperatureCelsius", "Temperature (°C)"],
    ["pressureInHg", "Pressure (inHg)"],
    ["densitySlugsPerCubicFoot", "Density (slug/ft³)"],
    ["windKnots", "Wind speed (kt)"],
    [
      "groundAltitudeMetres",
      "Simulator surface altitude (m; datum unspecified)",
    ],
    ["clearanceMetres", "Simulator clearance AGL (m)"],
    ["groundSlopeDegrees", "Along-track ground slope (degrees)"],
    ["lapseKelvinPerKilometre", "Local lapse (K/km)"],
  ]) {
    const o = el("option", `${title.split(" (")[0]} (${convert(value, 0).unit.replace(" MSL", "")}${value==="clearanceMetres"?" AGL":""})`);
    o.value = value;
    y.append(o);
  }
  controls.append(y);
  let chart;
  function draw() {
    chart?.destroy();
    const data = [];
    for (const p of result.profile) {
      const horizontal=convert(x.value,p[x.value]).value;
      if (p.breakBefore) data.push({ x: horizontal, y: null });
      data.push({ x: horizontal, y: convert(y.value,p[y.value]).value, utc: p.utc, auxiliaryUtc: p.atmosphereUtc, groundUtc: p.groundUtc });
    }
    if (window.Chart)
      chart = new window.Chart(canvas, {
        type: "line",
        data: {
          datasets: [
            {
              label: y.selectedOptions[0].textContent,
              data,
              borderColor: "#52dbc0",
              pointRadius: 2,
              spanGaps: false,
            },
          ],
        },
        options: {
          animation: false,
          parsing: false,
          plugins: {tooltip:{callbacks:{afterLabel:c=>`Aircraft ${c.raw.utc} · atmosphere ${c.raw.auxiliaryUtc??"unavailable"} · ground ${c.raw.groundUtc??"unavailable"}`}}},
          scales: {
            x: {
              type: "linear",
              title: { display: true, text: x.selectedOptions[0].textContent },
            },
            y: {
              title: { display: true, text: y.selectedOptions[0].textContent },
            },
          },
        },
      });
    description.textContent = "Actual recorded packets; no interpolation across gaps. Separate aircraft and auxiliary timestamps appear in chart tooltips and the accessible values table.";
    accessibleValues.replaceChildren();scientificTable(accessibleValues,'Recorded profile values',[x.selectedOptions[0].textContent,y.selectedOptions[0].textContent,'Aircraft UTC','Atmosphere UTC','Ground UTC','Gap before'],result.profile.map(p=>[format(convert(x.value,p[x.value]).value),format(convert(y.value,p[y.value]).value),p.utc,p.atmosphereUtc??'Unavailable',p.groundUtc??'Unavailable',p.breakBefore?'Yes':'No']));
  }
  x.onchange = y.onchange = draw;
  draw();
  const observer=new MutationObserver(()=>{if(!canvas.isConnected){chart?.destroy();observer.disconnect();}});observer.observe(document.body,{childList:true,subtree:true});
  const advanced=el("details");advanced.append(el("summary","Advanced — observed cloud encounters, altitude bins & methods"));for(const method of result.methods)advanced.append(el("p",method));parent.append(advanced);
  advanced.append(el("h4", "Observed cloud encounters"));
  for (const c of result.clouds)
    advanced.append(
      el(
        "p",
        `${c.entryObserved ? "Observed entry" : "Cloud present / entry unknown"} ${c.entryUtc} · ${format(convert("altitudeMetres",c.entryAltitudeMetres).value)} ${convert("altitudeMetres",0).unit} → ${c.exitObserved ? "observed exit" : "exit not established"} ${c.exitUtc ?? ""} ${format(convert("altitudeMetres",c.exitAltitudeMetres).value)} ${convert("altitudeMetres",0).unit}`,
      ),
    );
  advanced.append(el("h4", "Altitude interval summaries"));
  for (const b of result.altitudeBins || [])
    advanced.append(
      el(
        "p",
        `${b.direction} · ${format(convert("altitudeMetres",b.minimumAltitudeMetres).value)}–${format(convert("altitudeMetres",b.maximumAltitudeMetres).value)} ${convert("altitudeMetres",0).unit} · ${b.samples} independent packets · mean temperature ${format(convert("temperatureCelsius",b.temperatureMeanCelsius).value)} ${convert("temperatureCelsius",0).unit} · mean wind ${format(convert("windKnots",b.windMeanKnots).value)} ${convert("windKnots",0).unit}`,
      ),
    );
}
export function exports(parent, id) {
  parent.append(
    el("h3", "Export research"),
    el(
      "p",
      "The research ZIP includes recorded telemetry, auxiliary timestamps, notes/events, scientific acquisitions, source references and hashes, completed analysis products and methods. Original scientific source files remain in your library. Nothing is uploaded.",
    ),
  );
  link(
    parent,
    "Download Research Package (ZIP)",
    `/api/missions/${id}/export/research.zip`,
  );
  for (const format of [
    "flight.csv",
    "science.csv",
    "events.csv",
    "gpx",
    "kml",
    "report.txt",
  ])
    link(parent, format, `/api/missions/${id}/export/${format}`);
}
