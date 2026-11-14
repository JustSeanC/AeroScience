import {message as localizedCopy,setPresentationText} from './i18n.js';
import {t as textForUI} from './i18n.js';
import { newIdentity } from "./workstation-ui.js";
import { planningField, planningDistance, planningArea, planningText,planningMeasure,onUnitsChanged } from "./science-display.js";
import { areaSummary } from "./planner-geometry.js";
import { sensorPlanning } from "./sensor-planning.js";
const element = (tag, text) => {
  const e = document.createElement(tag);
  if (text) setPresentationText(e,text);
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
  panel.append(element("summary", localizedCopy("ui.patterns.messageFlightPatternGenerator")));
  const grid = element("div");
  grid.className = "research-grid";
  panel.append(grid);
  parent.append(panel);
  const field = (name, value, type = "number", quantity = null, suffix="") => {
    const label = element("label", name),
      input = element("input");
    input.type = type;setPresentationText(input,name,"aria-label");
    input.value = value ?? "";
    if (type === "number") input.step = "any";
    label.append(input);
    grid.append(label);
    if(quantity)planningField(input,quantity,value,name,suffix);
    return input;
  };
  const label = element("label", localizedCopy("ui.patterns.messagePattern")),
    kind = element("select");
  setPresentationText(kind,localizedCopy("ui.patterns.messagePattern"),"aria-label");
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
  const lat = field(localizedCopy("ui.patterns.messageCenterStartLatitude"), null),
    lon = field(localizedCopy("ui.patterns.messageCenterStartLongitude"), null),
    heading = field(localizedCopy("ui.patterns.messageHeadingDegreesTrue"), 0),
    spacing = field(localizedCopy("ui.patterns.messageTrackSpacingd5591605"), 1000,"number","distance"),
    length = field(localizedCopy("ui.patterns.messageLegLength"), 10000,"number","distance"),
    width = field(localizedCopy("ui.patterns.messageSurveyWidth"), 5000,"number","distance"),
    radius = field(localizedCopy("ui.patterns.messageOrbitTurnRadius"), 2500,"number","distance"),
    legs = field(localizedCopy("ui.patterns.messageSurveyPassesSpokes"), 6),
    orbits = field(localizedCopy("ui.patterns.messageOrbits"), 1),
    alt = field(localizedCopy("ui.research.messageTargetAltitude"), null,"number","altitude","optional"),
    notes = field(localizedCopy("ui.patterns.messagePatternNotes"), "", "text");
  const passes=field(localizedCopy("ui.patterns.messageNumberOfIdenticalTransectPasses"),1);passes.min=1;passes.max=20;passes.step=1;
  const passAltitudes=field(localizedCopy("ui.patterns.messagePerPassTargetAltitudesCommaSeparatedOptional72c31d3b"),"","text");
  const targetSpeed=field(localizedCopy("ui.patterns.messageTransectTargetGroundspeed"),null,"number","speed","optional");
  let passAltitudeValues=null,passAltitudeScale=1,renderedPassAltitudes="";
  function readPassAltitudes(){if(passAltitudes.value!==renderedPassAltitudes){passAltitudeValues=passAltitudes.value.trim()?passAltitudes.value.split(",").map(v=>v.trim()?Number(v)/passAltitudeScale:null):null;renderedPassAltitudes=passAltitudes.value;}return passAltitudeValues;}
  function refreshPassAltitudes(){readPassAltitudes();const units=planningMeasure("altitude",0);passAltitudeScale=units.scale;const title=textForUI("ui.patterns.messagePerPassTargetAltitudesCommaSeparatedOptional",{value1:(String(units.unit))});setPresentationText(passAltitudes.closest("label").firstChild,title);setPresentationText(passAltitudes,title,"aria-label");passAltitudes.value=(passAltitudeValues||[]).map(v=>v==null?"":String(Number((v*passAltitudeScale).toFixed(6)))).join(", ");renderedPassAltitudes=passAltitudes.value;}
  refreshPassAltitudes();const stopPassUnits=onUnitsChanged(refreshPassAltitudes);const passObserver=new MutationObserver(()=>{if(!panel.isConnected){stopPassUnits();passObserver.disconnect();}});passObserver.observe(document.body,{childList:true,subtree:true});

  const clockwise = element("input");
  clockwise.type = "checkbox";
  clockwise.checked = true;
  const direction = element("label", localizedCopy("ui.patterns.messageClockwise"));
  direction.prepend(clockwise);
  grid.append(direction);
  const report = element("p"),
    saved = element("div");
  panel.append(report, saved);
  const help=element("details"),explanation=element("p");help.append(element("summary",localizedCopy("ui.patterns.messagePatternHelpAndLimitations")),explanation);panel.append(help);
  function describe(){setPresentationText(explanation,kind.value==="lawnmower"?localizedCopy("ui.patterns.messageLawnmowerLongScanLegsFollowTheReference"):kind.value==="creepingLine"?localizedCopy("ui.patterns.messageCreepingLineLongScanLegsCrossThe"):kind.value==="orbit"?localizedCopy("ui.patterns.messageOrbitCenterRadiusDirectionAndRevolutionsDefine"):localizedCopy("ui.patterns.messagePreviewIsDashedWhiteCommittedRouteIs"));}
  const fieldErrors=new Map();
  for(const control of [lat,lon,heading,spacing,length,width,radius,legs,orbits,alt]){const error=element("span");error.className="field-error";error.id="pattern-error-"+Math.random().toString(36).slice(2);control.setAttribute("aria-describedby",error.id);control.closest("label").append(error);fieldErrors.set(control,error);}
  function visibility(){targetSpeed.closest("label").hidden=passes.closest("label").hidden=passAltitudes.closest("label").hidden=kind.value!=="straightTransect";const parallel=["lawnmower","creepingLine"].includes(kind.value);for(const [control,visible] of [[spacing,parallel||kind.value==="expandingSquare"],[width,parallel||kind.value==="box"],[length,! ["orbit","expandingSquare","radialSpokes"].includes(kind.value)],[radius,["orbit","racetrack","radialSpokes"].includes(kind.value)],[legs,parallel||["expandingSquare","radialSpokes"].includes(kind.value)],[orbits,["orbit","racetrack"].includes(kind.value)]])control.closest("label").hidden=!visible;direction.hidden=!["orbit","racetrack","expandingSquare","radialSpokes","box"].includes(kind.value);}
  function validate(){let valid=true;for(const [input,error] of fieldErrors){setPresentationText(error,"");input.removeAttribute("aria-invalid");}
    const fail=(input,message)=>{valid=false;input.setAttribute("aria-invalid","true");setPresentationText(fieldErrors.get(input),message);};
    if(lat.value===""||lon.value===""){setPresentationText(report,preview?localizedCopy("ui.patterns.messagePreviewOutdatedChooseACenterOn"):localizedCopy("ui.patterns.messageChooseACenterOnTheMapTo"));return false;}
    if(!Number.isFinite(Number(lat.value))||Number(lat.value)<-90||Number(lat.value)>90)fail(lat,localizedCopy("ui.patterns.messageLatitudeMustBeBetween90And90"));
    if(!Number.isFinite(Number(lon.value))||Number(lon.value)<-180||Number(lon.value)>180)fail(lon,localizedCopy("ui.patterns.messageLongitudeMustBeBetween180And180"));
    if(heading.value===""||!Number.isFinite(Number(heading.value)))fail(heading,localizedCopy("ui.patterns.messageEnterAReferenceHeadingInDegreesTrue"));
    for(const [input,name] of [[spacing,textForUI("ui.patterns.messageTrackSpacing")],[length,textForUI("ui.patterns.messageLegLength")],[width,textForUI("ui.patterns.messageSurveyWidth")],[radius,textForUI("ui.patterns.messageRadius")]])if(!input.closest("label").hidden){const v=input.planningUnits.read();if(!Number.isFinite(v)||v<10||v>500000)fail(input,localizedCopy("ui.patterns.messageMustBeBetweenAnd",{value1:(String(name)),value2:(String(planningDistance(10))),value3:(String(planningDistance(500000)))}));}
    if(!legs.closest("label").hidden&&(!Number.isInteger(Number(legs.value))||Number(legs.value)<2||Number(legs.value)>100))fail(["lawnmower","creepingLine"].includes(kind.value)?width:legs,["lawnmower","creepingLine"].includes(kind.value)?localizedCopy("ui.patterns.messageSurveyWidthIsTooLargeForThis"):localizedCopy("ui.patterns.messagePassesSpokesMustBeBetween2"));
    if(["orbit","racetrack"].includes(kind.value)&&(!Number.isInteger(Number(orbits.value))||Number(orbits.value)<1||Number(orbits.value)>10))fail(orbits,localizedCopy("ui.patterns.messageOrbitCountMustBeBetween1And"));
    if(!valid)setPresentationText(report,preview?localizedCopy("ui.patterns.messagePreviewOutdatedCorrectTheHighlightedFields"):localizedCopy("ui.patterns.messageCorrectTheHighlightedFieldsToPreview"));
    return valid;
  }
  describe();visibility();setPresentationText(report,localizedCopy("ui.patterns.messageChooseACenterOnTheMapTo"));
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
        textForUI("ui.patterns.messageChooseAGeographicCenterOrEnterCoordinates"),
      );
    return {
      id: preview?.definition.id || newIdentity(),
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
      return textForUI("ui.patterns.messagePreviewReadyPatternPreviewOnlyNavigationPoints",{value1:(String(parallel&&d.fitMode==="polygon"?`${result.segments.filter(s=>s.role==="scan").length} in-area scan segments / ${result.segments.filter(s=>s.role==="transition").length} transitions · ${planningDistance(result.scanDistanceMetres)} in-area scan / ${planningDistance(result.transitionDistanceMetres)} transition distance (may leave area; turning feasibility unvalidated) · `:parallel?`${d.legs} survey passes · Track spacing ${planningDistance(d.spacingMetres)} · Approximate survey envelope ${planningArea(d.lengthMetres*(d.legs-1)*d.spacingMetres)} (sensor coverage unvalidated) · `:"")),value2:(String(d.kind==="straightTransect"?`${d.passes||1} exact same-line passes · pilot-managed turnaround distance/time unknown · `:"")),value3:(String(result.waypoints.length)),value4:(String(planningDistance(result.distanceMetres))),value5:(String(d.headingDegrees)),value6:(String(result.definition.kind==="orbit"?"Orbit export uses a 5° point-to-point approximation; native EFB orbit support is not claimed.":""))});
    });
    const current = get();
    set({ ...current, preview: result, previewOnly:true });
    return result;
  }
  function schedule(){
    changed?.();
    clearTimeout(timer);generation++;describe();visibility();
    if(preview)setPresentationText(report,localizedCopy("ui.patterns.messagePreviewOutdatedRecalculating"));
    const requested=generation;
    timer=setTimeout(()=>generate().catch(error=>{if(requested+1===generation)setPresentationText(report,error.message);}),250);
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
  action(localizedCopy("ui.patterns.messageChooseCenterOnMap"),()=>pickCenter?.(point=>{lat.value=point.latitude;lon.value=point.longitude;return generate().catch(e=>setPresentationText(report,e.message));}));
  const areaChoice=element("select");setPresentationText(areaChoice,localizedCopy("ui.patterns.messagePatternSurveyArea"),"aria-label");panel.append(areaChoice);
  function refreshAreas(){const previous=areaChoice.value;areaChoice.replaceChildren();for(const [i,area] of (get().surveyAreas||[]).entries()){const option=element("option",area.name);option.value=i;areaChoice.append(option);}if(previous)areaChoice.value=previous;}
  refreshAreas();panel.addEventListener("toggle",()=>{if(panel.open)refreshAreas();});
  const fitChoice=element("select");setPresentationText(fitChoice,localizedCopy("ui.patterns.messageSurveyFitMethod"),"aria-label");for(const [value,title] of [["polygon",textForUI("ui.patterns.messagePolygonScanLegsDefault")],["envelope",textForUI("ui.patterns.messageRectangularEnvelopeOverscan")]]){const option=element("option",title);option.value=value;fitChoice.append(option);}panel.append(fitChoice);
  action(localizedCopy("ui.patterns.messageFitPatternToSurveyArea"),async()=>{
    refreshAreas();const area=get().surveyAreas[Number(areaChoice.value)];if(!area)throw Error(textForUI("ui.patterns.messageDrawAndSaveSurveyAreaFirstThen"));
    const summary=areaSummary(area.vertices);if(summary.error)throw Error(summary.error);
    const id=++generation;clearTimeout(timer);
    const input=definitionForFit(summary.center);
    const fitted=await api("/api/patterns/fit-survey",{definition:input,area,fitMode:fitChoice.value});
    if(id!==generation||!panel.isConnected)return;
    fitMode=fitted.fitMode;clippingPolygon=fitted.clippingPolygon;
    lat.value=fitted.center.latitude;lon.value=fitted.center.longitude;
    length.planningUnits.set(fitted.lengthMetres);width.planningUnits.set(fitted.widthMetres);legs.value=fitted.legs;
    changed?.();await generate();
    setPresentationText(explanation,fitMode==="polygon"?localizedCopy("ui.patterns.messageGreenScanSegmentsAreClippedToThe"):localizedCopy("ui.patterns.messageRectangularEnvelopeOverscanIsSelectedScanLegs"));
  });
  function definitionForFit(center){
    const previousLat=lat.value,previousLon=lon.value;
    if(lat.value===""||lon.value===""){lat.value=center.latitude;lon.value=center.longitude;}
    try{return definition();}finally{lat.value=previousLat;lon.value=previousLon;}
  }
  action(localizedCopy("ui.patterns.messageFitPreview"),()=>{if(!preview?.waypoints)throw Error(textForUI("ui.patterns.messageChooseACenterAndWaitForA"));fitPreview?.(preview.waypoints);});
  action(localizedCopy("ui.patterns.messageDiscardPreview"),()=>{clearTimeout(timer);generation++;preview=null;set({...get(),preview:null,previewOnly:true});setPresentationText(report,localizedCopy("ui.patterns.messagePatternPreviewDiscardedCommittedPlanGeometryIs"));});
  action(localizedCopy("ui.patterns.messageUseLiveAircraftPosition"), () => {
    const p = aircraft();
    if (!Number.isFinite(p?.latitude))
      throw Error(
        textForUI("ui.patterns.messageLiveAircraftPositionUnavailableEnterCoordinatesOr"),
      );
    lat.value = p.latitude;
    lon.value = p.longitude;
    return generate();
  });
  action(localizedCopy("ui.patterns.messageUseFirstWaypoint"), () => {
    const p = get().waypoints[0];
    if (!p) throw Error(textForUI("ui.patterns.messageAddOrSelectAWaypointFirst"));
    lat.value = p.latitude;
    lon.value = p.longitude;
    return generate();
  });
  action(localizedCopy("ui.patterns.messagePreviewPattern"), generate);
  const add=action(localizedCopy("ui.patterns.messageAddPatternToMission"),async()=>{
    if(adding)return;adding=true;add.disabled=true;
    try{
      clearTimeout(timer);
      if(!validate())return;
      if(lastAddedSignature===signature(definition())){setPresentationText(report,localizedCopy("ui.patterns.messageThisPatternIsAlreadyAddedChangeIts"));return;}
      let generated=preview&&previewSignature===signature(definition())?preview:await generate();
      if(!generated){setPresentationText(report,localizedCopy("ui.patterns.messageParametersChangedCorrectTheFieldsAndWait"));return;}
      if(previewSignature!==signature(definition())){setPresentationText(report,localizedCopy("ui.patterns.messageParametersChangedWhilePreparingWaitForThe"));return;}
      const committedSignature=signature(definition());generation++;remember();
      // One validated preview has one pattern ID. Rapid clicks cannot duplicate it.
      if(patterns.some(p=>p.id===generated.definition.id)){setPresentationText(report,localizedCopy("ui.patterns.messageThisPatternIsAlreadyInTheDraft"));return;}
      apply({...get(),waypoints:[...get().waypoints,...generated.waypoints],patterns:[...patterns,generated.definition],preview:[]});
      lastAddedSignature=committedSignature;preview=null;previewSignature=null;setPresentationText(report,localizedCopy("ui.patterns.messagePatternAddedToMissionDraftSaveMission"));
    }finally{adding=false;add.disabled=false;}
  });
  action(localizedCopy("ui.patterns.messageUndoGeometryChange"), () => {
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
          textForUI("ui.patterns.messageEditParameters"),
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
            describe();visibility();generate().catch(e=>setPresentationText(report,e.message));
            setPresentationText(report,localizedCopy("ui.patterns.messageEditingSavedParametersRegenerateReplacesOnlyThis"));
          },
        ],
        [
          textForUI("ui.patterns.messageRegenerate"),
          async () => {
            if(regenerating)return;regenerating=true;clearTimeout(timer);
            try{
              let result;
              if(preview?.definition.id===d.id){if(!validate())return;result=previewSignature===signature(definition())?preview:await generate();}
              else{const own=++generation;result=await api("/api/patterns/preview",d);if(own!==generation||!panel.isConnected){setPresentationText(report,localizedCopy("ui.patterns.messageParametersChangedWaitForTheNewestPreview"));return;}}
              if(!result){setPresentationText(report,localizedCopy("ui.patterns.messageWaitForAValidCurrentPreviewBefore"));return;}
              generation++;remember();const w=get().waypoints;const at=w.findIndex(p=>p.patternId===d.id);const kept=w.filter(p=>p.patternId!==d.id);kept.splice(at<0?kept.length:at,0,...result.waypoints);
              apply({...get(),waypoints:kept,patterns:patterns.map(p=>p.id===d.id?result.definition:p),preview:[]});setPresentationText(report,localizedCopy("ui.patterns.messagePatternUpdatedInMissionDraftSaveMission"));
            }finally{regenerating=false;}
          },
        ],
        [
          textForUI("ui.patterns.messageRemovePattern"),
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
