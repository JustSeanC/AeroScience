import { planningField, planningDistance, planningArea, planningText,planningMeasure,onUnitsChanged } from "./science-display.js";
import { areaSummary } from "./planner-geometry.js";
import { sensorPlanning } from "./sensor-planning.js";
const element = (tag, text) => {
  const e = document.createElement(tag);
  if (text) e.textContent = text;
  return e;
};
export function patternEditor(parent, { api, notice, get, set, aircraft, pickCenter, fitPreview, changed, instruments, sensorSettings }) {
  let fitMode="envelope",clippingPolygon=null;
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
    input.type = type;input.setAttribute("aria-label",name);
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
  const passes=field("Number of identical transect passes",1);passes.min=1;passes.max=20;passes.step=1;
  const passAltitudes=field("Per-pass target altitudes (comma-separated; optional)","","text");
  const targetSpeed=field("Transect target groundspeed",null,"number","speed","optional");
  let passAltitudeValues=null,passAltitudeScale=1,renderedPassAltitudes="";
  function readPassAltitudes(){if(passAltitudes.value!==renderedPassAltitudes){passAltitudeValues=passAltitudes.value.trim()?passAltitudes.value.split(",").map(v=>v.trim()?Number(v)/passAltitudeScale:null):null;renderedPassAltitudes=passAltitudes.value;}return passAltitudeValues;}
  function refreshPassAltitudes(){readPassAltitudes();const units=planningMeasure("altitude",0);passAltitudeScale=units.scale;const title=`Per-pass target altitudes (${units.unit}; comma-separated; optional)`;passAltitudes.closest("label").firstChild.textContent=title;passAltitudes.setAttribute("aria-label",title);passAltitudes.value=(passAltitudeValues||[]).map(v=>v==null?"":String(Number((v*passAltitudeScale).toFixed(6)))).join(", ");renderedPassAltitudes=passAltitudes.value;}
  refreshPassAltitudes();const stopPassUnits=onUnitsChanged(refreshPassAltitudes);const passObserver=new MutationObserver(()=>{if(!panel.isConnected){stopPassUnits();passObserver.disconnect();}});passObserver.observe(document.body,{childList:true,subtree:true});

  const clockwise = element("input");
  clockwise.type = "checkbox";
  clockwise.checked = true;
  const direction = element("label", "Clockwise");
  direction.prepend(clockwise);
  grid.append(direction);
  const report = element("p"),
    saved = element("div");
  panel.append(report, saved);
  const help=element("details"),explanation=element("p");help.append(element("summary","Pattern help and limitations"),explanation);panel.append(help);
  function describe(){explanation.textContent=kind.value==="lawnmower"?"Lawnmower: long scan legs follow the reference heading; each turn steps sideways across it. Entry and exit are marked green and pink.":kind.value==="creepingLine"?"Creeping line: long scan legs cross the reference heading; the survey steps forward along that heading. Parallel legs are rotated 90° relative to lawnmower for the same reference heading.":kind.value==="orbit"?"Orbit: center, radius, direction and revolutions define the mission. The map shows a smooth circle with entry/exit only. Exports retain the 5° point-to-point approximation (73 points for one revolution); no native EFB orbit command is claimed.":"Preview is dashed white; committed route is gold. Parameter changes update the preview without changing the map view.";}
  const fieldErrors=new Map();
  for(const control of [lat,lon,heading,spacing,length,width,radius,legs,orbits,alt]){const error=element("span");error.className="field-error";error.id="pattern-error-"+Math.random().toString(36).slice(2);control.setAttribute("aria-describedby",error.id);control.closest("label").append(error);fieldErrors.set(control,error);}
  function visibility(){targetSpeed.closest("label").hidden=passes.closest("label").hidden=passAltitudes.closest("label").hidden=kind.value!=="straightTransect";const parallel=["lawnmower","creepingLine"].includes(kind.value);for(const [control,visible] of [[spacing,parallel||kind.value==="expandingSquare"],[width,parallel||kind.value==="box"],[length,! ["orbit","expandingSquare","radialSpokes"].includes(kind.value)],[radius,["orbit","racetrack","radialSpokes"].includes(kind.value)],[legs,parallel||["expandingSquare","radialSpokes"].includes(kind.value)],[orbits,["orbit","racetrack"].includes(kind.value)]])control.closest("label").hidden=!visible;direction.hidden=!["orbit","racetrack","expandingSquare","radialSpokes","box"].includes(kind.value);}
  function validate(){let valid=true;for(const [input,error] of fieldErrors){error.textContent="";input.removeAttribute("aria-invalid");}
    const fail=(input,message)=>{valid=false;input.setAttribute("aria-invalid","true");fieldErrors.get(input).textContent=message;};
    if(lat.value===""||lon.value===""){report.textContent=preview?"Preview outdated — choose a center on the map.":"Choose a center on the map to preview your pattern.";return false;}
    if(!Number.isFinite(Number(lat.value))||Number(lat.value)<-90||Number(lat.value)>90)fail(lat,"Latitude must be between −90° and 90°.");
    if(!Number.isFinite(Number(lon.value))||Number(lon.value)<-180||Number(lon.value)>180)fail(lon,"Longitude must be between −180° and 180°.");
    if(heading.value===""||!Number.isFinite(Number(heading.value)))fail(heading,"Enter a reference heading in degrees true.");
    for(const [input,name] of [[spacing,"Track spacing"],[length,"Leg length"],[width,"Survey width"],[radius,"Radius"]])if(!input.closest("label").hidden){const v=input.planningUnits.read();if(!Number.isFinite(v)||v<10||v>500000)fail(input,`${name} must be between ${planningDistance(10)} and ${planningDistance(500000)}.`);}
    if(!legs.closest("label").hidden&&(!Number.isInteger(Number(legs.value))||Number(legs.value)<2||Number(legs.value)>100))fail(["lawnmower","creepingLine"].includes(kind.value)?width:legs,["lawnmower","creepingLine"].includes(kind.value)?"Survey width is too large for this track spacing and the maximum of 100 passes.":"Passes / spokes must be between 2 and 100.");
    if(["orbit","racetrack"].includes(kind.value)&&(!Number.isInteger(Number(orbits.value))||Number(orbits.value)<1||Number(orbits.value)>10))fail(orbits,"Orbit count must be between 1 and 10.");
    if(!valid)report.textContent=preview?"Preview outdated — correct the highlighted fields. The previous valid preview is retained.":"Correct the highlighted fields to preview.";
    return valid;
  }
  describe();visibility();report.textContent="Choose a center on the map to preview your pattern.";
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
    clearTimeout(timer);generation++;preview=null;lastAddedSignature=null;
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
      passes:kind.value==="straightTransect"?Number(passes.value):1,
      passAltitudesFeet:kind.value==="straightTransect"?readPassAltitudes():null,
      targetGroundspeedKnots:kind.value==="straightTransect"?targetSpeed.planningUnits.read():null,
      center: { latitude: Number(lat.value), longitude: Number(lon.value) },
      headingDegrees: Number(heading.value),
      lengthMetres: length.planningUnits.read()??10000,
      widthMetres: width.planningUnits.read()??5000,
      spacingMetres: spacing.planningUnits.read()??1000,
      radiusMetres: radius.planningUnits.read()??2500,
      legs: Number(legs.value)||6,
      orbits: ["orbit","racetrack"].includes(kind.value)?Number(orbits.value):1,
      clockwise: clockwise.checked,
      targetAltitudeFeet: alt.planningUnits.read(),
      notes: notes.value,
      fitMode:["lawnmower","creepingLine"].includes(kind.value)?fitMode:"envelope",clippingPolygon:["lawnmower","creepingLine"].includes(kind.value)?clippingPolygon:null,
    };
  };
  let previewSignature=null,adding=false,regenerating=false,lastAddedSignature=null;
  const signature=d=>JSON.stringify({...d,id:null});
  let generation = 0,
    timer;
  async function generate() {
    const id = ++generation;
    if(!validate())return null;
    const input=definition(),requestedSignature=signature(input);
    const result = await api("/api/patterns/preview", input);
    if (id !== generation || !panel.isConnected) return null;
    preview = result;previewSignature=requestedSignature;
    planningText(report,()=>{
      const d=result.definition,parallel=["lawnmower","creepingLine"].includes(d.kind);
      return `Preview Ready. Pattern preview only: ${parallel&&d.fitMode==="polygon"?`${result.segments.filter(s=>s.role==="scan").length} in-area scan segments / ${result.segments.filter(s=>s.role==="transition").length} transitions · ${planningDistance(result.scanDistanceMetres)} in-area scan / ${planningDistance(result.transitionDistanceMetres)} transition distance (may leave area; turning feasibility unvalidated) · `:parallel?`${d.legs} survey passes · Track spacing ${planningDistance(d.spacingMetres)} · Approximate survey envelope ${planningArea(d.lengthMetres*(d.legs-1)*d.spacingMetres)} (sensor coverage unvalidated) · `:""}${d.kind==="straightTransect"?`${d.passes||1} exact same-line passes · pilot-managed turnaround distance/time unknown · `:""}${result.waypoints.length} navigation points · ${planningDistance(result.distanceMetres)} route distance · Reference heading ${d.headingDegrees}° true. ${result.definition.kind==="orbit"?"Orbit export uses a 5° point-to-point approximation; native EFB orbit support is not claimed.":""}`;
    });
    const current = get();
    set({ ...current, preview: result, previewOnly:true });
    return result;
  }
  function schedule(){
    changed?.();
    clearTimeout(timer);generation++;describe();visibility();
    if(preview)report.textContent="Preview outdated — recalculating…";
    const requested=generation;
    timer=setTimeout(()=>generate().catch(error=>{if(requested+1===generation)report.textContent=error.message;}),250);
  }
  let lastEditKey=null;
  function parametersChanged(event){
    const key=JSON.stringify([kind.value,...[lat,lon,heading,spacing,length,width,radius,legs,orbits,alt,notes,passes,passAltitudes,targetSpeed,clockwise].map(input=>input.type==="checkbox"?input.checked:input.value)]);
    if(event.type==="change"&&key===lastEditKey)return;lastEditKey=key;
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
  const fitChoice=element("select");fitChoice.setAttribute("aria-label","Survey fit method");for(const [value,title] of [["polygon","Polygon scan legs (default)"],["envelope","Rectangular envelope overscan"]]){const option=element("option",title);option.value=value;fitChoice.append(option);}panel.append(fitChoice);
  action("Fit Pattern to Survey Area",async()=>{
    refreshAreas();const area=get().surveyAreas[Number(areaChoice.value)];if(!area)throw Error("Draw and Save Survey Area first, then select it here.");
    const summary=areaSummary(area.vertices);if(summary.error)throw Error(summary.error);
    const id=++generation;clearTimeout(timer);
    const input=definitionForFit(summary.center);
    const fitted=await api("/api/patterns/fit-survey",{definition:input,area,fitMode:fitChoice.value});
    if(id!==generation||!panel.isConnected)return;
    fitMode=fitted.fitMode;clippingPolygon=fitted.clippingPolygon;
    lat.value=fitted.center.latitude;lon.value=fitted.center.longitude;
    length.planningUnits.set(fitted.lengthMetres);width.planningUnits.set(fitted.widthMetres);legs.value=fitted.legs;
    changed?.();await generate();
    explanation.textContent=fitMode==="polygon"?"Green scan segments are clipped to the survey boundary. Orange dashed transitions can leave concave areas; no aircraft turning radius or complete sensor coverage is claimed.":"Rectangular envelope overscan is selected. Scan legs can extend outside the polygon; sensor coverage remains unvalidated.";
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
  const add=action("Add Pattern to Mission",async()=>{
    if(adding)return;adding=true;add.disabled=true;
    try{
      clearTimeout(timer);
      if(!validate())return;
      if(lastAddedSignature===signature(definition())){report.textContent="This pattern is already added. Change its parameters or use Edit / Regenerate.";return;}
      let generated=preview&&previewSignature===signature(definition())?preview:await generate();
      if(!generated){report.textContent="Parameters changed. Correct the fields and wait for Preview Ready before adding.";return;}
      if(previewSignature!==signature(definition())){report.textContent="Parameters changed while preparing. Wait for the updated preview before adding.";return;}
      const committedSignature=signature(definition());generation++;remember();
      // One validated preview has one pattern ID. Rapid clicks cannot duplicate it.
      if(patterns.some(p=>p.id===generated.definition.id)){report.textContent="This pattern is already in the draft. Use Edit / Regenerate to update it.";return;}
      apply({...get(),waypoints:[...get().waypoints,...generated.waypoints],patterns:[...patterns,generated.definition],preview:[]});
      lastAddedSignature=committedSignature;preview=null;previewSignature=null;report.textContent="Pattern added to mission draft. Save Mission Plan to keep it.";
    }finally{adding=false;add.disabled=false;}
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
            fitMode=d.fitMode||"envelope";clippingPolygon=d.clippingPolygon||null;fitChoice.value=fitMode;
            preview = { definition: d };
            kind.value = d.kind;
            passes.value=d.passes||1;passAltitudeValues=d.passAltitudesFeet||null;renderedPassAltitudes=passAltitudes.value;refreshPassAltitudes();targetSpeed.planningUnits.set(d.targetGroundspeedKnots);
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
            describe();visibility();generate().catch(e=>report.textContent=e.message);
            report.textContent =
              "Editing saved parameters. Regenerate replaces only this pattern’s tagged points; manual edits to those points will be replaced.";
          },
        ],
        [
          "Regenerate",
          async () => {
            if(regenerating)return;regenerating=true;clearTimeout(timer);
            try{
              let result;
              if(preview?.definition.id===d.id){if(!validate())return;result=previewSignature===signature(definition())?preview:await generate();}
              else{const own=++generation;result=await api("/api/patterns/preview",d);if(own!==generation||!panel.isConnected){report.textContent="Parameters changed; wait for the newest preview before regenerating.";return;}}
              if(!result){report.textContent="Wait for a valid current preview before regenerating.";return;}
              generation++;remember();const w=get().waypoints;const at=w.findIndex(p=>p.patternId===d.id);const kept=w.filter(p=>p.patternId!==d.id);kept.splice(at<0?kept.length:at,0,...result.waypoints);
              apply({...get(),waypoints:kept,patterns:patterns.map(p=>p.id===d.id?result.definition:p),preview:[]});report.textContent="Pattern updated in mission draft. Save Mission Plan to keep it.";
            }finally{regenerating=false;}
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
  const sensor=instruments?sensorPlanning(panel,{api,instruments,definition,savedSettings:sensorSettings,apply:value=>{spacing.planningUnits.set(value);schedule();},preview:result=>{if(result.pattern)set({...get(),preview:{...result.pattern,modeledSwaths:result.modeledSwaths,terrainPlanningFootprint:result.terrainPlanningFootprint},previewOnly:true});}}):null;
  return { getPatterns: () => patterns, getSensorPlanning:()=>sensor?.settings()??sensorSettings??null,remember, refreshAreas, hasPreview:()=>!!preview?.waypoints };
}
