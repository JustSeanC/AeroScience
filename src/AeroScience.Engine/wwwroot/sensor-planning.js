import {message as localizedCopy,setPresentationText} from './i18n.js';
import {t as textForUI} from './i18n.js';
import { planningField, planningDistance } from "./science-display.js";
const e = (tag, text) => {
  const n = document.createElement(tag);
  if (text) setPresentationText(n,text);
  return n;
};
export function sensorPlanning(
  parent,
  { api, instruments, definition, apply, preview, savedSettings },
) {
  const box = e("details");
  box.className = "sensor-planning";
  box.append(e("summary", localizedCopy("ui.sensorPlanning.messagePlanSpacingFromSensorGeometry")));
  parent.append(box);
  const label = e("label", localizedCopy("ui.missionOperations.messageInstrument")),
    instrument = e("select");
  setPresentationText(instrument,localizedCopy("ui.sensorPlanning.messagePlanningInstrument"),"aria-label");
  label.append(instrument);
  box.append(label);
  for (const [id, name] of [
    ["multispectral-imager", textForUI("ui.sensorPlanning.messageMultispectralCamera")],
    ["thermal-imager", textForUI("ui.sensorPlanning.messageThermalCamera")],
    ["hyperspectral-imager", textForUI("ui.missionWorkspace.messageHyperspectral")],
    ["terrain-scanner", textForUI("ui.sensorPlanning.messageTerrainScanner")],
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
    else setPresentationText(i,name,"aria-label");
    return i;
  };
  const altitude = field(
      localizedCopy("ui.sensorPlanning.messageSurveyAltitudeAboveGround"),
      (savedSettings?.altitudeAglMetres??304.8)/.3048,
      "altitude",
      "AGL",
    ),
    speed = field(localizedCopy("ui.missionWorkspace.messageGroundspeed"), (savedSettings?.groundspeedMetresPerSecond??100*1852/3600)*3600/1852, "speed"),
    side = field(localizedCopy("ui.sensorPlanning.messageSideOverlap"), (savedSettings?.sideOverlap??.3)*100),
    forward = field(localizedCopy("ui.sensorPlanning.messageForwardOverlap"), (savedSettings?.forwardOverlap??.6)*100);
  side.min = forward.min = 0;
  side.max = forward.max = 98;
  let canonicalAgl=savedSettings?.altitudeAglMetres??304.8,canonicalSpeed=savedSettings?.groundspeedMetresPerSecond??100*1852/3600;
  let lastFeet=altitude.planningUnits.read(),lastKnots=speed.planningUnits.read();
  const readAgl=()=>{const feet=altitude.planningUnits.read();if(feet!==lastFeet){canonicalAgl=feet*.3048;lastFeet=feet;}return canonicalAgl;};
  const readSpeed=()=>{const knots=speed.planningUnits.read();if(knots!==lastKnots){canonicalSpeed=knots*1852/3600;lastKnots=knots;}return canonicalSpeed;};
  const report = e(
    "p",
    localizedCopy("ui.sensorPlanning.messageSelectAPayloadProfileUnderScientificInstruments"),
  );
  report.setAttribute("role", "status");
  box.append(report);
  const use = e("button", localizedCopy("ui.sensorPlanning.messageUseRecommendedTrackSpacing"));
  use.type = "button";
  use.disabled = true;
  box.append(use);
  const terrainLabel = e("label", localizedCopy("ui.sensorPlanning.messageTerrainAwareCentrePreviewOptional")),
    terrain = e("select");
  setPresentationText(terrain,localizedCopy("ui.sensorPlanning.messageTerrainAwarePlanningSource"),"aria-label");
  const flat = e("option", localizedCopy("ui.sensorPlanning.messageFlatGroundBaseline"));
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
    if(savedSettings?.terrainSourceId){if(![...terrain.options].some(o=>o.value===savedSettings.terrainSourceId)){const o=e('option',localizedCopy("ui.sensorPlanning.messagePreviouslySelectedTerrainSourceCurrentlyUnavailable"));o.value=savedSettings.terrainSourceId;terrain.append(o);}terrain.value=savedSettings.terrainSourceId;}
    })
    .catch(() => {});
  const methods = e("details");
  methods.append(e("summary", localizedCopy("ui.sensorPlanning.messageAssumptionsAndSourceLimits")));
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
      if (!config) throw Error(textForUI("ui.sensorPlanning.messageChooseASupportedInstrument"));
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
      setPresentationText(report,localizedCopy("ui.sensorPlanning.messageModeledSwathRecommendedSpacing",{value1:(String(result.payload.profile.name)),value2:(String(planningDistance(result.swathMetres))),value3:(String(planningDistance(result.trackSpacingMetres))),value4:(String(result.nominalCrossTrackGsdMetres?.toPrecision(4) ?? "unspecified")),value5:(String(result.pattern ? `${Math.ceil(result.pattern.waypoints.length / 2)} scan/transition segments · ${planningDistance(result.pattern.distanceMetres)} · ${(result.durationSeconds / 60).toFixed(1)} min at stated groundspeed` : "Choose a pattern center for route estimates.")),value6:(String(result.recommendedIntervalSeconds != null ? ` · Frame interval ≤${result.recommendedIntervalSeconds.toFixed(2)} s` : "")),value7:(String(result.theoreticalPointDensityPerSquareMetre != null ? ` · Ideal modeled density ${result.theoreticalPointDensityPerSquareMetre.toFixed(2)} points/m² (not recorded returns)` : ""))}));
      if (result.terrainStatus)
        report.textContent += " · " + result.terrainStatus;
      setPresentationText(detail,result.advisories.join(" "));
      preview?.(result);
    } catch (error) {
      if (own === generation) setPresentationText(report,error.message);
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
