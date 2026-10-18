import { planningField, planningDistance, planningArea, planningText } from "./science-display.js";
import { areaSummary } from "./planner-geometry.js";
const element = (tag, text) => {
  const e = document.createElement(tag);
  if (text) e.textContent = text;
  return e;
};
export function patternEditor(parent, { api, notice, get, set, aircraft, pickCenter, fitPreview, changed }) {
  let preview = null,
    patterns = structuredClone(get().patterns || []),
    undo = [];
  const panel = element("details");
  panel.className = "panel research-panel pattern-editor";
  panel.open=false;
  panel.append(element("summary", "Flight pattern generator"));
  const grid = element("div");
  grid.className = "research-grid";
  panel.append(grid);
  parent.append(panel);
  const field = (name, value, type = "number", quantity = null, suffix="") => {
    const label = element("label", name),
      input = element("input");
    input.type = type;
    input.value = value ?? "";
    if (type === "number") input.step = "any";
    label.append(input);
    grid.append(label);
    if(quantity)planningField(input,quantity,value,name,suffix);
    return input;
  };
  const label = element("label", "Pattern"),
    kind = element("select");
  kind.setAttribute("aria-label","Pattern");
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
    spacing = field("Track Spacing", 1000,"number","distance"),
    length = field("Leg length", 10000,"number","distance"),
    width = field("Survey width", 5000,"number","distance"),
    radius = field("Orbit / turn radius", 2500,"number","distance"),
    legs = field("Survey passes / spokes", 6),
    orbits = field("Orbits", 1),
    alt = field("Target altitude", null,"number","altitude","optional"),
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
  const explanation=element("p");panel.append(explanation);
  function describe(){explanation.textContent=kind.value==="lawnmower"?"Lawnmower: long scan legs follow the reference heading; each turn steps sideways across it. Entry and exit are marked green and pink.":kind.value==="creepingLine"?"Creeping line: long scan legs cross the reference heading; the survey steps forward along that heading. Parallel legs are rotated 90° relative to lawnmower for the same reference heading.":kind.value==="orbit"?"Orbit: center, radius, direction and revolutions define the mission. The map shows a smooth circle with entry/exit only. Exports retain the 5° point-to-point approximation (73 points for one revolution); no native EFB orbit command is claimed.":"Preview is dashed white; committed route is gold. Parameter changes update the preview without changing the map view.";}
  describe();
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
    changed?.();
    undo.push(structuredClone({ ...get(), patterns }));
    if (undo.length > 20) undo.shift();
  }
  function apply(data) {
    clearTimeout(timer);generation++;preview=null;
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
      lengthMetres: length.planningUnits.read(),
      widthMetres: width.planningUnits.read(),
      spacingMetres: spacing.planningUnits.read(),
      radiusMetres: radius.planningUnits.read(),
      legs: Number(legs.value),
      orbits: Number(orbits.value),
      clockwise: clockwise.checked,
      targetAltitudeFeet: alt.planningUnits.read(),
      notes: notes.value,
    };
  };
  let generation = 0,
    timer;
  async function generate() {
    const id = ++generation;
    const result = await api("/api/patterns/preview", definition());
    if (id !== generation || !panel.isConnected) return null;
    preview = result;
    planningText(report,()=>{
      const d=result.definition,parallel=["lawnmower","creepingLine"].includes(d.kind);
      return `Pattern preview only: ${parallel?`${d.legs} survey passes · Track spacing ${planningDistance(d.spacingMetres)} · Approximate survey envelope ${planningArea(d.lengthMetres*(d.legs-1)*d.spacingMetres)} (sensor coverage unvalidated) · `:""}${result.waypoints.length} navigation points · ${planningDistance(result.distanceMetres)} route distance · Reference heading ${d.headingDegrees}° true. ${result.method}`;
    });
    const current = get();
    set({ ...current, preview: result, previewOnly:true });
    return result;
  }
  function schedule(){
    changed?.();
    clearTimeout(timer);generation++;preview=null;describe();set({...get(),preview:null,previewOnly:true});
    const requested=generation;
    timer=setTimeout(()=>generate().catch(error=>{if(requested+1===generation)report.textContent=error.message;}),250);
  }
  function parametersChanged(event){
    if(["lawnmower","creepingLine"].includes(kind.value)&&[spacing,width].includes(event.target)){
      const gap=spacing.planningUnits.read(),extent=width.planningUnits.read();
      if(gap>0&&extent>0)legs.value=Math.max(2,Math.ceil(extent/gap)+1);
    }
    spacing.closest("label").classList.toggle("primary-spacing",["lawnmower","creepingLine"].includes(kind.value));
    legs.readOnly=["lawnmower","creepingLine"].includes(kind.value);
    schedule();
  }
  spacing.closest("label").classList.add("primary-spacing");legs.readOnly=true;
  grid.addEventListener("input",parametersChanged);grid.addEventListener("change",parametersChanged);
  action("Choose center on map",()=>pickCenter?.(point=>{lat.value=point.latitude;lon.value=point.longitude;return generate().catch(e=>report.textContent=e.message);}));
  const areaChoice=element("select");areaChoice.setAttribute("aria-label","Pattern survey area");panel.append(areaChoice);
  function refreshAreas(){const previous=areaChoice.value;areaChoice.replaceChildren();for(const [i,area] of (get().surveyAreas||[]).entries()){const option=element("option",area.name);option.value=i;areaChoice.append(option);}if(previous)areaChoice.value=previous;}
  refreshAreas();panel.addEventListener("toggle",()=>{if(panel.open)refreshAreas();});
  action("Fit Pattern to Survey Area",async()=>{
    refreshAreas();const area=get().surveyAreas[Number(areaChoice.value)];if(!area)throw Error("Draw and Save Survey Area first, then select it here.");
    const summary=areaSummary(area.vertices);if(summary.error)throw Error(summary.error);
    const id=++generation;clearTimeout(timer);
    const input=definitionForFit(summary.center);
    const fitted=await api("/api/patterns/fit-survey",{definition:input,area});
    if(id!==generation||!panel.isConnected)return;
    lat.value=fitted.center.latitude;lon.value=fitted.center.longitude;
    length.planningUnits.set(fitted.lengthMetres);width.planningUnits.set(fitted.widthMetres);legs.value=fitted.legs;
    changed?.();await generate();
    explanation.textContent="Fit uses the heading-aligned local geodesic envelope and your entered Track Spacing. Passes may extend beyond the polygon; routes are not clipped. Sensor swath/overlap and complete scientific coverage are unvalidated.";
  });
  function definitionForFit(center){
    const previousLat=lat.value,previousLon=lon.value;
    if(lat.value===""||lon.value===""){lat.value=center.latitude;lon.value=center.longitude;}
    try{return definition();}finally{lat.value=previousLat;lon.value=previousLon;}
  }
  action("Fit Preview",()=>{if(!preview?.waypoints)throw Error("Choose a center and wait for a valid preview.");fitPreview?.(preview.waypoints);});
  action("Discard Preview",()=>{clearTimeout(timer);generation++;preview=null;set({...get(),preview:null,previewOnly:true});report.textContent="Pattern preview discarded; committed plan geometry is unchanged.";});
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
  action("Add Pattern to Mission", async () => {
    clearTimeout(timer);
    const generated = await generate();
    if(!generated)throw Error("Parameters changed before the preview completed. Wait for the current preview.");
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
            length.planningUnits.set(d.lengthMetres);
            width.planningUnits.set(d.widthMetres);
            spacing.planningUnits.set(d.spacingMetres);
            radius.planningUnits.set(d.radiusMetres);
            legs.value = d.legs;
            orbits.value = d.orbits;
            alt.planningUnits.set(d.targetAltitudeFeet);
            notes.value = d.notes ?? "";
            clockwise.checked = d.clockwise;
            describe();generate().catch(e=>report.textContent=e.message);
            report.textContent =
              "Editing saved parameters. Regenerate replaces only this pattern’s tagged points; manual edits to those points will be replaced.";
          },
        ],
        [
          "Regenerate",
          async () => {
            const id=++generation;
            const result = await api(
              "/api/patterns/preview",
              preview?.definition.id === d.id ? definition() : d,
            );
            if(id!==generation||!panel.isConnected)return;
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
  return { getPatterns: () => patterns, remember, refreshAreas, hasPreview:()=>!!preview?.waypoints };
}
