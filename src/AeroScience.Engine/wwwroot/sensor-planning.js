import { planningField, planningDistance } from "./science-display.js";
const e = (tag, text) => {
  const n = document.createElement(tag);
  if (text) n.textContent = text;
  return n;
};
export function sensorPlanning(
  parent,
  { api, instruments, definition, apply, preview, savedSettings },
) {
  const box = e("details");
  box.className = "sensor-planning";
  box.append(e("summary", "Plan spacing from sensor geometry"));
  parent.append(box);
  const label = e("label", "Instrument"),
    instrument = e("select");
  instrument.setAttribute("aria-label", "Planning instrument");
  label.append(instrument);
  box.append(label);
  for (const [id, name] of [
    ["multispectral-imager", "Multispectral camera"],
    ["thermal-imager", "Thermal camera"],
    ["hyperspectral-imager", "Hyperspectral"],
    ["terrain-scanner", "Terrain scanner"],
  ]) {
    const o = e("option", name);
    o.value = id;
    instrument.append(o);
  }
  if(savedSettings)instrument.value=savedSettings.instrumentId;
  const field = (name, value, quantity, suffix = "") => {
    const l = e("label", name),
      i = e("input");
    i.type = "number";
    i.step = "any";
    i.value = value;
    l.append(i);
    box.append(l);
    if (quantity) planningField(i, quantity, value, name, suffix);
    else i.setAttribute("aria-label", name);
    return i;
  };
  const altitude = field(
      "Survey altitude above ground",
      (savedSettings?.altitudeAglMetres??304.8)/.3048,
      "altitude",
      "AGL",
    ),
    speed = field("Groundspeed", (savedSettings?.groundspeedMetresPerSecond??100*1852/3600)*3600/1852, "speed"),
    side = field("Side overlap (%)", (savedSettings?.sideOverlap??.3)*100),
    forward = field("Forward overlap (%)", (savedSettings?.forwardOverlap??.6)*100);
  side.min = forward.min = 0;
  side.max = forward.max = 98;
  let canonicalAgl=savedSettings?.altitudeAglMetres??304.8,canonicalSpeed=savedSettings?.groundspeedMetresPerSecond??100*1852/3600;
  let lastFeet=altitude.planningUnits.read(),lastKnots=speed.planningUnits.read();
  const readAgl=()=>{const feet=altitude.planningUnits.read();if(feet!==lastFeet){canonicalAgl=feet*.3048;lastFeet=feet;}return canonicalAgl;};
  const readSpeed=()=>{const knots=speed.planningUnits.read();if(knots!==lastKnots){canonicalSpeed=knots*1852/3600;lastKnots=knots;}return canonicalSpeed;};
  const report = e(
    "p",
    "Select a payload profile under Scientific instruments, then review the geometry here. AGL and MSL are independent values.",
  );
  report.setAttribute("role", "status");
  box.append(report);
  const use = e("button", "Use recommended track spacing");
  use.type = "button";
  use.disabled = true;
  box.append(use);
  const terrainLabel = e("label", "Terrain-aware centre preview (optional)"),
    terrain = e("select");
  terrain.setAttribute("aria-label", "Terrain-aware planning source");
  const flat = e("option", "Flat-ground baseline");
  flat.value = "";
  terrain.append(flat);
  terrainLabel.append(terrain);
  box.append(terrainLabel);
  api("/api/science/sources")
    .then((items) => {
      for (const item of items.filter(
        (i) => i.filesAvailable && i.source.manifest.kind === "terrain",
      )) {
        const o = e("option", item.source.manifest.name);
        o.value = item.source.id;
        terrain.append(o);
      }
    if(savedSettings?.terrainSourceId){if(![...terrain.options].some(o=>o.value===savedSettings.terrainSourceId)){const o=e('option','Previously selected terrain source (currently unavailable)');o.value=savedSettings.terrainSourceId;terrain.append(o);}terrain.value=savedSettings.terrainSourceId;}
    })
    .catch(() => {});
  const methods = e("details");
  methods.append(e("summary", "Assumptions and source limits"));
  const detail = e("p");
  methods.append(detail);
  box.append(methods);
  let latest = null,
    generation = 0,
    timer;
  async function update() {
    const own = ++generation;
    use.disabled = true;
    let pattern;
    try {
      pattern = definition();
    } catch {
      pattern = null;
    }
    try {
      const config = instruments().find(
        (c) => c.instrumentId === instrument.value,
      );
      if (!config) throw Error("Choose a supported instrument.");
      const result = await api("/api/sensor-planning", {
        instrument: config,
        altitudeAglMetres: readAgl(),
        groundspeedMetresPerSecond:readSpeed(),
        sideOverlap: Number(side.value) / 100,
        forwardOverlap: Number(forward.value) / 100,
        pattern,
        terrainSourceId: terrain.value || null,
      });
      if (own !== generation || !box.isConnected) return;
      latest = result;
      use.disabled = false;
      report.textContent = `${result.payload.profile.name} · Modeled swath ${planningDistance(result.swathMetres)} · Recommended spacing ${planningDistance(result.trackSpacingMetres)} · Nominal cross-track GSD ${result.nominalCrossTrackGsdMetres?.toPrecision(4) ?? "unspecified"} m · ${result.pattern ? `${Math.ceil(result.pattern.waypoints.length / 2)} scan/transition segments · ${planningDistance(result.pattern.distanceMetres)} · ${(result.durationSeconds / 60).toFixed(1)} min at stated groundspeed` : "Choose a pattern center for route estimates."}${result.recommendedIntervalSeconds != null ? ` · Frame interval ≤${result.recommendedIntervalSeconds.toFixed(2)} s` : ""}${result.theoreticalPointDensityPerSquareMetre != null ? ` · Ideal modeled density ${result.theoreticalPointDensityPerSquareMetre.toFixed(2)} points/m² (not recorded returns)` : ""}`;
      if (result.terrainStatus)
        report.textContent += " · " + result.terrainStatus;
      detail.textContent = result.advisories.join(" ");
      preview?.(result);
    } catch (error) {
      if (own === generation) report.textContent = error.message;
    }
  }
  const schedule = () => {
    clearTimeout(timer);
    generation++;
    timer = setTimeout(update, 300);
  };
  let used=!!savedSettings;
  box.addEventListener("input", ()=>{used=true;schedule();});
  box.addEventListener("change", schedule);
  box.addEventListener("toggle", () => {
    if (box.open) {used=true;update();}
  });
  box.closest("form")?.addEventListener("change", (event) => {
    if (box.open && !box.contains(event.target)) schedule();
  });
  use.onclick = () => {
    if (latest) apply(latest.trackSpacingMetres);
  };
  return {settings:()=>used?{instrumentId:instrument.value,altitudeAglMetres:readAgl(),groundspeedMetresPerSecond:readSpeed(),sideOverlap:Number(side.value)/100,forwardOverlap:Number(forward.value)/100,terrainSourceId:terrain.value||null}:null};
}
