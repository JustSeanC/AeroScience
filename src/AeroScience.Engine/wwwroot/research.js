import {message as localizedCopy,setPresentationText} from './i18n.js';
import {t as textForUI} from './i18n.js';
import {renderResearchContext} from './scientific-context.js';
import {reviewCollectedScienceDeletion} from "./collected-science-deletion.js";
import { newIdentity,informationDetails } from "./workstation-ui.js";
import {researchCardSelection,researchFlightControls,researchFlightTimeline} from './personal-research-flight.js';
import {flightRegimeFields,atmosphericFields,researchFields,researchDefinition,isAtmosphericResearch,isScienceResearch,researchCardTargets} from './personal-research-start.js';
import { missionPerformance } from "./mission-performance.js";
import { preflightReadiness } from "./preflight-readiness.js";
import { collectionPlanner } from "./collection-planner.js";
import { initializeScienceSessions } from "./science-sessions.js";
import { missionIntelligence } from "./mission-intelligence.js";
import {missionOperations} from './mission-operations.js';
import {openTfrReferences} from "./tfr-layers.js";
import {reviewPreflight} from "./download-activity.js";
import { hyperspectralCard } from "./hyperspectral-import.js";
import { areaSummary } from "./planner-geometry.js";
import { scientificReadiness } from "./scientific-access.js";
import { routeExportButton } from "./route-export.js";
import { remoteProducts } from "./remote-science.js";
import { ResearchMap } from "./map.js";
import { patternEditor } from "./patterns.js";
import { payloadSettings } from "./payload-settings.js";
import { calibrationSettings } from "./calibration-settings.js";
import { operatingAssessments } from "./operating-assessments.js";
import { missionWorkspace } from "./mission-workspace.js";
import {
  scienceCharts,
  initializeUnits,
  unitControls,
  displayReading,
  syncUnits,
  planningField, planningUnitControls, planningDistance, planningArea, planningSurvey, planningText,
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
          (r) => Number.isFinite(r.latitude) && Number.isFinite(r.longitude) && Number.isFinite(r.value) && r.quality === "valid",
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
    : textForUI("localization.unavailableValue");
const text = (tag, value, cls) => {
  const e = document.createElement(tag);
  setPresentationText(e,value);
  if (cls) e.className = cls;
  return e;
};
const button = (label, action) => {
  const b = text("button", label, "secondary");
  b.type = "button";
  b.onclick = async () => {
    b.disabled = true;
    try {
      await action(b);
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
  setPresentationText(e,label,"aria-label");
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
    const settings=informationDetails(row,localizedCopy("ui.research.messageInstrumentSettings")),interval = input(
      settings,
      localizedCopy("ui.research.messageIntervalSeconds"),
      (selected.find((c) => c.instrumentId === d.id)?.intervalMs ?? 1000) /
        1000,
      "number",
    );
    interval.min = 1;
    interval.max = 60;
    interval.step = 1;
    interval.dataset.interval = d.id;
    const cadenceHelp=text("small",localizedCopy("ui.research.messageSavedScientificAcquisitionInterval160SecondsControls"));settings.append(cadenceHelp);
    row.prepend(label);
    const config=selected.find(c=>c.instrumentId===d.id)??{instrumentId:d.id};
    row.readPayload=payloadSettings(settings,config,payloadCatalog,ctx.api);
    row.readCalibration=calibrationSettings(settings,config,calibrationCatalog,ctx.api);
    row.dataset.payloadInstrument=d.id;
    parent.append(row);
  }
}
function readConfigs(parent) {
  return definitions.map((d) => ({
    instrumentId: d.id,
    enabled: parent.querySelector(`[data-instrument="${d.id}"]`).checked,
    intervalMs:
      Number(parent.querySelector(`[data-interval="${d.id}"]`).value) * 1000,
    payload: parent.querySelector(`[data-payload-instrument="${d.id}"]`)?.readPayload?.()??null,
    calibration: parent.querySelector(`[data-payload-instrument="${d.id}"]`)?.readCalibration?.()??null,
  }));
}
export function initializeResearch(context) {
  ctx = context;
  const intelligence=missionIntelligence($("control"),ctx.api,{acceptState:data=>!state||(state.activeMission?.id||null)===(data.flight.activeMission?.id||null),onState:data=>{personalFlight?.feed(data.flight,data.researchCards);operations?.live(data);window.dispatchEvent(new CustomEvent('aeroscience-intelligence',{detail:data}));},onStale:message=>{personalFlight?.stale(message);operations?.stale(message);window.dispatchEvent(new Event('aeroscience-intelligence-stale'));}});
  missionPerformance($("control"),ctx.api,()=>state?.activeMission?.id,{live:true});
  personalFlight=researchFlightControls($("control"),ctx.api,{shared:true,onFocus:id=>operations?.focus(id),onChange:cards=>operations?.cardsChanged(cards)});
  const control = $("control"),
    guidance = text("article", undefined, "panel research-panel");
  guidance.id = "flight-guidance";
  control.append(guidance);
  const environment=text('article',undefined,"panel research-panel");environment.id='environment-readings';control.append(environment);
  const mapPanel = text("article", undefined, "panel research-panel");
  mapPanel.id = "mission-map";
  mapPanel.append(text("h2", localizedCopy("ui.research.messageMissionMap")));
  liveMap = new ResearchMap(mapPanel);
  control.querySelector(".mission-grid").after(mapPanel);
  const markerPanel = text("article", undefined, "panel research-panel");
  markerPanel.id = "observation-notes";
  markerPanel.append(text("h2", localizedCopy("ui.research.messagePilotObservations")),text("p",localizedCopy("ui.research.messageChooseACategoryAndWriteWhatYou")));
  const category = select(
    markerPanel,
    localizedCopy("ui.missionTimeline.messageCategory"),
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
  const note = input(markerPanel, localizedCopy("ui.research.messageNotes"), "", "textarea");
  note.id = "marker-note";
  note.maxLength = 4000;
  let observationRequest=null,noteRequest=null;const savedObservation=text("p");savedObservation.setAttribute("role","status");markerPanel.append(savedObservation);
  markerPanel.append(
    button("Add observation marker", async () => {
      if (!state?.activeMission) throw Error(textForUI("ui.research.messageStartAMissionFirst"));
      const id=state.activeMission.id,content={category:category.value,notes:note.value},key=JSON.stringify([id,content]);if(observationRequest?.key!==key)observationRequest={key,requestId:newIdentity()};
      setPresentationText(savedObservation,localizedCopy("ui.research.messageSavingPilotObservation"));const saved=await ctx.api(`/api/missions/${id}/markers`,{...content,requestId:observationRequest.requestId}).catch(error=>{setPresentationText(savedObservation,localizedCopy("ui.research.messagePilotObservationCouldNotBeConfirmedYour"));throw error;});
      if(state?.activeMission?.id===id&&note.value===content.notes)note.value="";observationRequest=null;setPresentationText(savedObservation,localizedCopy("ui.research.messagePilotObservationSavedFlight",{value1:(String(saved.timestampUtc)),value2:(String(saved.latitude===null?'Location unavailable; no position was invented':'Recorded location attached'))}));
      lastFetch = 0;
    }),
    button("Save in-flight note", async () => {
      if (!state?.activeMission) throw Error(textForUI("ui.research.messageStartAMissionFirst"));
      const id=state.activeMission.id,content=note.value,key=JSON.stringify([id,content]);if(noteRequest?.key!==key)noteRequest={key,requestId:newIdentity()};setPresentationText(savedObservation,localizedCopy("ui.research.messageSavingPilotInFlightNote"));
      await ctx.api(`/api/missions/${id}/notes`,{phase:"in-flight",text:content,requestId:noteRequest.requestId}).catch(error=>{setPresentationText(savedObservation,localizedCopy("ui.research.messageThePilotNoteCouldNotBeConfirmed"));throw error;});
      if(state?.activeMission?.id===id&&note.value===content)note.value="";noteRequest=null;setPresentationText(savedObservation,localizedCopy("ui.research.messagePilotInFlightNoteSavedItsLocationIs"));
    }),
  );
  control.append(markerPanel);
  scientificReadiness(control, ctx.api, liveMap).catch(() => {});
  $("instruments").append(text("div", undefined, "research-panel"));
  $("instruments").lastChild.id = "instrument-content";
  operations=missionOperations($('operations'),ctx.api,{map:liveMap,livePanels:{flight:control.querySelector('.mission-panel'),cards:$('research-flight-controls'),science:$('mission-intelligence'),sensors:$('mission-intelligence-sensors'),map:mapPanel,observations:markerPanel},refreshIntelligence:intelligence.refresh,currentSnapshot:()=>state,onRefreshMap:()=>{if(liveId&&!busy){lastFetch=0;}}});
}
let operations;
export function openMissionOperations(){return operations.open();}
export function closeMissionOperations(){operations?.close();}
function setLiveMap(data){if(!operations?.mapData(data))liveMap?.set(data);}
export async function loadResearch(page) {
  payloadCatalog ??= await ctx.api('/api/instrument-profiles');
  calibrationCatalog = await ctx.api('/api/instruments/calibration');
  if (!definitions.length) {
    [definitions, templates] = await Promise.all([
      ctx.api("/api/instruments"),
      ctx.api("/api/templates"),
    ]);
  }
  if (page === "planner") {
    await planner();
    const cardPlanId = new URLSearchParams(location.hash.split("?")[1] || "").get("researchPlan");
    if (cardPlanId) {
      const plan=editing?.id===cardPlanId?editing:await ctx.api(`/api/plans/${encodeURIComponent(cardPlanId)}`);
      if (location.hash.includes(`researchPlan=${cardPlanId}`)) {
        if(editing?.id===cardPlanId||editPlan(plan))window.dispatchEvent(new CustomEvent('aeroscience-research-plan-opened',{detail:{planId:cardPlanId}}));
        else{history.replaceState(null,'','#planner');ctx.notice(localizedCopy("ui.research.messageYourExistingMissionDraftWasKeptChoose"));}
      }
    }
  }
  if (page === "instruments") await instruments();
}
let payloadCatalog;
let personalFlight;
let calibrationCatalog;
let plannerDirty=false, pendingGeometry=false;
// Expose the existing in-memory draft for Home; never a second draft/plan store.
export function plannerDraft(){return (plannerDirty||pendingGeometry)&&$("plan-form") ? {id:editing?.id||null,name:$("plan-name")?.value||''} : null;}
window.addEventListener("beforeunload",event=>{if(plannerDirty||pendingGeometry){event.preventDefault();event.returnValue="";}});
async function planner() {
  if((plannerDirty||pendingGeometry)&&$("plan-form"))return;
  plans = await ctx.api(`/api/plans?limit=50&offset=${planOffset}`);
  const p = $("planner-content");
  p.replaceChildren(text("h2", localizedCopy("missions.savedPlans")));
  await planningUnitControls(p,ctx.api);
  await scientificReadiness(p, ctx.api);
  for (const plan of plans) {
    const row = text("div", undefined, "plan-row");
    const scopeLabel=text("label",localizedCopy("ui.research.messagePreflightPreparationScope")),scopeChoice=text("select");setPresentationText(scopeChoice,localizedCopy("ui.research.messagePreflightPreparationScopeFor",{value1:(plan.name)}),"aria-label");for(const [value,title] of [["entire",textForUI("ui.downloadActivity.messageEntireMission")],["first-half",textForUI("ui.research.messageFirstHalfOfMissionWhenRouteOrder")],["areas",textForUI("ui.downloadActivity.messageChooseResearchAreas")]]){const o=text("option",title);o.value=value;scopeChoice.append(o);}scopeLabel.append(scopeChoice);row.append(scopeLabel);
    row.append(
      text("strong", plan.name),
      text("span", `${plan.template} · ${plan.mode}`),
      button("Edit", () => editPlan(plan)),
      button("Review TFRs", opener => {openTfrReferences(ctx.api,planMap?.map,{planId:plan.id,opener}).catch(()=>ctx.notice(localizedCopy("ui.research.messageTFRReviewIsUnavailableMissionControlsRemain")));}),
      button("Prepare scientific data", async (opener) => {
        await reviewPreflight(ctx.api,plan.id,scopeChoice.value,opener);
      }),
      button("Select for Flight", async () => {
        await ctx.api("/api/mission-selection", {planId:plan.id});
        window.dispatchEvent(new Event("aeroscience-selection"));
        location.hash = "#control";
      }),
      button("Delete plan", async () => {
        if (
          confirm(
            localizedCopy("ui.research.messageDeleteThisSavedPlanRecordingSnapshotsRemain"),
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
    text("span", localizedCopy("ui.research.messagePlanPage",{value1:(String(planOffset / 50 + 1))})),
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
  if((plannerDirty||pendingGeometry)&&!confirm(localizedCopy("ui.research.messageDiscardUnsavedMissionAndGeometryEditsCancel")))return false;
  plannerDirty=false;pendingGeometry=false;
  editing = plan;
  const draftPlanId=plan?.id||newIdentity();
  waypoints = structuredClone(plan?.waypoints || []);
  areas = structuredClone(plan?.surveyAreas || []);
  const e = $("plan-editor");
  e.replaceChildren(
    text("h2", plan ? localizedCopy("ui.research.messageEditMissionPlan") : localizedCopy("ui.research.messageNewMissionPlan")),
  );
  const form = document.createElement("form");
  form.id = "plan-form";
  e.append(form);
  if (plan?.researchCard) {
    const linked = text("p", localizedCopy("ui.research.messagePersonalResearchTestCardRevision",{value1:(String(plan.researchCard.revision))}));
    const back = text("a", plan.researchStarter?localizedCopy("ui.personalResearchStart.messageBackToMyResearch"):localizedCopy("nav.myResearch")); back.href = "#my-research";
    form.append(linked, back);
  }
  const draftStatus=text("p",localizedCopy("ui.research.messageMissionPlanDraftChangesAreNot"),"planner-status");draftStatus.setAttribute("role","status");form.append(draftStatus);
  let readinessView;
  const dirty=message=>{if(!form.isConnected)return;plannerDirty=true;readinessView?.invalidate();setPresentationText(draftStatus,message||localizedCopy("ui.research.messageUnsavedChangesSaveMissionPlanWhen"));};
  form.addEventListener("input",event=>{if(!event.target.closest(".pattern-editor")&&!event.target.closest(".draft-geometry-editor"))dirty();});
  let starterSection,starterCard,starterFields,starterName,starterFeedback,loadStarter=Promise.resolve();
  const references=plan?.researchCards??(plan?.researchCard?[plan.researchCard]:[]);
  const readCards=researchCardSelection(form,ctx.api,references,()=>{dirty();if(starterSection){starterSection.hidden=!readCards().some(r=>r.id===plan.researchStarter.requestId);save.disabled=!starterSection.hidden&&!starterFields;}});
  if(plan?.researchStarter&&(plan.researchStarter.intent==='customize'||!plan.researchStarter.originalCard)&&references.some(r=>r.id===plan.researchStarter.requestId)){
    starterSection=text('section',undefined,'research-panel');starterSection.id='research-starter-targets';starterSection.append(text('h3',localizedCopy("ui.research.messageYourExperimentTargets")),text('p',localizedCopy("ui.research.messageThisIsYourOwnPreparedCardEditing")));
    const feedback=text('p',localizedCopy("ui.research.messageLoadingYourPreparedTargets"));starterFeedback=feedback;feedback.setAttribute('role','status');starterSection.append(feedback);const fields=text('div',undefined,'research-grid');starterSection.append(fields);form.append(starterSection);
    const load=()=>{loadStarter=ctx.api(`/api/research/cards/${plan.researchStarter.requestId}?revision=${readCards().find(r=>r.id===plan.researchStarter.requestId)?.revision||references[0].revision}`).then(card=>{if(!form.isConnected)return;starterCard=card;fields.replaceChildren();renderResearchContext(fields,card,{plan});starterName=input(fields,localizedCopy("ui.research.messageExperimentName"),card.name);starterName.required=true;starterName.maxLength=100;starterFields=researchFields(fields,card,{prefix:"Experiment: ",api:ctx.api});fields.onchange=event=>{if(starterCard.observationFlight&&event.target.type==='checkbox'){const id=event.target.dataset.observationInstrument;if(!id)return;const control=configuration.querySelector(`[data-instrument="${id}"]`);if(control)control.checked=event.target.checked;payloadTouched=true;showRecommended();dirty(localizedCopy("ui.research.messageOptionalSupportingInstrumentsUpdatedPilotObservationsStay"));return;}if(!starterCard.survey||event.target.type!=='checkbox')return;const selected=new Set(starterFields.read().measurements.map(m=>({airTemperature:'ambient-temperature',terrainElevation:'terrain-scanner',landSurfaceTemperature:'thermal-imager'})[m]));for(const id of ['ambient-temperature','terrain-scanner','thermal-imager']){const control=configuration.querySelector(`[data-instrument="${id}"]`);if(control)control.checked=selected.has(id);}payloadTouched=true;showRecommended();dirty(localizedCopy("ui.research.messageSelectedSurveyMeasurementsUpdatedTheirMatchingInstruments"));};setPresentationText(feedback,localizedCopy("ui.research.messageRevisionFreeFlightIsSupported",{value1:(String(card.revision)),value2:(String(researchCardTargets(card)))}));save.disabled=false;retry.hidden=true;}).catch(error=>{if(form.isConnected){setPresentationText(feedback,localizedCopy("ui.research.messageExperimentTargetsCouldNotLoad",{value1:(String(error.message))}));save.disabled=!starterSection.hidden;retry.hidden=false;}throw error;});return loadStarter;};
    const retry=button('Reload Experiment Targets',()=>load());retry.hidden=true;starterSection.append(retry);load().catch(()=>{});
  }else if(plan?.researchStarter)form.append(text('p',localizedCopy("ui.research.messageThisPlanReusesASavedExperimentRevision")));
  form.append(text("h3",localizedCopy("ui.research.message1MissionDetails")));
  const grid = text("div", undefined, "research-grid mission-details-grid");
  form.append(grid);
  const name = input(grid, localizedCopy("ui.research.messagePlanName"), plan?.name);
  name.required = true;
  name.maxLength = 100;
  name.id = "plan-name";
  const template = select(
    grid,
    localizedCopy("ui.research.messageTemplate"),
    templates.map((t) => [t.id, t.name]),
    plan?.template || "generalResearch",
  );
  const mode = select(
    grid,
    localizedCopy("ui.research.messageScienceMode"),
    [
      ["universal", localizedCopy("ui.research.messageUniversalFlyAnything")],
      ["realistic", localizedCopy("ui.research.messageRealisticAuthenticResearch")],
    ],
    plan?.mode || "universal",
  );
  const objective = input(grid, localizedCopy("ui.research.messageObjective"), plan?.objective, "textarea"),
    description = input(grid, localizedCopy("ui.research.messageDescription"), plan?.description, "textarea"),
    aircraft = input(
      grid,
      localizedCopy("ui.research.messageIntendedAircraftDescriptiveOnly"),
      plan?.intendedAircraft,
    ),
    departure = input(grid, localizedCopy("ui.research.messageDeparture"), plan?.departure),
    destination = input(grid, localizedCopy("ui.research.messageDestination"), plan?.destination);
  objective.closest("label").classList.add("mission-objective-field");
  const flightRules = select(
    grid,
    localizedCopy("ui.research.messageFlightPlanRules"),
    [
      ["VFR", localizedCopy("ui.research.messageVFR")],
      ["IFR", localizedCopy("ui.research.messageIFR")],
    ],
    plan?.flightRules || "VFR",
  );
  const cruise = input(
    grid,
    localizedCopy("ui.research.messageFlightPlanCruisingAltitudeFtMSLRequired"),
    plan?.cruisingAltitudeFeet,
    "number",
  );
  const low = input(
      grid,
      localizedCopy("ui.research.messageMinimumPlannedAltitudeFtMSL"),
      plan?.minimumAltitudeFeet,
      "number",
    ),
    high = input(
      grid,
      localizedCopy("ui.research.messageMaximumPlannedAltitudeFtMSL"),
      plan?.maximumAltitudeFeet,
      "number",
    ),
    duration = input(
      grid,
      localizedCopy("ui.research.messageEstimatedDurationMinutes"),
      plan?.estimatedDurationMinutes,
      "number",
    );
  for(const [field,title,suffix] of [[cruise,textForUI("ui.research.messageFlightPlanCruisingAltitude"),"required for PLN"],[low,textForUI("ui.research.messageMinimumPlannedAltitude"),""],[high,textForUI("ui.research.messageMaximumPlannedAltitude"),""]])planningField(field,"altitude",field.value===""?null:Number(field.value),title,suffix);
  duration.min = 1;
  duration.max = 43200;
  const notes = input(
    form,
    localizedCopy("ui.research.messagePreflightNotes"),
    plan?.preflightNotes,
    "textarea",
  );
  notes.maxLength = 4000;
  informationDetails(form,localizedCopy("ui.research.messageScienceModesExplained"),text("p",localizedCopy("ui.research.messageRealisticModeSupportsDocumentedSensorGeometryCalibration")));
  const advanced=text("details");advanced.append(text("summary",localizedCopy("ui.research.messageAdvancedMissionDetailsExportSettings")));
  for(const field of [description,aircraft,departure,destination,flightRules,cruise,low,high,duration,notes])advanced.append(field.closest("label"));
  form.append(advanced);
  const configuration = text("div"),selectedPayload=text("p");
  form.append(configuration);
  configs(configuration, plan?.instruments || []);
  const recommended=text("section",undefined,"panel recommended-payload");setPresentationText(recommended,localizedCopy("ui.research.messageRecommendedInstrumentPayload"),"aria-label");
  const recommendations=text("div"),payloadActions=text("div",undefined,"action-row");recommended.append(recommendations,payloadActions);grid.after(recommended);
  let localSourceCandidates=null,priorTemplate=template.value,payloadTouched=!!plan;
  const externalKinds={"terrain-scanner":"terrain","thermal-imager":"thermal","multispectral-imager":"multispectral","hyperspectral-imager":"hyperspectral"};
  const payloadPurpose={
    "terrain-scanner":textForUI("ui.research.messageStudyTerrainElevationsAlongThePlannedSurvey"),
    "multispectral-imager":textForUI("ui.research.messageStudySurfaceReflectanceAndVegetationPatterns"),
    "thermal-imager":textForUI("ui.research.messageStudyHistoricalLandSurfaceTemperature"),
    "hyperspectral-imager":textForUI("ui.research.messageExamineSpectraFromCompatibleOriginalENVIObservations"),
    "ambient-temperature":textForUI("ui.research.messageRecordSimulatorReportedAmbientAirTemperature"),
    "ambient-wind":textForUI("ui.research.messageRecordSimulatorReportedWindSpeedAndDirection"),
    "ambient-pressure":textForUI("ui.research.messageRecordSimulatorReportedAmbientPressure"),
    "ambient-density":textForUI("ui.research.messageRecordSimulatorReportedAmbientAirDensity")
  };
  const sourceRequirement={
    "terrain-scanner":textForUI("ui.research.messageExternalDataSupported3DEPBareEarthDEMOr"),
    "multispectral-imager":textForUI("ui.research.messageExternalDataCompatibleSentinel2Level2AObservations"),
    "thermal-imager":textForUI("ui.research.messageExternalDataLandsatCollection2Level2Surface"),
    "hyperspectral-imager":textForUI("ui.research.messageExternalDataCompatibleOriginalENVIReflectanceRadianceCollections")
  };
  let recommendationTemplate=null,recommendationCards=[];
  function recommendationCard(c,role,parent){
    const definition=definitions.find(d=>d.id===c.instrumentId),kind=externalKinds[c.instrumentId],row=text("article",undefined,"finding-card payload-recommendation");
    row.dataset.recommendation=role;row.dataset.instrumentId=c.instrumentId;
    const included=text("p",undefined,"payload-inclusion"),profile=text("p",undefined,"payload-reference"),details=text("details"),cadence=text("p"),sources=text("p"),limits=text("p"),calibration=text("p"),reference=text("p");
    row.append(text("span",role==="primary"?localizedCopy("ui.research.messagePrimaryRecommendation"):localizedCopy("ui.research.messageOptionalAddition"),"payload-role"),text("h4",definition?.name||c.instrumentId),text("p",payloadPurpose[c.instrumentId]||definition?.description||localizedCopy("ui.research.messageReviewThisInstrumentSSupportedMeasurements")),included);
    row.append(text("p",sourceRequirement[c.instrumentId]||localizedCopy("ui.research.messageSimulatorDerivedObservationsNoExternalSatelliteDataRequired"),"payload-source"));
    if(kind)row.append(profile);
    details.append(text("summary",localizedCopy("ui.research.messageViewInstrumentDetails")),text("p",definition?.description||localizedCopy("ui.research.messageUnknownInstrumentReviewConfiguration")),cadence,calibration);
    if(kind){
      details.append(reference,sources,text("p",kind==="hyperspectral"?localizedCopy("ui.research.messageUseTheLibrarySSupportedAuthorizedAVIRISDownloadImport"):localizedCopy("ui.research.messageRetainedSourceFilesAndProviderFootprintsDo")),text("p",localizedCopy("ui.research.messageOriginalExternalObservationDatesRemainSeparateFrom")));
      const a=text("a",localizedCopy("ui.research.messageFindCompatibleExternalData"),"button secondary");a.href="#library/download?science="+kind;details.append(a);
    }
    details.append(limits);row.append(details);
    let add;
    if(role==="optional"){
      add=text("button",localizedCopy("ui.research.messageAdd",{value1:(String(definition?.name||c.instrumentId))}),"secondary");add.type="button";
      add.onclick=()=>{
        const enabled=configuration.querySelector(`[data-instrument="${c.instrumentId}"]`);
        if(!enabled)return;
        enabled.checked=true;payloadTouched=true;showRecommended();dirty(localizedCopy("ui.research.messageOptionalInstrumentEnabledInThisDraftIts"));
      };row.append(add);
    }
    parent.append(row);
    return current=>{
      const effective=current.find(v=>v.instrumentId===c.instrumentId);
      setPresentationText(included,effective?.enabled?localizedCopy("ui.research.messageEnabledInDraft"):localizedCopy("ui.research.messageNotEnabledInDraft"));
      row.dataset.enabled=String(!!effective?.enabled);
      setPresentationText(profile,localizedCopy("ui.research.messageReference",{value1:(String(effective?.payload?.profile?.name||"No reference selected"))}));
      setPresentationText(reference,localizedCopy("ui.research.messageReferenceIdentityVersionNo",{value1:(String(effective?.payload?.profile?.id||"Unknown")),value2:(String(effective?.payload?.profile?.version??"Unknown")),value3:(String(effective?.payload?.custom?"Custom modeled geometry":"Selected reference geometry"))}));
      setPresentationText(cadence,localizedCopy("ui.research.messageRecommendedAcquisitionIntervalSCurrentConfiguredInterval",{value1:(String(c.intervalMs/1000)),value2:(String(effective?.intervalMs/1000??"Unknown"))}));
      setPresentationText(calibration,localizedCopy("ui.research.messageCalibrationProvenance",{value1:(String(effective?.calibration?.name||effective?.calibration?.id||"Not supplied; no accuracy certification is implied."))}));
      const retained=localSourceCandidates?.filter(s=>s.filesAvailable&&s.source.manifest.kind===kind);
      setPresentationText(sources,localSourceCandidates===null?localizedCopy("ui.research.messageLocalSourcePresenceNotYetAssessed"):retained?.length?localizedCopy("ui.research.messageRetainedCandidateSMissionAreaSuitabilityAndValid",{value1:(String(retained.length))}):localizedCopy("ui.research.messageNoVerifiedRetainedCandidateOfThisScientific"));
      setPresentationText(limits,(effective?.payload?.profile?.limitations||[]).join(" ")||localizedCopy("ui.research.messageOperatingLimitsAreAssessedOnlyWhereDocumented"));
      if(add)add.disabled=!!effective?.enabled;
    };
  }
  function showRecommended(){
    const t=templates.find(t=>t.id===template.value),current=readConfigs(configuration),recommendedIds=new Set((t?.instruments||[]).filter(c=>c.enabled).map(c=>c.instrumentId));
    if(recommendationTemplate!==t){
      recommendationTemplate=t;recommendationCards=[];
      recommendations.replaceChildren(text("h3",localizedCopy("ui.research.messageRecommendedSciencePayload")),text("p",`${t?.name||textForUI("ui.research.messageNoTemplate")}: ${t?.objective||textForUI("ui.research.messageNoObjectiveSupplied")}`));
      const primary=text("div",undefined,"recommended-payload-grid");recommendations.append(primary);
      for(const c of(t?.instruments||[]).filter(c=>c.enabled))recommendationCards.push(recommendationCard(c,"primary",primary));
      if(!primary.childElementCount)primary.append(text("p",localizedCopy("ui.research.messageNoFixedPrimaryPayloadChooseInstrumentsFor")));
      const optional=(t?.optionalInstruments||[]).filter(c=>!recommendedIds.has(c.instrumentId));
      if(optional.length){
        const more=text("details",undefined,"recommended-optional"),list=text("div",undefined,"recommended-payload-grid");
        more.append(text("summary",localizedCopy("ui.research.messageOptionalScientificInstrumentsAdd",{value1:(String(optional.length))})),text("p",localizedCopy("ui.research.messageOptionalAdditionsAreNotEnabledAutomaticallyAtmospheric")),list);
        for(const c of optional)recommendationCards.push(recommendationCard(c,"optional",list));
        recommendations.append(more);
      }
      const caveats=text("details",undefined,"recommended-explanation");caveats.append(text("summary",localizedCopy("ui.research.messageHowRecommendationsWorkScientificLimitations")),text("p",localizedCopy("ui.research.messagePrimaryInstrumentsMatchTheTemplateSResearchObjective")),text("p",localizedCopy("ui.research.messageRecommendationsDoNotInstallHardwareOrConfirm")));recommendations.append(caveats);
      recommendations.append(text("p",undefined,"payload-additional"));
    }
    setPresentationText(selectedPayload,localizedCopy("ui.research.messageEnabledInstruments",{value1:(String(current.filter(c=>c.enabled).map(c=>definitions.find(d=>d.id===c.instrumentId)?.name||c.instrumentId).join(", ")||"None selected"))}));
    for(const update of recommendationCards)update(current);
    const extra=current.filter(c=>c.enabled&&!recommendedIds.has(c.instrumentId)),additional=recommendations.querySelector(".payload-additional");
    setPresentationText(additional,extra.length?localizedCopy("ui.research.messageAdditionalCustomInstrumentsEnabled",{value1:(String(extra.map(c=>definitions.find(d=>d.id===c.instrumentId)?.name||c.instrumentId).join(", ")))}):"");
    additional.hidden=!extra.length;
  }
  payloadActions.append(button("Apply Recommended Payload",()=>{
    const t=templates.find(t=>t.id===template.value),current=readConfigs(configuration);
    if((plan||payloadTouched||current.some(c=>c.enabled||c.payload?.custom))&&!confirm(localizedCopy("ui.research.messageReplaceThisDraftSInstrumentSettingsCadenceProfiles",{value1:(String(current.filter(c=>c.enabled).map(c=>definitions.find(d=>d.id===c.instrumentId)?.name||c.instrumentId).join(", ")))})))return;
    payloadTouched=true;configs(configuration,t.instruments);showRecommended();dirty(localizedCopy("ui.research.messageRecommendedPayloadAppliedToDraftReviewAnd"));
  }),button("Merge Recommended Instruments",()=>{
    const ids=new Set(templates.find(t=>t.id===template.value).instruments.filter(c=>c.enabled).map(c=>c.instrumentId));
    for(const id of ids){const enabled=configuration.querySelector(`[data-instrument="${id}"]`);if(enabled)enabled.checked=true;}
    payloadTouched=true;showRecommended();dirty(localizedCopy("ui.research.messageRecommendedInstrumentsEnabledInTheDraftExisting"));
  }),button("Keep Current Payload",()=>{payloadTouched=true;showRecommended();setPresentationText(draftStatus,localizedCopy("ui.research.messageCurrentPayloadRetainedTemplateChoiceAndRecommendations"));}));
  configuration.addEventListener("input",()=>{payloadTouched=true;showRecommended();});configuration.addEventListener("change",()=>{payloadTouched=true;showRecommended();});showRecommended();
  ctx.api('/api/science/sources').then(s=>{if(form.isConnected){localSourceCandidates=s;showRecommended();}}).catch(()=>{if(form.isConnected){localSourceCandidates=null;showRecommended();}});
  const presetSection = text("details");
  presetSection.append(text("summary", localizedCopy("ui.research.messageApplyAnInstrumentPreset")));
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
                localizedCopy("ui.research.messageApplySupportedInstrumentsOnly",{value1:(String(resolved.warnings.join("\n")))}),
              )
            )
              return;
            payloadTouched=true;configs(configuration, resolved.instruments);showRecommended();dirty();
          }),
        );
      if (!rows.length)
        presetSection.append(
          text("p", localizedCopy("ui.research.messageSaveAReusableConfigurationFromScienceCollection")),
        );
    })
    .catch((e) => ctx.notice(e.message));
  template.onchange = () => {
    const t = templates.find((t) => t.id === template.value);
    if(!objective.value||objective.value===templates.find(t=>t.id===priorTemplate)?.objective)objective.value = t.objective;
    priorTemplate=template.value;
    if(!plan&&!payloadTouched){configs(configuration,t.instruments);showRecommended();dirty(localizedCopy("ui.research.messageRecommendedPayloadVisiblyPrefilledForThisUntouched"));}
    else{showRecommended();dirty(localizedCopy("ui.research.messageTemplateRecommendationsUpdatedCurrentInstrumentConfigurationIs"));}
  };
  const estimate = text("p", undefined, "footnote");
  form.append(
    button("Estimate scientific storage", async () => {
      const v = await ctx.api("/api/science/estimate", {
        instruments: readConfigs(configuration),
        durationMinutes: duration.value ? Number(duration.value) : null,
      });
      setPresentationText(estimate,localizedCopy("ui.research.messageEstimatedScienceStorageAvailable",{value1:(String(v.estimatedBytes === null ? "Supply duration" : n(v.estimatedBytes / 1048576) + " MiB")),value2:(String(v.availableBytes === null ? "Unknown" : n(v.availableBytes / 1073741824) + " GiB")),value3:(String(v.capacityWarning ? " · CAPACITY WARNING" : "")),value4:(String(v.basis)),value5:(String(v.limitations.join(" ")))}));
    }),
    estimate,
  );
  const locationSection=text("section");form.append(locationSection);
  locationSection.append(text("h3", localizedCopy("ui.research.message2LocationWaypointsAndSurveyAreas")));
  const list = text("div");
  locationSection.append(list);
  const drawList = () => {
    list.replaceChildren();
    waypoints.forEach((w, i) => {
      const row = text("div", undefined, "waypoint-row");
      row.append(
        text(
          "strong",
          `${i + 1}. ${w.name}${w.navigationIdentifier?' ('+w.navigationIdentifier+')':''} · ${n(w.latitude, 6)}, ${n(w.longitude, 6)}`,
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
      const definition=patternValues?.find(d=>d.id===w.patternId);
      if(definition?.kind==="orbit") {
        let group=list.querySelector(`[data-orbit="${w.patternId}"]`);
        if(!group){group=text("details");group.dataset.orbit=w.patternId;const count=waypoints.filter(p=>p.patternId===w.patternId).length;const description=text("summary");group.append(description);planningText(description,()=>textForUI("ui.research.messageOrbitRadiusRevolutionS",{value1:(String(planningDistance(definition.radiusMetres))),value2:(String(definition.clockwise?"clockwise":"counterclockwise")),value3:(String(definition.orbits)),value4:(String(count))}));list.append(group);}
        group.append(row);
      }else list.append(row);
    });
    for (let i = 0; i < areas.length; i++) {
      const area = areas[i];
      const row = text("div");
      row.append(
        text("span", localizedCopy("ui.research.messageVertices",{value1:(String(area.name)),value2:(String(area.vertices.length))})),
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
      plan: { waypoints, surveyAreas: areas, patterns: patternValues, departure: departure.value },
      aircraft: aircraftFor(state),
    });
  };
  const editor = text("div", undefined, "research-grid draft-geometry-editor");
  locationSection.append(editor);
  let clickMode=null, areaVertices=[], selectedArea=null, selectPatternCenter=null, waypointIndex=-1;
  let patternValues = structuredClone(plan?.patterns || []);
  function canReplaceGeometry(){return !pendingGeometry||confirm(localizedCopy("ui.research.messageDiscardTheCurrentUnsavedWaypointOrSurvey"));}
  function cancelGeometry(){pendingGeometry=false;clickMode=null;areaVertices=[];planMap.interaction(null);planMap.draft();editor.replaceChildren();setPresentationText(draftStatus,plannerDirty?localizedCopy("ui.research.messageUnsavedMissionDraftChangesRemain"):localizedCopy("ui.research.messageGeometryEditCancelledSavedRouteUnchanged"));}
  function beginWaypoint(){waypointEditor(-1);}

  function waypointEditor(index, position = {}) {
    if(pendingGeometry&&!position.fromMap&&!canReplaceGeometry())return;
    waypointIndex=index;pendingGeometry=true;clickMode="waypoint";planMap?.interaction("waypoint");
    editor.replaceChildren(text("h4",index>=0?localizedCopy("ui.research.messageEditSelectedWaypoint"):localizedCopy("ui.research.messageDraftWaypointClickTheMapTo")));
    setPresentationText(draftStatus,localizedCopy("ui.research.messageDraftWaypointOnlySaveWaypointCommits"));
    const w = {...(waypoints[index] || {}),...position};
    const airport=position.airport;
    const title = input(
        editor,
        localizedCopy("ui.research.messageWaypointName"),
        w.name || `Point ${waypoints.length + 1}`,
      ),
      lat = input(editor, localizedCopy("ui.map.messageLatitudeDegreesNorth"), w.latitude, "number"),
      lon = input(editor, localizedCopy("ui.map.messageLongitudeDegreesEast"), w.longitude, "number"),
      alt = input(
        editor,
        localizedCopy("ui.research.messageTargetAltitudeFtMSLOptional"),
        w.targetAltitudeFeet,
        "number",
      ),
      instruction = input(editor, localizedCopy("ui.research.messageInstructions"), w.instructions),
      purpose = input(editor, localizedCopy("ui.research.messageWaypointObjective"), w.objective),
      navigationIdentifier = input(
        editor,
        localizedCopy("ui.research.messagePublishedNavigationIdentifierOptionalNotTheScience"),
        w.navigationIdentifier,
      );
    const airportRole=airport?select(editor,localizedCopy("ui.research.messageAirportRole"),[['waypoint',localizedCopy("ui.research.messageIntermediateWaypoint")],['departure',localizedCopy("ui.research.messageDeparture")],['destination',localizedCopy("ui.research.messageDestination")]],'waypoint'):null;
    if(airport)editor.append(text('p',localizedCopy("ui.research.messageAirportReferencePointOurAirportsSnapshotThis")));
    planningField(alt,"altitude",w.targetAltitudeFeet??null,localizedCopy("ui.research.messageTargetAltitude"),"optional");
    lat.step = lon.step = "any";lat.min=-90;lat.max=90;lon.min=-180;lon.max=180;
    const updateMarker=()=>{if(lat.value!==""&&lon.value!==""&&lat.checkValidity()&&lon.checkValidity())planMap.draft([{latitude:Number(lat.value),longitude:Number(lon.value)}]);};
    lat.addEventListener("input",updateMarker);lon.addEventListener("input",updateMarker);updateMarker();
    editor.append(
      button("Save Waypoint", () => {
        const value = {
          ...w,
          id: w.id || newIdentity(),
          navigationIdentifier: navigationIdentifier.value || null,
          name: title.value,
          latitude: Number(lat.value),
          longitude: Number(lon.value),
          targetAltitudeFeet: alt.planningUnits.read(),
          instructions: instruction.value,
          objective: purpose.value,
        };
        if (
          !lat.value ||
          !lon.value ||
          Math.abs(value.latitude) > 90 ||
          Math.abs(value.longitude) > 180
        )
          throw Error(textForUI("ui.research.messageEnterValidLatitudeAndLongitude"));
        pattern.remember();
        // The definition keeps its existing waypoint/identifier contract.
        delete value.airport;delete value.fromMap;
        if (index >= 0) waypoints[index] = value;
        else if(airportRole?.value==='departure'){waypoints.unshift(value);departure.value=value.navigationIdentifier;}
        else {waypoints.push(value);if(airportRole?.value==='destination')destination.value=value.navigationIdentifier;}
        cancelGeometry();drawList();dirty(localizedCopy("ui.research.messageWaypointSavedToDraftSaveMission"));
      }),
      button("Cancel Waypoint",cancelGeometry),
    );
  }
  function areaEditor(index = -1) {
    if(!canReplaceGeometry())return;pendingGeometry=true;clickMode=null;planMap.interaction(null);
    editor.replaceChildren(text("h4",localizedCopy("ui.research.messageAdvancedSurveyVertexEditor")));
    const area = areas[index];
    const title = input(editor, localizedCopy("ui.research.messageAreaName"), area?.name),
      vertices = input(
        editor,
        localizedCopy("ui.research.messageVerticesOneLatitudeLongitudePerLine"),
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
        const summary=areaSummary(points);if(summary.error)throw Error(summary.error);
        const value = { name: title.value, vertices: points };
        pattern.remember();
        if (index >= 0) areas[index] = value;
        else areas.push(value);
        cancelGeometry();drawList();dirty(localizedCopy("ui.research.messageSurveyAreaSavedToDraft"));
      }),button("Cancel area edit",cancelGeometry),
    );
  }
  function startSurvey(){
    if(!canReplaceGeometry())return;pendingGeometry=true;clickMode="survey";areaVertices=[];planMap.interaction("survey");planMap.draft();
    editor.replaceChildren(text("h4",localizedCopy("ui.research.messageDrawSurveyArea")),text("p",localizedCopy("ui.research.messageClickAtLeastThreePointsAroundThe")));
    const progress=text("p",localizedCopy("ui.research.message0Vertices"));progress.id="survey-draft-progress";
    editor.append(progress,button("Undo Last Vertex",()=>{areaVertices.pop();planMap.draft(areaVertices);setPresentationText(progress,localizedCopy("ui.research.messageVerticesfa6bc834",{value1:(String(areaVertices.length))})); }),button("Finish Area",()=>{
      const result=areaSummary(areaVertices);if(result.error){setPresentationText(progress,result.error);return;}
      clickMode=null;planMap.interaction(null);planMap.draft(areaVertices,true);
      const dimensions=text("p");editor.replaceChildren(text("h4",localizedCopy("ui.research.messageSurveyPolygonPreviewNotYetIn")),dimensions);planningText(dimensions,()=>planningSurvey(result));
      const name=input(editor,localizedCopy("ui.research.messageSurveyAreaName"),`Survey area ${areas.length+1}`);
      editor.append(button("Save Survey Area",()=>{if(!name.value.trim())throw Error(textForUI("ui.research.messageGiveThisSurveyAreaAName"));pattern.remember();areas.push({name:name.value.trim(),vertices:structuredClone(areaVertices)});selectedArea=areas.length-1;cancelGeometry();drawList();pattern.refreshAreas?.();dirty(localizedCopy("ui.research.messageSurveyAreaSavedToDraftGenerate"));}),button("Cancel Survey Area",cancelGeometry));
    }),button("Cancel Drawing",cancelGeometry));
    setPresentationText(draftStatus,localizedCopy("ui.research.messageDrawingSurveyAreaClickTheMap"));
  }
  locationSection.append(
    button("Add waypoint", beginWaypoint),
    button("Add airport waypoint",()=>planMap.focusAirportSearch()),
    button("Draw Survey Area", startSurvey),
  );
  const map = text("div");
  locationSection.append(map);
  planMap?.observer?.disconnect();
  planMap?.destroy();
  planMap = new ResearchMap(map, (p) => {
    if(clickMode==="waypoint"){
      const fields=editor.querySelectorAll("input");const draft=fields.length?{name:fields[0].value,targetAltitudeFeet:fields[3].planningUnits.read(),instructions:fields[4].value,objective:fields[5].value,navigationIdentifier:fields[6].value}:{};
      waypointEditor(waypointIndex,{...draft,...p,fromMap:true});planMap.draft([p]);
    }
    else if(clickMode==="survey"){areaVertices.push(p);planMap.draft(areaVertices);setPresentationText($("survey-draft-progress"),localizedCopy("ui.research.messageVerticesFinishAreaWhenReady",{value1:(String(areaVertices.length))}));}
    else if(clickMode==="pattern"){planMap.interaction(null);clickMode=null;selectPatternCenter?.(p);}
  }, {
    onAirportSelect:airport=>{if(!canReplaceGeometry())return;cancelGeometry();waypointEditor(-1,{name:airport.name,navigationIdentifier:airport.icao||airport.id||airport.iata,latitude:airport.latitude,longitude:airport.longitude,airport});},
    onWaypointSelect:index=>waypointEditor(index),
    onWaypointsChange: (value) => {
      pattern.remember();
      waypoints = value;dirty(localizedCopy("ui.research.messageWaypointMovedInThePlanDraft"));
      drawList();
    },
    onAreasChange: (value) => {
      pattern.remember();
      areas = value;dirty(localizedCopy("ui.research.messageSurveyGeometryChangedInThePlanDraft"));
      drawList();
    },
  });
  const patternSection=text("section");patternSection.append(text("h3",localizedCopy("ui.research.message3FlightPatterns")));form.append(patternSection);
  const pattern = patternEditor(patternSection, {
    instruments:()=>readConfigs(configuration),
    sensorSettings:plan?.sensorPlanning,
    changed:()=>dirty(),
    pickCenter:callback=>{if(!canReplaceGeometry())return;cancelGeometry();dirty();selectPatternCenter=callback;clickMode="pattern";planMap.interaction("pattern");setPresentationText(draftStatus,localizedCopy("ui.research.messageClickTheMapToChooseThePattern"));},
    fitPreview:points=>planMap.fit(points),
    api: ctx.api,
    notice: ctx.notice,
    aircraft: () => aircraftFor(state),
    get: () => ({ waypoints, surveyAreas: areas, patterns: patternValues }),
    set: (value) => {
      if(value.previewOnly){planMap.preview(value.preview);setPresentationText(draftStatus,localizedCopy("ui.research.messagePatternPreviewOnlyAddPatternTo"));return;}
      waypoints = value.waypoints;areas = value.surveyAreas;patternValues=value.patterns||patternValues;
      planMap.preview(null);drawList();dirty(localizedCopy("ui.research.messagePatternAddedChangedInThePlanDraft"));
    },
  });
  const advancedCoordinates=text("details");advancedCoordinates.append(text("summary",localizedCopy("ui.research.messageAdvancedEnterSurveyVerticesManually")),button("Manual survey coordinates",()=>areaEditor()));locationSection.append(advancedCoordinates);
  const scienceSection=text("section");scienceSection.append(text("h3",localizedCopy("ui.research.message4ScientificInstrumentsAndDataReadiness")),selectedPayload);informationDetails(scienceSection,localizedCopy("ui.research.messageChooseInstrumentsAcquisitionSettings"),configuration);scienceSection.append(presetSection);form.append(scienceSection);scientificReadiness(scienceSection,ctx.api).catch(()=>{});
  const waitLabel=text('label',localizedCopy("ui.research.messageWaitForMeToStartScienceCollection")),waitForScience=document.createElement('input');waitForScience.type='checkbox';waitForScience.checked=plan?.startScienceArmed===true;waitLabel.prepend(waitForScience);scienceSection.append(waitLabel);informationDetails(scienceSection,localizedCopy("ui.research.messageRecordingAndScienceSessionBehavior"),text('p',localizedCopy("ui.research.messageWhenSelectedStartRecordingLeavesScienceArmed")));
  const atmosphericSection=text('details');atmosphericSection.append(text('summary',localizedCopy("ui.research.messageAtmosphericResearchSettings")));
  const profileDirection=select(atmosphericSection,localizedCopy("ui.research.messageProfileDirection"),[['both',localizedCopy("ui.research.messageAscentAndDescent")],['ascent',localizedCopy("ui.research.messageAscent")],['descent',localizedCopy("ui.research.messageDescent")]],plan?.atmosphericSettings?.direction??'both');
  const profileInterval=input(atmosphericSection,localizedCopy("ui.research.messageAltitudeInterval"), (plan?.atmosphericSettings?.altitudeIntervalMetres??100)/.3048,'number');planningField(profileInterval,'altitude',(plan?.atmosphericSettings?.altitudeIntervalMetres??100)/.3048,localizedCopy("ui.research.messageAltitudeInterval"),'interval');
  const profileGap=input(atmosphericSection,localizedCopy("ui.research.messageMaximumContiguousGapSeconds"),plan?.atmosphericSettings?.maximumGapSeconds??5,'number');
  atmosphericSection.append(text('p',localizedCopy("ui.research.messageProfilesPreserveActualObservationTimesCloudsAre")));
  scienceSection.prepend(atmosphericSection);
  const atmosphericVisibility=()=>{atmosphericSection.hidden=!['atmosphericProfile','cloudStructureSurvey','atmosphericTransect'].includes(template.value);};template.addEventListener('change',atmosphericVisibility);atmosphericVisibility();
  const automation=collectionPlanner(form,plan?.collection,()=>({id:draftPlanId,name:name.value,waypoints,surveyAreas:areas}),ctx.api,dirty);
  form.append(text("h3",localizedCopy("ui.research.message5ReviewAndSave")),text("p",localizedCopy("ui.research.messageSaveWaypointsAndAreasToThisDraft")));
  departure.onchange = drawList;
  drawList();
  const save = text("button", localizedCopy("ui.research.messageSaveMissionPlan"), "primary");
  save.disabled=!!starterSection&&!starterFields;
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
      cruisingAltitudeFeet: cruise.planningUnits.read(),
    }),
    ctx.api,
  );
  const nullable = (e) => e.planningUnits?e.planningUnits.read():(e.value ? Number(e.value) : null);
  const readDraft = () => ({
        id: draftPlanId,
        researchCard: readCards()[0] || null,
        researchCards: readCards(),
        researchStarter:plan?.researchStarter||null,
        startScienceArmed:waitForScience.checked?true:plan?.startScienceArmed===true?false:plan?.startScienceArmed??null,
        collection: automation.read(),
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
        atmosphericSettings:atmosphericSection.hidden?(plan?.atmosphericSettings??null):{direction:profileDirection.value,altitudeIntervalMetres:profileInterval.planningUnits.read()*.3048,maximumGapSeconds:Number(profileGap.value),minimumAltitudeFeet:nullable(low),maximumAltitudeFeet:nullable(high)},
        sensorPlanning:pattern.getSensorPlanning(),
      });
  readinessView = preflightReadiness(form,readDraft,ctx.api);
  form.onsubmit = (event) => {
    event.preventDefault();
    if(pendingGeometry){setPresentationText(draftStatus,localizedCopy("ui.research.messageSaveOrCancelTheDraftWaypointSurveyArea"));editor.scrollIntoView({block:"center"});return;}
    if(pattern.hasPreview?.()){setPresentationText(draftStatus,localizedCopy("ui.research.messagePatternPreviewOnlyAddPatternToMission"));return;}
    ctx.command(save, async () => {
      form.inert=true;
      try{
        if(starterSection&&!starterSection.hidden)await loadStarter;if(!form.isConnected)return;
        const draft=readDraft();
        if(starterCard&&draft.researchCards.some(r=>r.id===starterCard.id)&&(starterName.value.trim()!==starterCard.name||JSON.stringify(starterFields.read())!==JSON.stringify(researchDefinition(starterCard)))){
          starterCard=await ctx.api('/api/research/cards',{id:starterCard.id,expectedRevision:starterCard.revision,name:starterName.value,procedure:starterCard.procedure?starterFields.read():null,flightRegime:isScienceResearch(starterCard)||starterCard.procedure?null:starterFields.read(),atmosphericProfile:starterCard.atmosphericProfile?starterFields.read():null,atmosphericTransect:starterCard.atmosphericTransect?starterFields.read():null,survey:starterCard.survey?starterFields.read():null,observationFlight:starterCard.observationFlight?starterFields.read():null,experimentalSensor:starterCard.experimentalSensor?starterFields.read():null});
          if(!form.isConnected)return;readCards.replaceReference(starterCard);draft.researchCards=readCards();draft.researchCard=draft.researchCards[0]||null;setPresentationText(starterFeedback,localizedCopy("ui.research.messageYourCardRevisionIsSavedLocallySave",{value1:(String(starterCard.revision))}));
        }
        const savedPlan=await ctx.api('/api/plans',draft);
        if(!form.isConnected)return;editing=savedPlan;plannerDirty=false;pendingGeometry=false;await planner();setPresentationText(document.querySelector('#plan-editor .planner-status'),localizedCopy("ui.research.messageMissionPlanSavedReadyToStart"));
      }finally{if(form.isConnected)form.inert=false;}
    });
  };
  return true;
}
async function instruments() {
  await initializeUnits(ctx.api);
  const p = $("instrument-content");
  p.replaceChildren();
  informationDetails(p,localizedCopy("ui.research.messageCollectionMethodsSourceTypes"),text('p',localizedCopy("ui.research.messageAtmosphericInstrumentsUseSimulatorReportedValuesAtThe")));
  const sourceLabel = text(
    "p",
    state?.connection?.source === "simulated"
      ? localizedCopy("ui.research.messageSIMULATEDTESTFIXTURENoSimulatorConnection")
      : localizedCopy("ui.research.messageSourceMSFS2024StandardSimConnectAtmosphere"),
    "source-notice",
  );
  p.append(sourceLabel);
  const operating=text('section');operating.id='operating-reports';p.append(operating);await operatingAssessments(operating,ctx.api);
  await scientificReadiness(p, ctx.api);
  const hyperspectral=informationDetails(p,localizedCopy("ui.research.messageHyperspectralSourcePreparation"));
  await hyperspectralCard(hyperspectral,ctx.api,state?.telemetry);
  await unitControls(p, ctx.api, () => instruments());
  const collection = text("div");
  collection.id = "collection-status";
  p.append(collection);
  const scienceStatus=initializeScienceSessions(ctx,collection,{sensorParent:operating});
  missionPerformance(p,ctx.api,()=>state?.activeMission?.id,{live:true});
  const presets = text("details");
  presets.append(text("summary", localizedCopy("ui.research.messageReusableInstrumentPresets")));
  p.append(presets);
  const presetList = text("div");
  presets.append(presetList);
  const presetName = input(presets, localizedCopy("ui.research.messagePresetName"), ""),
    presetDescription = input(presets, localizedCopy("ui.research.messagePresetDescription"), "");
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
              localizedCopy("ui.research.messageApplySupportedInstrumentsOnly",{value1:(String(resolved.warnings.join("\n")))}),
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
            id: newIdentity(),
            name: item.name + " copy",
          });
          await presetRows();
        }),
        button("Delete preset", async () => {
          if (
            confirm(
              localizedCopy("ui.research.messageDeleteThisReusablePresetSavedMissionSnapshots"),
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
  const savePreset=button("Save current configuration as preset", async () => {
      await ctx.api("/api/presets", {
        id: presetName.dataset.id || newIdentity(),
        name: presetName.value,
        description: presetDescription.value,
        instruments: readConfigs($("live-instrument-config")),
      });
      delete presetName.dataset.id;
      await presetRows();
    });
  savePreset.disabled=true;
  const presetLoading=text('p',localizedCopy("ui.research.messageLoadingCurrentInstrumentConfiguration"),'footnote');presetLoading.setAttribute('role','status');
  presets.append(savePreset,presetLoading);
  for (const recommendation of templates.filter((t) => t.instruments.length))
    presets.append(
      button("Recommended: " + recommendation.name, () =>
        configs($("live-instrument-config"), recommendation.instruments),
      ),
    );
  await presetRows();
  const configuration = text("div"),selectedPayload=text("p");
  configuration.id = "live-instrument-config";
  p.append(configuration);
  configs(configuration, await ctx.api('/api/instruments/configuration'));
  savePreset.disabled=false;setPresentationText(presetLoading,'');
  p.append(
    button("Apply instrument configuration", async () => {
      await ctx.api("/api/instruments", readConfigs(configuration));
      scienceStatus.invalidate();await scienceStatus.refresh();
    }),
  );
  const values = text("div");
  values.id = "instrument-values";
  p.append(values);
  const scienceMapPanel = text("details", undefined, "panel research-panel");
  scienceMapPanel.append(text("summary",localizedCopy("ui.research.messageScientificSampleLocationsExpandMapAnd")));
  scienceMapPanel.append(text("h2", localizedCopy("ui.research.messageScientificSampleLocations")),text("p",localizedCopy("ui.research.messageBlueDotsShowSuccessfulAtmosphericSamplePositions")));
  p.append(scienceMapPanel);
  scienceMap?.destroy();
  scienceMap = new ResearchMap(scienceMapPanel);
  scienceMap.set({
    track,
    markers,
    plan: livePlan || {},
    samples: samplePositions(scienceRows),
    footprints: remoteFootprints,
    aircraft: aircraftFor(state),
  });
  const chart = text("div");
  chart.id = "science-charts";
  p.append(chart);
  const advancedDatasets=text("details");advancedDatasets.append(text("summary",localizedCopy("ui.research.messageAdvancedSavedDatasets")));p.append(advancedDatasets);
  const datasetList = text("div");
  advancedDatasets.append(datasetList);
  let offset = 0;
  async function datasets() {
    const data = await ctx.api(
      `/api/science/datasets?limit=21&offset=${offset}`,
    );
    datasetList.replaceChildren();
    if (!data.available) datasetList.append(text("p", data.error));
    previousDatasets.disabled=offset===0;nextDatasets.disabled=data.datasets.length<=20;
    for (const d of data.datasets.slice(0,20)) {
      const row = text("article", undefined, "panel research-panel");
      row.append(
        text("h3", `${d.missionName} · ${d.instrumentId}`),
        text("p", localizedCopy("ui.research.messageObservations",{value1:(String(d.observationCount)),value2:(String(d.createdUtc))})),
        button("View dataset observations", async () => {
          const result = await ctx.api(
            `/api/science/datasets/${d.id}/observations?limit=1000`,
          );
          const holder = text("div");
          row.append(holder);
          showScience(
            holder,
            result.observations,
            textForUI("ui.research.messageFirst1000ObservationsContinueThroughPagesFor"),
          );
          let after = result.next;
          const more = button("View More Measurements", async () => {
            const next = await ctx.api(
              `/api/science/datasets/${d.id}/observations?after=${after}&limit=1000`,
            );
            after = next.next;more.disabled=!next.hasMore;
            showScience(
              holder,
              next.observations,
              textForUI("ui.research.messageNextPageFullSavedValues"),
            );
          });
          more.disabled=!result.hasMore;row.append(more);
        }),
      );
      datasetList.append(row);
    }
  }
  const previousDatasets=button("Browse Previous Datasets",async()=>{offset=Math.max(0,offset-20);await datasets();});
  const nextDatasets=button("View More Datasets",async()=>{offset+=20;await datasets();});
  advancedDatasets.append(previousDatasets,nextDatasets);await datasets();
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
  parent.replaceChildren(text("h2", localizedCopy("ui.research.messageFlightGuidanceReadOnly")));
  if (!g || g.quality !== "valid") {
    parent.append(text("p", localizedCopy("ui.research.messageUnavailable",{value1:(String(g ? " · " + g.quality : ""))})));
    return;
  }
  parent.append(text("p", localizedCopy("ui.research.messageAutopilot",{value1:(String(g.autopilot))})));
  const grid = text("div", undefined, "research-grid");
  for (const [label, m] of [
    [textForUI("ui.research.messageLateral"), g.lateral],
    [textForUI("ui.research.messageVertical"), g.vertical],
    [textForUI("ui.research.messageSpeed"), g.speed],
  ])
    grid.append(
      text(
        "div",
        `${label}: ${m.mode || textForUI("map.native.unavailable")}${Number.isFinite(m.target) ? " · " + n(m.target, 3) + " " + m.unit : ""}${m.armed ? textForUI("ui.research.messageArmed",{value1:(m.armed)}) : ""}${m.status ? " · " + m.status : ""}`,
      ),
    );
  parent.append(grid, text("p", `${g.timestampUtc} · ${g.source}`, "footnote"));
}
export function renderResearch(next) {
  if (!next) return;
  personalFlight?.update(next);
  state = next;
  operations?.update(next);
  syncUnits(next.research?.units);
  guidance($("flight-guidance"), next.research?.guidance);
  const environment=$('environment-readings');
  if(environment){environment.replaceChildren(text('h2',localizedCopy("ui.research.messageTerrainLocalEnvironment")));
    for(const r of next.telemetry?.environment?.readings??[])environment.append(text('p',`${r.simulatorVariable}: ${n(r.value,3)} ${r.unit} · ${r.quality}`));
    if(!next.telemetry?.environment)environment.append(text('p',localizedCopy("ui.research.messageAwaitingOptionalSimulatorGroundWeatherObservationsFlightRecording")));
    const details=text('details');details.append(text('summary',localizedCopy("ui.research.messageTimingAndReferenceInformation")),text('p',localizedCopy("ui.research.messageOptionalFieldsRetainIndependentPacketReceiptTimes")));environment.append(details);
  }
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
      const group=isRemote?textForUI("ui.research.messageRemoteSensingInstruments"):textForUI("ui.research.messageAtmosphericInstruments");
      if(group!==previousGroup){p.append(text("h2",group));previousGroup=group;}
      card.append(
        text("h3", d?.name || i.instrumentId),
        text("span", i.enabled ? i.quality : localizedCopy("ui.research.messageDisabled"), "science-state"),
      );
      if(i.payloadProfileName)card.append(text('p',i.payloadProfileName));
      for (const v of i.measurements) {
        const label =
          d?.measurements.find((m) => m.id === v.measurementId)?.name ||
          (v.measurementId.endsWith('.user-adjusted')?v.measurementId.replace('.user-adjusted','')+' · User-adjusted derivation':v.measurementId);
        const reading = displayReading(v.measurementId, v.value, v.unit);
        card.append(
          text("p", label, "secondary-label"),
          text(
            "div",
            i.quality === "valid" && v.quality === "valid"
              ? `${n(reading.value, reading.decimals)} ${reading.unit}`
              : localizedCopy("localization.unavailableValue"),
            "science-value",
          ),
        );
      }
      card.append(
        text(
          "p",
          localizedCopy("ui.research.messageEverySAcquisitionSets",{value1:(String(i.intervalMs / 1000)),value2:(String(i.acquisitionSets)),value3:(String(i.observationCount)),value4:(String(isRemote ? "saved products" : "measurement values"))}),
        ),
      );
      const details = text("details");
      details.dataset.instrument = i.instrumentId;
      details.open = opened.has(i.instrumentId);
      details.append(
        text("summary", localizedCopy("ui.research.messageAcquisitionDiagnostics")),
        text(
          "p",
          localizedCopy("ui.research.messageLastSuccessfulActualIntervalS",{value1:(String(i.lastSuccessfulUtc || "None")),value2:(String(n(i.actualAverageIntervalSeconds))),value3:(String(i.rejectedObservations)),value4:(String(i.missingIntervals))}),
        ),
        text(
          "p",
          i.message ||
            localizedCopy("ui.research.messageSimulatorReportedValuesNoIndependentCalibratedMeasurements"),
        ),
      );
      card.append(details);
      if (isRemote)
        card.append(
          text(
            "p",
            i.message || localizedCopy("ui.research.messageImportSupportedRealGeographicDataToCollect"),
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
        localizedCopy("ui.research.messageSourceAvailability",{value1:(String(next.research?.collectionStatus || "stopped"))}),
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
    text("h2", localizedCopy("ui.research.messageScienceCollection")),
    text(
      "p",
      localizedCopy("ui.research.messageInstrumentsEnabledAcquisitionSets",{value1:(String(next.research?.collectionStatus || "stopped")),value2:(String(instruments.filter((i) => i.enabled).length)),value3:(String(instruments.reduce((n, i) => n + i.acquisitionSets, 0))),value4:(String(instruments.reduce((n, i) => n + i.observationCount, 0)))}),
    ),
  );
  const link = text("a", localizedCopy("ui.research.messageViewScience"));
  link.href = "#science";
  summary.append(link);
  if (next.activeMission && livePlan) {
    summary.append(
      text("h3", localizedCopy("ui.research.messageActiveResearchRoute")),
      text(
        "p",
        localizedCopy("ui.research.messagePlannedWaypointsCurrentPosition",{value1:(String(livePlan.patterns?.map((p) => p.kind).join(", ") || "Custom route")),value2:(String(livePlan.waypoints?.length || 0)),value3:(String(next.telemetry ? "available" : "unavailable"))}),
      ),
    );
    const mapLink = text("a", localizedCopy("ui.research.messageViewMissionMap"));
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
          localizedCopy("ui.research.messageNextObservedWaypointObservedInOrder",{value1:(String(livePlan.waypoints?.[activeRouteState.nextWaypoint]?.name || "End of route")),value2:(String(planningDistance(1000)))}),
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
    activeRouteState = null; remoteFootprints = [];
    lastFetch = 0;
  }
  if(!next.activeMission && next.selectedPlanId && selectedPreview!==next.selectedPlanId && !selectingPreview){
    selectingPreview=true;const selected=next.selectedPlanId;
    ctx.api(`/api/plans/${selected}`).then(plan=>{if(state?.selectedPlanId===selected&&!state.activeMission){livePlan=plan;selectedPreview=selected;setLiveMap({plan,aircraft:aircraftFor(next)});if(!operations?.reviewing)liveMap?.fit();}}).catch(e=>ctx.notice(e.message)).finally(()=>selectingPreview=false);
  }else if(!next.activeMission&&!next.selectedPlanId){livePlan=null;selectedPreview=null;}
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
    footprints: remoteFootprints,
  });
  setLiveMap({
    track,
    markers,
    samples: samplePositions(scienceRows),
    footprints: remoteFootprints,
    plan: livePlan || {},
    aircraft: fresh ? t : null,
  });
  if (liveId && !busy && Date.now() - lastFetch > 4000 && ($('control').offsetParent||$('instruments').offsetParent||operations?.active&&!operations.reviewing)) {
    busy = true;
    lastFetch = Date.now();
    refreshLive(liveId)
      .catch((e) => ctx.notice(e.message))
      .finally(() => (busy = false));
  }
}
window.addEventListener("aeroscience-selection",()=>{selectedPreview=null;});
let selectedPreview=null,selectingPreview=false;
let livePlan = null,
  activeRouteState = null, remoteFootprints = [];
async function refreshLive(id) {
  const [points, marks, research, science, navigation, products] = await Promise.all([
    ctx.api(
      `/api/missions/${id}/track?limit=1500${trackCursor ? `&after=${trackCursor}` : ""}`,
    ),
    ctx.api(`/api/missions/${id}/markers?limit=200`),
    ctx.api(`/api/missions/${id}/research`),
    ctx.api(`/api/missions/${id}/science?after=${cursor}&limit=1000`),
    ctx.api("/api/maps/active"),
    ctx.api(`/api/science/products?limit=30&missionId=${id}`),
  ]);
  if (liveId !== id) return;
  operations?.scannerProducts?.(products);
  track = trackCursor ? [...track, ...points.points] : points.points;
  if (track.length > 3000)
    track = track.filter((_, i) => i % 2 === 0 || i === track.length - 1);
  trackCursor = points.nextSequence;
  markers = marks;
  livePlan = research.planSnapshot;
  activeRouteState = navigation;
  remoteFootprints=products.filter(p=>p.footprint?.boundary?.length>=3).map(p=>({vertices:p.footprint.boundary,label:textForUI("ui.research.messageDerivedFootprintFlightExternal",{value1:(String(p.instrumentId)),value2:(String(p.flightPacketUtc)),value3:(String(p.sourceObservationUtc||"unknown date"))})}));
  scienceRows = [...scienceRows, ...science.observations].slice(-3000);
  cursor = science.next;
  setLiveMap({
    track,
    markers,
    samples: samplePositions(scienceRows),
    footprints: remoteFootprints,
    plan: livePlan || {},
    aircraft: aircraftFor(state),
  });
  if ($("science-charts") && !document.querySelector(".expanded-chart[open], .chart-values[open]")) {
    $("science-charts").replaceChildren();
    scienceCharts($("science-charts"), scienceRows);
  }
}
function showScience(parent, rows, label) {
  parent.replaceChildren(text("p", label));
  scienceCharts(parent, rows);
  const raw = text("details");
  raw.append(text("summary", localizedCopy("ui.research.messageOriginalCanonicalObservationRows")));
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
  const host=body,access=await ctx.api('/api/science/access');
  if(access.canConfigure&&mission.status!=='recording'){const removeScience=button('Delete Collected Science',()=>reviewCollectedScienceDeletion(ctx.api,{missionIds:[id]},async()=>{host.replaceChildren();await researchDetail(host,id,mission);}));removeScience.className='danger';body.append(removeScience);}
  missionPerformance(body,ctx.api,id);
  const sessions=text('details');sessions.append(text('summary',localizedCopy("ui.research.messageScienceSessionsAndRetainedMeasurements")));body.append(sessions);
  sessions.addEventListener('toggle',async()=>{if(!sessions.open||sessions.dataset.loaded)return;sessions.dataset.loaded='true';
    try{const [record,counts]=await Promise.all([ctx.api(`/api/missions/${id}/collection`),ctx.api(`/api/missions/${id}/collection/counts`)]);
      if(!record.state){sessions.append(text('p',localizedCopy("ui.research.messageLegacyArchiveNoScienceSessionsWereRecorded")));return;}
      for(const session of record.sessions){const count=counts.find(c=>c.sessionId===session.id);sessions.append(text('p',localizedCopy("ui.research.messageSessionOriginal",{value1:(String(session.sequence)),value2:(String(session.id)),value3:(String(session.status)),value4:(String(session.startedUtc)),value5:(String(session.endedUtc||'unknown end time')),value6:(String(count?.originalAtmosphericValues||0)),value7:(String(count?.derivedAtmosphericValues||0)),value8:(String(count?.remoteProducts||0))})));}
      sessions.append(text('p',localizedCopy("ui.research.messageRetainedCountsDoNotEstablishMeasurementAccuracy")));
    }catch(e){sessions.append(text('p',localizedCopy("ui.research.messageSessionEvidenceScientificLibraryUnavailable",{value1:(String(e.message))})));}
  });
  const operating=document.createElement('section');body.append(operating);await operatingAssessments(operating,ctx.api,id);
  await initializeUnits(ctx.api);
  const review = await ctx.api(`/api/missions/${id}/review`);
  if (review.research.planSnapshot?.researchCards?.length || review.research.planSnapshot?.researchCard) {
    const link = text("a", localizedCopy("ui.research.messageReviewPersonalResearch")); link.href = `#my-research?flight=${id}`; body.append(link);
  }
  const workspace=missionWorkspace(body,id,mission,ctx.api,review);body=workspace.overview;
  researchFlightTimeline(body,ctx.api,id).catch(error=>body.append(text('p',error.message)));
  const protection = await ctx.api(`/api/missions/${id}/protection`);
  const protectedLabel = text(
    "strong",
    protection.isProtected ? localizedCopy("ui.research.messageProtectedMission") : localizedCopy("ui.research.messageMissionIsNotPinned"),
  );
  const protectMissionButton=button(
      protection.isProtected ? "Remove Protection" : "Protect Mission",
      async () => {
        const result = await ctx.api(`/api/missions/${id}/protection`, {
          protected: !protection.isProtected,
        });
        protection.isProtected = result.isProtected;
        setPresentationText(protectedLabel,result.isProtected
          ? localizedCopy("ui.research.messageProtectedMission")
          : localizedCopy("ui.research.messageMissionIsNotPinned"));
        setPresentationText(protectMissionButton,result.isProtected?localizedCopy("ui.research.messageRemoveProtection"):localizedCopy("ui.research.messageProtectMission"));
      },
    );
  body.append(protectedLabel,protectMissionButton);
  if (review.scienceAvailable)
    try {
      await remoteProducts(workspace.overview, ctx.api, id);
    } catch (error) {
      body.append(text("p", localizedCopy("ui.research.messageRemoteProductsUnavailable",{value1:(String(error.message))})));
    }
  if (review.research.planSnapshot) {
    body.append(text("h3", localizedCopy("ui.research.messagePlannedRouteImmutableRecordingSnapshot")));
    routeExportButton(body, () => review.research.planSnapshot, ctx.api);
    body.append(
      text(
        "p",
        localizedCopy("ui.research.messageRecordedFlightExportsBelowContainActualSaved"),
      ),
    );
  }
  await renderAnalysis(workspace.analysis, id, review.research.planSnapshot, ctx.api);
  body.append(
    text("h3", localizedCopy("ui.research.messageResearchReview")),
    text("p", localizedCopy("ui.research.messageRecordingSource",{value1:(String(mission.source || "Unavailable"))})),
    text(
      "p",
      `${review.research.template} · ${review.research.objective || textForUI("ui.research.messageNoObjectiveSupplied")}`,
    ),
  );
  for (const pause of review.sciencePauses || [])
    body.append(
      text(
        "p",
        localizedCopy("ui.research.messageScienceCollectionDeliberatelyPausedTo",{value1:(String(pause.startedUtc)),value2:(String(pause.endedUtc || "unknown end")),value3:(String(pause.seconds == null ? "duration unknown" : n(pause.seconds) + " s")),value4:(String(pause.interrupted ? " · recording interrupted" : ""))}),
      ),
    );
  const f = review.flight;
  body.append(
    text(
      "p",
      localizedCopy("ui.research.messageMachMeanValues",{value1:(String(n(f.minimumMach, 3))),value2:(String(n(f.maximumMach, 3))),value3:(String(n(f.meanMach, 3))),value4:(String(f.machSamples)),value5:(String(n(f.minimumIasKnots))),value6:(String(n(f.maximumIasKnots))),value7:(String(n(f.actualAverageIntervalSeconds, 3))),value8:(String(f.gapEvents))}),
    ),
  );
  if (review.research.planSnapshot)
    body.append(
      text(
        "p",
        localizedCopy("ui.research.messagePlanSnapshotPreflightNotes",{value1:(String(review.research.planSnapshot.name)),value2:(String(review.research.planSnapshot.preflightNotes || "None"))}),
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
        localizedCopy("ui.research.messageSavedRejectedMissingIntervalsActual",{value1:(String(d.instrumentId)),value2:(String(d.observationCount)),value3:(String(d.rejectedObservations)),value4:(String(d.missingIntervals)),value5:(String(n(d.actualAverageIntervalSeconds)))}),
      ),
    );
  for (const d of review.datasets)
    body.append(
      text(
        "p",
        localizedCopy("ui.research.messageDatasetLibrary",{value1:(String(d.datasetId)),value2:(String(d.available ? "Available" : "Unavailable / cleared")),value3:(String(d.libraryId))}),
      ),
    );
  if (!review.scienceAvailable)
    body.append(
      text(
        "p",
        localizedCopy("ui.research.messageScienceLibraryUnavailableFlightHistoryRemains",{value1:(String(review.scienceError))}),
      ),
    );
  for (const s of review.measurements)
    body.append(
      text(
        "p",
        localizedCopy("ui.research.messageMeasurementValuesMin",{value1:(String(s.measurementId)),value2:(String(s.count)),value3:(String(n(displayReading(s.measurementId, s.minimum, s.unit).value, 3))),value4:(String(n(displayReading(s.measurementId, s.maximum, s.unit).value, 3))),value5:(String(n(displayReading(s.measurementId, s.mean, s.unit).value, 3))),value6:(String(displayReading(s.measurementId, 0, s.unit).unit))}),
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
      localizedCopy("ui.research.messageMapDisplayPointsFromSavedSamples",{value1:(String(path.points.length)),value2:(String(path.totalSamples)),value3:(String(path.downsampled ? " (display reduced)" : ""))}),
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
  const note = input(body, localizedCopy("ui.research.messagePostflightNote"), "", "textarea");
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
      textForUI("ui.research.messageFirst1000ObservationsCompleteExportsIncludeEvery"),
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
        showScience(holder, next.observations, textForUI("ui.research.messageNextObservationPage"));
      }),
    );
  }
  if (mission.status !== "recording")
    for (const [format, label] of [
      ["flight.csv", textForUI("ui.research.messageFlightTelemetryCSV")],
      ["science.csv", textForUI("ui.research.messageScientificObservationsCSV")],
      ["events.csv", textForUI("ui.research.messageEventsCSV")],
      ["gpx", textForUI("ui.research.messageActualTrackGPX")],
      ["kml", textForUI("ui.research.messagePlannedActualKML")],
      ["report.txt", textForUI("ui.research.messageResearchSummary")],
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
        localizedCopy("ui.research.messageLiveMeasurementsUnavailableRecordedScienceAndFlight"),
      ),
    );
  setLiveMap({ track, markers, plan: livePlan || {}, aircraft: null });
  scienceMap?.set({ track, markers, plan: livePlan || {}, aircraft: null });
}
