import {message as localizedCopy,setPresentationText} from './i18n.js';
import {t as textForUI} from './i18n.js';
import {archivedSources} from "./archived-source-picker.js";
import { ResearchMap } from "./map.js";
import { planningArea, planningDistance, displayReading } from "./science-display.js";
import {table as scientificTable} from './workstation-ui.js';
const el = (tag, text) => {
  const n = document.createElement(tag);
  if (text !== undefined) setPresentationText(n,text);
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
        setPresentationText(p,error.message);
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
    : textForUI("localization.unavailableValue");
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
  setPresentationText(nav,localizedCopy("ui.missionWorkspace.messageMissionResearchWorkspace"),"aria-label");
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
    textForUI("results.overview"),
    textForUI("ui.missionWorkspace.messageReplay"),
    textForUI("ui.atmosphericResearch.messageCoverage"),
    textForUI("ui.missionWorkspace.messageImagery"),
    textForUI("ui.missionWorkspace.messageAnalysis"),
    textForUI("ui.scienceAnalysis.messageExport"),
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
    el("h3", localizedCopy("ui.missionWorkspace.messageRecordedMissionReplay")),
    el(
      "p",
      localizedCopy("ui.missionWorkspace.messageReplayUsesActualSavedTimesPlaybackChanges"),
    ),
  );
  const tools = el("div");
  tools.className = "map-tools";
  parent.append(tools);
  const speed = el("select");
  setPresentationText(speed,localizedCopy("ui.missionWorkspace.messagePlaybackSpeed"),"aria-label");
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
  setPresentationText(timeline,localizedCopy("ui.missionWorkspace.messageRecordedMissionTime"),"aria-label");
  parent.append(timeline);
  const status = el("p");
  status.setAttribute("role", "status");
  setPresentationText(status,localizedCopy("ui.missionWorkspace.messageReplayStatus"),"aria-label");
  parent.append(status);
  const mapPanel = el("div");
  parent.append(mapPanel);
  const map = new ResearchMap(mapPanel, null, {
    aircraftLabel: textForUI("ui.missionWorkspace.messageRecordedAircraft"),
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
    setPresentationText(play,localizedCopy("ui.missionWorkspace.messagePlay"));
  };
  setStop(stop);
  async function draw() {
    const sample = page.samples[index];
    if (!sample) {
      setPresentationText(status,localizedCopy("ui.missionWorkspace.messageNoSavedSampleAtOrAfterThis"));
      return;
    }
    const f = sample.frame;
    const previous = page.samples[index - 1]?.frame,
      broken =
        previous &&
        Date.parse(f.timestampUtc) - Date.parse(previous.timestampUtc) > 5000;
    setPresentationText(status,localizedCopy("ui.missionWorkspace.messageSavedSampleCursor",{value1:(String(f.timestampUtc)),value2:(String(f.aircraft)),value3:(String(broken ? " · recording gap before this sample" : "")),value4:(String(new Date(clock).toISOString()))}));
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
      [localizedCopy("ui.missionWorkspace.messageAltitudeMSL"), localizedCopy("ui.missionWorkspace.messageFt",{value1:(String(format(f.altitudeMslFeet)))})],
      [localizedCopy("ui.missionWorkspace.messageGroundspeed"), localizedCopy("ui.missionWorkspace.messageKt",{value1:(String(format(f.groundspeedKnots)))})],
      [localizedCopy("ui.missionWorkspace.messageTemperature"), format(f.atmosphere?.temperatureCelsius) + " °C"],
      [localizedCopy("ui.missionWorkspace.messagePressure"), localizedCopy("ui.missionWorkspace.messageInHg",{value1:(String(format(f.atmosphere?.pressureInHg)))})],
      [
        localizedCopy("ui.missionWorkspace.messageGroundEnvironmentSourceTimes"),
        localizedCopy("ui.missionWorkspace.messageSeeEachSavedAuxiliaryReadingBelow"),
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
          localizedCopy("ui.missionWorkspace.messageGroundAndEnvironmentReadingsUnavailableInThis"),
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
          el("h4", localizedCopy("ui.missionWorkspace.messageSavedAcquisitionsWithin3Seconds")),
        );
        acquisitions.append(el('p',localizedCopy("ui.missionWorkspace.messageInstrumentStateConfigurationTime",{value1:(String(context.instruments.scienceState)),value2:(String(context.instruments.configurationUtc??'not archived'))})));
        for(const c of context.instruments.instruments??[])if(c.enabled)acquisitions.append(el('p',`${c.instrumentId} · ${c.intervalMs/1000}s · ${c.payload?.profile.name??'profile not archived'}`));
        if(context.error||available.error)acquisitions.append(el('p',localizedCopy("ui.missionWorkspace.messageScientificLibraryUnavailable",{value1:(String(context.error??available.error))})));
        for(const row of (context.observations??[]).slice(0,200))acquisitions.append(el('p',`${row.measurementId}: ${format(row.value,5)} ${row.unit} · ${row.timestampUtc} · ${row.quality}`));
        if(context.observations?.length>200)acquisitions.append(el('p',localizedCopy("ui.missionWorkspace.messageObservationAlignmentWindowTruncatedAt200Values")));
        map.set({
          ...map.data,
          footprints: products
            .slice(0, 100)
            .map((p) => ({
              vertices: p.footprint.boundary,
              label: textForUI("ui.missionWorkspace.messageActualPacketSource",{value1:(String(p.instrumentId)),value2:(String(p.flightPacketUtc)),value3:(String(p.sourceObservationUtc ?? "unknown date"))}),
            })),
        });
        for (const p of products.slice(0, 100))
          link(
            acquisitions,
            localizedCopy("ui.missionWorkspace.messageOriginalSource",{value1:(String(p.instrumentId)),value2:(String(p.flightPacketUtc)),value3:(String(p.sourceObservationUtc ?? "date unknown"))}),
            `/api/science/products/${p.id}/export/json`,
          );
      } catch (error) {
        map.set({...map.data,footprints:[]});
        setPresentationText(acquisitions,localizedCopy("ui.missionWorkspace.messageScientificLibraryUnavailable",{value1:(String(error.message))}));
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
    setPresentationText(play,localizedCopy("lidar.pause"));
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
        setPresentationText(status,localizedCopy("ui.missionWorkspace.messageRecordingGapLastKnownPositionRetained"));
      }catch(error){setPresentationText(status,localizedCopy("ui.missionWorkspace.messageReplayUnavailable",{value1:(String(error.message))}));stop();}finally{playbackBusy=false;}
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
  const status = el("p", localizedCopy("ui.downloadActivity.messageQueued"));
  status.setAttribute("role", "status");
  setPresentationText(status,localizedCopy("ui.missionWorkspace.messageScientificAnalysisStatus"),"aria-label");
  parent.append(status);
  const cancel = action(parent, "Cancel analysis", () =>
    api(`/api/analysis/jobs/${job.id}/cancel`, {}),
  );
  while (parent.isConnected) {
    const current = await api(`/api/analysis/jobs/${job.id}`);
    setPresentationText(status,`${current.state} · ${current.completed}${current.total ? " / " + current.total : ""} · ${current.message ?? ""}`);
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
    el("h3", localizedCopy("ui.missionWorkspace.messageScientificCoverage")),
    el(
      "p",
      localizedCopy("ui.missionWorkspace.messageCompareModeledAcquisitionFootprintsWithSavedValid"),
    ),
  );
  const label = el("label", localizedCopy("ui.missionOperations.messageInstrument")),
    choice = el("select");
  setPresentationText(choice,localizedCopy("ui.missionWorkspace.messageCoverageInstrument"),"aria-label");
  for (const [value, name] of [
    ["multispectral-imager", textForUI("ui.missionWorkspace.messageMultispectralNDVI")],
    ["thermal-imager", textForUI("ui.missionWorkspace.messageThermal")],
    ["terrain-scanner", textForUI("ui.missionWorkspace.messageTerrain")],
    ["hyperspectral-imager", textForUI("ui.missionWorkspace.messageHyperspectral")],
  ]) {
    const o = el("option", name);
    o.value = value;
    choice.append(o);
  }
  label.append(choice);
  parent.append(label);
  const bandLabel = el("label", localizedCopy("ui.missionWorkspace.messageSpecificSavedBandOptional")),
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
          localizedCopy("ui.missionWorkspace.messageDownloadCoverageAnalysisAndGeographicPolygons"),
          `/api/science/artifacts/${artifact}`,
        );
        const catalog = await api(`/api/missions/${id}/artifacts`);
        if ((catalog.find((a) => a.id === artifact)?.bytes ?? Infinity) > 8 * 1024 * 1024) {
          const summary = await api(`/api/science/artifacts/${artifact}/metadata`);
          results.append(el("p", localizedCopy("ui.missionWorkspace.messageDetailedPolygonsExceedThe8MiBInteractiveMap",{value1:(String(planningArea(summary.validDataSquareMetres))),value2:(String(format(summary.validDataSurveyPercent)))})));
          continue;
        }
        const data = await api(`/api/science/artifacts/${artifact}`);
        results.append(
          el(
            "p",
            localizedCopy("ui.missionWorkspace.messageModeledGeometrySurveyDenominatorGeometric",{value1:(String(planningArea(data.geometricSquareMetres))),value2:(String(data.surveySquareMetres == null ? "unavailable (no survey polygon)" : planningArea(data.surveySquareMetres))),value3:(String(format(data.geometricSurveyPercent)))}),
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
        results.append(el("p", localizedCopy("ui.missionWorkspace.messageUnionOfValidScientificSupport",{value1:(String(planningArea(data.validDataSquareMetres))),value2:(String(format(data.validDataSurveyPercent)))})));
        for (const s of data.sources) {
          results.append(
            el(
              "p",
              localizedCopy("ui.missionWorkspace.messageSourceSavedValidSamples",{value1:(String(s.band)),value2:(String(s.sourceId)),value3:(String(s.savedValidSamples)),value4:(String(planningArea(s.validSupportSquareMetres))),value5:(String(format(s.surveyPercent)))}),
            ),
          );
          layers[`Valid ${s.band} · ${s.sourceId.slice(0, 8)}`] =
            window.L.geoJSON(s.validSupportGeoJson, {
              style: { color: "#52dbc0", fillOpacity: 0.3 },
            }).addTo(map.map);
        }
        for(const [name,layer] of Object.entries(layers))map.layers.addOverlay({id:'coverage-'+name.toLowerCase().replace(/[^a-z0-9]+/g,'-').slice(0,85),name,layer,group:"Science",description:name.startsWith('Modeled')||name.startsWith('Geometric')?textForUI("ui.missionWorkspace.messageModeledGeometryOnlyNotValidAcquiredPixels"):textForUI("ui.missionWorkspace.messageValidScientificSupportForTheNamedOriginal")});
        const detail = el("details");
        detail.append(
          el("summary", localizedCopy("ui.missionWorkspace.messageMethodsDenominatorAndUncertainty")),
          el("p", data.methods.join(" ")),
        );
        results.append(detail);
      }
    });
  });
}
export async function imagery(parent, id, api) {
  parent.append(
    el("h3", localizedCopy("ui.missionWorkspace.messageGeographicScientificImagery")),
    el(
      "p",
      localizedCopy("ui.missionWorkspace.messageMissionSampleCompositeUsesOnlySavedValid"),
    ),
  );
  const products = await api(`/api/science/products/page?missionId=${id}`),
    sources = (await archivedSources(api,parent,[id])).items;
  const sourceLabel = el("label", localizedCopy("ui.missionWorkspace.messageSavedAcquisitionSource")),
    source = el("select");
  setPresentationText(source,localizedCopy("ui.missionWorkspace.messageCompositeSource"),"aria-label");
  for (const item of sources) {
    const s = item.source;
    const o = el(
      "option",
      `${s.manifest.name} · ${s.manifest.observationUtc ?? "date unknown"}${item.originalsRetained?"":" · originals deleted; saved values retained"}`,
    );
    o.value = s.id;
    source.append(o);
  }
  sourceLabel.append(source);
  parent.append(sourceLabel);
  if (products.items[0]) source.value = products.items[0].sourceId;
  const bandLabel = el("label", localizedCopy("ui.missionWorkspace.messageScientificBandOrView")),
    band = el("select");
  setPresentationText(band,localizedCopy("ui.missionWorkspace.messageCompositeBand"),"aria-label");
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
          value + (b?.wavelengthNm ? localizedCopy("ui.missionWorkspace.messageNm",{value1:(String(b.wavelengthNm))}) : ""),
        );
      o.value = value;
      band.append(o);
    }
  }
  parent.addEventListener('aeroscience-archived-sources',()=>{const value=source.value;for(const item of sources)if(![...source.options].some(option=>option.value===item.source.id)){const option=el('option',item.source.manifest.name+' · '+(item.originalsRetained?localizedCopy("ui.missionWorkspace.messageOriginalsRetained"):localizedCopy("ui.missionWorkspace.messageSavedValuesOnly")));option.value=item.source.id;source.append(option);}source.value=value;});
  source.onchange = bands;
  bands();
  const modeLabel = el("label", localizedCopy("ui.missionWorkspace.messageProductType")),
    mode = el("select");
  for (const [value, name] of [
    ["mission-samples", textForUI("ui.missionWorkspace.messageMissionSampleComposite")],
    ["retained-source", textForUI("ui.missionWorkspace.messageRetainedSourceMosaic")],
  ]) {
    const o = el("option", name);
    o.value = value;
    mode.append(o);
  }
  modeLabel.append(mode);
  parent.append(modeLabel);
  const selectionLabel = el("label", localizedCopy("ui.missionWorkspace.messageRepeatedPixelSelection")),
    selection = el("select");
  for (const value of ["last", "first"]) {
    const o = el("option", localizedCopy("ui.missionWorkspace.messageActualSavedAcquisition",{value1:(String(value))}));
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
          localizedCopy("ui.missionWorkspace.messageMiB",{value1:(String(a.kind)),value2:(String(new Date(a.createdUtc).toLocaleString())),value3:(String((a.bytes / 1048576).toFixed(2)))}),
        ),
      );
      link(row, localizedCopy("ui.missionWorkspace.messageGeoTIFF"), `/api/science/artifacts/${a.id}`);
      link(
        row,
        localizedCopy("ui.missionWorkspace.messageMetadataAndLineage"),
        `/api/science/artifacts/${a.id}/metadata`,
      );
      action(row, "View on map", async () => {
        const p = await api(`/api/science/artifacts/${a.id}/preview`);
        if (!map) {
          map = new ResearchMap(display);
          map.artifactGroup=window.L.layerGroup().addTo(map.map);
          map.layers.addOverlay({id:'scientific-artifact',name:'Retained scientific artifact',layer:map.artifactGroup,group:"Science",description:textForUI("ui.missionWorkspace.messageDerivedProductFromRetainedScientificEvidenceSource")});
        }
        map.artifactGroup.clearLayers();
        overlay = window.L.imageOverlay(
          p.image,
          [
            [p.bounds.south, p.bounds.west],
            [p.bounds.north, p.bounds.east],
          ],
          { opacity: 0.8 },
        ).addTo(map.artifactGroup);
        map.map.fitBounds(overlay.getBounds());
        let legend = display.querySelector(".raster-legend");
        if (!legend) {
          legend = el("p");
          legend.className = "raster-legend";
          display.append(legend);
        }
        setPresentationText(legend,localizedCopy("ui.missionWorkspace.messageTransparentMissingData",{value1:(String(p.bands.join(" / "))),value2:(String(format(p.minimum, 5))),value3:(String(format(p.maximum, 5))),value4:(String(p.units.join(" / "))),value5:(String(p.method))}));
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
  parent.append(el("h3", localizedCopy("ui.missionWorkspace.messageAtmosphericAndTerrainAnalysis")));
  const result = await api(`/api/missions/${id}/atmospheric-analysis`);
  parent.append(
    el(
      "p",
      localizedCopy("ui.missionWorkspace.messageRecordedSamplesGaps",{value1:(String(result.totalSamples)),value2:(String(result.gaps)),value3:(String(result.downsampled ? " · chart points thinned for bounded display" : ""))}),
    ),
  );
  const controls = el("div"),
    canvas = el("canvas"),
    description = el("p");
  const figure=el('figure');figure.className='scientific-chart';figure.append(el('figcaption',localizedCopy("ui.missionWorkspace.messageRecordedAtmosphericAndTerrainProfile")),canvas);const accessibleValues=el('div');parent.append(controls, figure, description, accessibleValues);
  setPresentationText(canvas,localizedCopy("ui.missionWorkspace.messageRecordedAtmosphericAndTerrainProfile"),"aria-label");
  canvas.setAttribute("role", "img");
  const convert = (property, value) => {
    const category = {temperatureCelsius:"temperature",pressureInHg:"pressure",densitySlugsPerCubicFoot:"density",windKnots:"wind-speed",altitudeMetres:"altitude",groundAltitudeMetres:"altitude",clearanceMetres:"altitude",distanceMetres:"distance"}[property];
    if (!category) return {value,unit:property==="groundSlopeDegrees"?"degrees":"K/km"};
    return displayReading(category, Number.isFinite(value) && category==="altitude" ? value/.3048 : value, category==="altitude"?"ft":"");
  };
  const x = el("select");
  setPresentationText(x,localizedCopy("ui.missionWorkspace.messageProfileHorizontalAxis"),"aria-label");
  for (const [value, title] of [
    ["altitudeMetres", textForUI("ui.missionWorkspace.messageAltitudeMSLM")],
    ["distanceMetres", textForUI("ui.missionWorkspace.messageAlongTrackDistanceM")],
  ]) {
    const o = el("option", `${title.split(" (")[0]} (${convert(value, 0).unit})`);
    o.value = value;
    x.append(o);
  }
  controls.append(x);
  const y = el("select");
  setPresentationText(y,localizedCopy("ui.missionWorkspace.messageProfileVariable"),"aria-label");
  for (const [value, title] of [
    ["temperatureCelsius", textForUI("ui.experimentalSensor.messageTemperatureC")],
    ["pressureInHg", textForUI("ui.missionWorkspace.messagePressureInHg")],
    ["densitySlugsPerCubicFoot", textForUI("ui.missionWorkspace.messageDensitySlugFt")],
    ["windKnots", textForUI("ui.missionWorkspace.messageWindSpeedKt")],
    [
      "groundAltitudeMetres",
      textForUI("ui.missionWorkspace.messageSimulatorSurfaceAltitudeMDatumUnspecified"),
    ],
    ["clearanceMetres", textForUI("ui.missionWorkspace.messageSimulatorClearanceAGLM")],
    ["groundSlopeDegrees", textForUI("ui.missionWorkspace.messageAlongTrackGroundSlopeDegrees")],
    ["lapseKelvinPerKilometre", textForUI("ui.missionWorkspace.messageLocalLapseKKm")],
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
          plugins: {tooltip:{callbacks:{afterLabel:c=>textForUI("ui.missionWorkspace.messageAircraftAtmosphereGround",{value1:(String(c.raw.utc)),value2:(String(c.raw.auxiliaryUtc??"unavailable")),value3:(String(c.raw.groundUtc??"unavailable"))})}}},
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
    setPresentationText(description,localizedCopy("ui.missionWorkspace.messageActualRecordedPacketsNoInterpolationAcrossGaps"));
    accessibleValues.replaceChildren();scientificTable(accessibleValues,textForUI("ui.missionWorkspace.messageRecordedProfileValues"),[x.selectedOptions[0].textContent,y.selectedOptions[0].textContent,textForUI("ui.missionWorkspace.messageAircraftUTC"),textForUI("ui.missionWorkspace.messageAtmosphereUTC"),textForUI("ui.missionWorkspace.messageGroundUTC"),textForUI("ui.missionWorkspace.messageGapBefore")],result.profile.map(p=>[format(convert(x.value,p[x.value]).value),format(convert(y.value,p[y.value]).value),p.utc,p.atmosphereUtc??textForUI("localization.unavailableValue"),p.groundUtc??textForUI("localization.unavailableValue"),p.breakBefore?textForUI("ui.missionWorkspace.messageYes"):textForUI("ui.missionTimeline.messageNo")]));
  }
  x.onchange = y.onchange = draw;
  draw();
  const observer=new MutationObserver(()=>{if(!canvas.isConnected){chart?.destroy();observer.disconnect();}});observer.observe(document.body,{childList:true,subtree:true});
  const advanced=el("details");advanced.append(el("summary",localizedCopy("ui.missionWorkspace.messageAdvancedObservedCloudEncountersAltitudeBins")));for(const method of result.methods)advanced.append(el("p",method));parent.append(advanced);
  advanced.append(el("h4", localizedCopy("ui.missionWorkspace.messageObservedCloudEncounters")));
  for (const c of result.clouds)
    advanced.append(
      el(
        "p",
        `${c.entryObserved ? "Observed entry" : "Cloud present / entry unknown"} ${c.entryUtc} · ${format(convert("altitudeMetres",c.entryAltitudeMetres).value)} ${convert("altitudeMetres",0).unit} → ${c.exitObserved ? "observed exit" : "exit not established"} ${c.exitUtc ?? ""} ${format(convert("altitudeMetres",c.exitAltitudeMetres).value)} ${convert("altitudeMetres",0).unit}`,
      ),
    );
  advanced.append(el("h4", localizedCopy("ui.missionWorkspace.messageAltitudeIntervalSummaries")));
  for (const b of result.altitudeBins || [])
    advanced.append(
      el(
        "p",
        localizedCopy("ui.missionWorkspace.messageIndependentPackets",{value1:(String(b.direction)),value2:(String(format(convert("altitudeMetres",b.minimumAltitudeMetres).value))),value3:(String(format(convert("altitudeMetres",b.maximumAltitudeMetres).value))),value4:(String(convert("altitudeMetres",0).unit)),value5:(String(b.samples)),value6:(String(format(convert("temperatureCelsius",b.temperatureMeanCelsius).value))),value7:(String(convert("temperatureCelsius",0).unit)),value8:(String(format(convert("windKnots",b.windMeanKnots).value))),value9:(String(convert("windKnots",0).unit))}),
      ),
    );
}
export function exports(parent, id) {
  parent.append(
    el("h3", localizedCopy("ui.missionWorkspace.messageExportResearch")),
    el(
      "p",
      localizedCopy("ui.missionWorkspace.messageTheResearchZIPIncludesRecordedTelemetryAuxiliary"),
    ),
  );
  link(
    parent,
    localizedCopy("ui.missionWorkspace.messageDownloadResearchPackageZIP"),
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
