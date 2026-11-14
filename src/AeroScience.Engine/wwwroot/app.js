import {message as localizedCopy,setPresentationText} from './i18n.js';
import {dialogHeader,requestDialogClose,markDialogSaved} from './dialog-ui.js';
import {readableName} from './workstation-ui.js';
import {initializePresentation,presentationControls,connectPresentationHost} from './presentation-settings.js';
import {resolveRoute,viewPages} from './navigation-routes.js';
import {t as textForUI,localizeTree} from './i18n.js';
import {homeDashboard} from './home.js';
import {missionsExplorer,missionPreparation} from './missions.js';
import {readSuccessResponse} from './http-response.js';
import {scienceWorkspace,disposeScienceWorkspace} from './science-workspace.js';
import {helpCenter,disposeHelpCenter} from './help-center.js';
import {missionSelection} from './mission-selection.js';
import {taskWorkspaces} from './task-workspaces.js';
import {scienceJourney} from './science-journey.js';
import {startMissionWithReadiness} from './mission-start-advisory.js';
import {personalResearch,disposePersonalResearch} from './personal-research.js';
import {scientificLibrary,disposeScientificLibrary} from './scientific-library.js';
import {downloadWorkspace,disposeDownloadWorkspace} from './download-workspace.js';
import {initializeWorkstationShell,streamlineMissionControl} from './workstation-shell.js';
import { settingsLocation,settingsNavigation } from "./settings-navigation.js";
import { mapSettingsUI, syncBasemaps } from "./basemaps.js";
import { sourceSettings } from "./remote-science.js";
import { updateSettings, notifyUpdates } from "./application-updates.js";
import { storageManagement } from "./storage-management.js";
import {libraryLocation} from './library-location.js';
setInterval(()=>{if(authorized&&!document.hidden)notifyUpdates(api);},60000);
import {
  initializeResearch,
  plannerDraft,
  loadResearch,
  renderResearch,
  researchDetail,
  invalidateResearch,
  openMissionOperations,closeMissionOperations,
} from "./research.js";
import { unitControls, initializeUnits } from "./science-display.js";
const $ = (id) => document.getElementById(id);
initializePresentation();
const workstationShell=initializeWorkstationShell();
const workspaces=taskWorkspaces();
const number = (v, digits = 0) =>
  Number.isFinite(v)
    ? v.toLocaleString("en-US", {
        maximumFractionDigits: digits,
        minimumFractionDigits: digits,
      })
    : "—";
const duration = (seconds) => {
  const s = Math.max(0, Math.floor(seconds));
  return [Math.floor(s / 3600), Math.floor(s / 60) % 60, s % 60]
    .map((n) => String(n).padStart(2, "0"))
    .join(":");
};
const date = (value) =>
  new Date(value).toLocaleString(undefined, {
    dateStyle: "medium",
    timeStyle: "short",
  });
const modeLabel = (mode) =>
  mode === "realistic" ? textForUI("ui.app.messageRealisticMode") : textForUI("ui.app.messageUniversalMode");
const modeHelp = (mode) =>
  mode === "realistic"
    ? textForUI("ui.app.messageRealisticModeEvidenceBasedOperatingAdvisoriesAnd")
    : textForUI("ui.app.messageUniversalMode");
function el(tag, text, className) {
  const node = document.createElement(tag);
  if (text !== undefined) setPresentationText(node,text);
  if (className) node.className = className;
  return node;
}
let csrf = "",
  current = null,
  stream = null,
  chart = [],
  lastTimestamp = "",
  historyPage = 0,
  historyRequest = 0;
let authorized = false,
  streamAlive = false;
let homeView=null;
let explorerView=null,preparationView=null;
function selectNavigation(route){
  const organization=route.page==='my-research'&&route.query.get('section')==='organizations'&&!$('organizations-navigation').hidden;
  for(const link of document.querySelectorAll('.nav-link')){
    const selected=link.hash==='#organizations' ? organization : !organization&&resolveRoute(link.hash).workspaceKey===workspaces.key(route.page);
    link.classList.toggle('selected',selected);
    if(selected)link.setAttribute('aria-current','page');else link.removeAttribute('aria-current');
  }
}
function organizationNavigation(connections){$('organizations-navigation').hidden=false;selectNavigation(resolveRoute(location.hash));}
window.addEventListener('aeroscience-organizations',event=>organizationNavigation(event.detail));
function notice(message = "") {
  setPresentationText($("notice"),message);
  $("notice").hidden = !message;
}
async function api(path, body, options = {}) {
  const response = await fetch(
    path,
    body === undefined
      ? options
      : {
          ...options,
          method: "POST",
          headers: {
            "Content-Type": "application/json",
            "X-AeroScience-CSRF": csrf,
          },
          body: JSON.stringify(body),
        },
  );
  if (!response.ok) {
    const data = await response.json().catch(() => ({}));
    if (response.status === 401) {
      authorized = false;
      stream?.close();
      $("pairing").hidden = false;
      $("workspace").hidden = true;
    }
    const error = new Error(
      data.error ||
        {
          401: textForUI("ui.app.messagePairingFailedOrYourSessionExpiredReconnect"),
          403: textForUI("ui.app.messageRequestProtectionRejectedTheCommandReloadThis"),
          429: textForUI("ui.app.messageTooManyPairingAttemptsWaitOneMinute"),
        }[response.status] ||
        textForUI("ui.app.messageRequestFailed",{value1:(String(response.status))}),
    );
    error.status = response.status;
    throw error;
  }
  return readSuccessResponse(response);
}
async function connect(code = null) {
  const session = await api("/api/session", { code });
  csrf = session.csrf;
  authorized = true;
  await connectPresentationHost(api);
  api('/api/organizations').then(value=>organizationNavigation(value.connections)).catch(()=>organizationNavigation([]));
  const build=await api("/api/build");
  if(build.profile==="validation"){
    dashboardTitle=build.name;
    setPresentationText(document,build.name,"title",true);
    let banner=document.getElementById("validation-identity");
    if(!banner){banner=el("p","","notice");banner.id="validation-identity";banner.setAttribute("role","status");document.body.prepend(banner);}
    setPresentationText(banner,localizedCopy("ui.app.messagePrivateTestProfilePort",{value1:(String(build.name)),value2:(String(build.port)),value3:(String(build.sourceCommit))}));
  }
  window.dispatchEvent(new Event("aeroscience-session"));
  $("pairing").hidden = true;
  $("workspace").hidden = false;
  notifyUpdates(api);
  notice();
  stream?.close();
  stream = new EventSource("/api/events");
  stream.addEventListener("state", (event) => {
    streamAlive = true;
    setPresentationText($("engine-link"),localizedCopy("ui.app.messageEngineOnline"));
    $("engine-dot").classList.add("live");
    try {
      render(JSON.parse(event.data));
    } catch {
      notice(localizedCopy("ui.app.messageTheEngineReturnedAnUnexpectedStateReload"));
    }
  });
  stream.onerror = async () => {
    streamAlive = false;
    homeView?.update();
    preparationView?.stale();
    setPresentationText($("engine-link"),localizedCopy("ui.app.messageReconnecting"));
    $("engine-dot").classList.remove("live");
    notice(
      localizedCopy("ui.app.messageDisplayConnectionInterruptedTheEngineContinuesRecording"),
    );
    clearTelemetry();
    // A restarted engine has new session keys. Network interruptions retain the old session.
    try {
      const response = await fetch("/api/state");
      if (response.status === 401) {
        stream.close();
        authorized = false;
        const access = await api("/api/access");
        if (!access.requiresPairing) await connect();
        else {
          $("pairing").hidden = false;
          $("workspace").hidden = true;
          notice(
            localizedCopy("ui.app.messageTheEngineRestartedOrYourSessionExpired"),
          );
        }
      }
    } catch {
      /* EventSource retries a transient network failure without creating another stream. */
    }
  };
  await initializeUnits(api);
  await navigate();
}
function clearTelemetry() {
  invalidateResearch();
  for (const id of [
    "altitude",
    "ias",
    "groundspeed",
    "vertical-speed",
    "pitch",
    "bank",
    "latitude",
    "longitude",
    "heading",
    "mach",
  ])
    setPresentationText($(id),"—");
  setPresentationText($("mach"),localizedCopy("ui.app.messageMach"));
  setPresentationText($("tas"),localizedCopy("ui.app.messageTrueAirspeed"));
  setPresentationText($("track"),localizedCopy("ui.app.messageTrack"));
  setPresentationText($("agl"),localizedCopy("ui.app.messageAboveGround"));
  $("horizon").setAttribute("transform", "translate(0 0)");
  $("connection-dot").classList.remove("live");
}
function updateStartButton(state) {
  const t=state.telemetry,fresh=t&&Date.now()-Date.parse(t.timestampUtc)<6000;
  $("start-button").disabled=starting||!fresh||state.connection.state!=="connected"||!!t?.isPaused||!selection.canStart(state);
}
function render(state) {
  let stopped = document.getElementById("recording-storage-stop");
  if (state.recordingStop) {
    if (!stopped) {
      stopped = el("aside", undefined, "panel source-notice");
      stopped.id = "recording-storage-stop";
      $("control").prepend(stopped);
    }
    setPresentationText(stopped,localizedCopy("ui.app.messageRECORDINGSTOPPED",{value1:(String(state.recordingStop.code)),value2:(String(state.recordingStop.message))}));
  } else stopped?.remove();
  syncBasemaps(state.maps);
  renderResearch(state);
  selection?.update(state);
  const wasActive = current?.activeMission?.id;
  current = state;
  homeView?.update();
  preparationView?.update(state);
  journey?.update(state);
  const t = state.telemetry;
  const fresh = t && Date.now() - Date.parse(t.timestampUtc) < 6000;
  const simulated = state.connection.source === "simulated";
  const connected = fresh && state.connection.state === "connected";
  setPresentationText($("source-badge"),simulated
    ? localizedCopy("ui.app.messageSIMULATEDTELEMETRY")
    : connected
      ? localizedCopy("ui.app.messageSIMCONNECTLIVEAIRCRAFT")
      : localizedCopy("ui.app.messageLIVETELEMETRYUNAVAILABLE"));
  $("source-badge").className =
    "badge " + (connected && !simulated ? "green" : "amber");
  setPresentationText($("source-note"),simulated
    ? localizedCopy("ui.app.messageTestFixtureData")
    : localizedCopy("ui.app.messageHeadingAndGroundTrackUseTrueNorth"));
  setPresentationText($("connection-text"),state.connection.message);
  setPresentationText($("aircraft"),fresh
    ? t.aircraft
    : localizedCopy("ui.app.messageAwaitingFreshAircraftData"));
  setPresentationText($("sampling-label"),localizedCopy("ui.app.messageTargetHzSampleCadenceHz",{value1:(String(number(1000 / state.samplingIntervalMs, 2))),value2:(String(number(state.sampling?.achievedHz, 2))),value3:(String(number(state.sampling?.missionAverageHz, 2))),value4:(String(number(state.sampling?.missingIntervals ?? 0)))}));
  $("connection-dot").classList.toggle(
    "live",
    state.connection.state === "connected" && !!fresh,
  );
  setPresentationText($("sample-time"),fresh
    ? localizedCopy("ui.app.messageUTC",{value1:(String(new Date(t.timestampUtc).toISOString().slice(11, 19)))})
    : localizedCopy("ui.app.messageNOFRESHDATA"));
  if (fresh) {
    setPresentationText($("altitude"),number(t.altitudeMslFeet));
    setPresentationText($("ias"),number(t.indicatedAirspeedKnots));
    setPresentationText($("mach"),localizedCopy("ui.app.messageMachc8e9ec1f",{value1:(String(number(t.machNumber, 2)))}));
    setPresentationText($("groundspeed"),number(t.groundspeedKnots));
    setPresentationText($("vertical-speed"),(t.verticalSpeedFeetPerMinute > 0 ? "+" : "") +
      number(t.verticalSpeedFeetPerMinute));
    setPresentationText($("tas"),localizedCopy("ui.app.messageTrueAirspeedKts",{value1:(String(number(t.trueAirspeedKnots)))}));
    setPresentationText($("track"),localizedCopy("ui.app.messageTrackT",{value1:(String(number(t.groundTrackDegrees, 1)))}));
    setPresentationText($("agl"),localizedCopy("ui.app.messageAboveGroundFt",{value1:(String(number(t.altitudeAglFeet)))}));
    setPresentationText($("pitch"),`${number(t.pitchDegrees, 1)}°`);
    setPresentationText($("bank"),`${number(t.bankDegrees, 1)}°`);
    setPresentationText($("latitude"),`${number(Math.abs(t.latitude), 5)}° ${t.latitude >= 0 ? "N" : "S"}`);
    setPresentationText($("longitude"),`${number(Math.abs(t.longitude), 5)}° ${t.longitude >= 0 ? "E" : "W"}`);
    setPresentationText($("heading"),`${number(t.headingDegrees, 1)}° T`);
    $("horizon").setAttribute(
      "transform",
      `rotate(${-t.bankDegrees} 110 50) translate(0 ${Math.max(-45, Math.min(45, t.pitchDegrees * 2))})`,
    );
    if (t.timestampUtc !== lastTimestamp) {
      chart.push(t.altitudeMslFeet);
      chart = chart.slice(-60);
      lastTimestamp = t.timestampUtc;
      const min = Math.floor(Math.min(...chart) / 100) * 100 - 100,
        max = Math.ceil(Math.max(...chart) / 100) * 100 + 100;
      setPresentationText($("chart-max"),number(max));
      setPresentationText($("chart-min"),number(min));
      $("altitude-line").setAttribute(
        "d",
        chart
          .map(
            (v, i) =>
              `${i ? "L" : "M"}${(i * 600) / 59},${120 - ((v - min) / (max - min)) * 110}`,
          )
          .join(" "),
      );
    }
  } else clearTelemetry();
  updateStartButton(state);
  const mission = state.activeMission;
  if(mission&&!location.hash)location.hash='#operations';
  $("mission-idle").hidden = !!mission;
  $("mission-active").hidden = !mission;
  setPresentationText($("record-status"),mission ? localizedCopy("ui.app.messageRECORDING") : localizedCopy("ui.app.messageSTANDBY"));
  $("record-status").className = "badge" + (mission ? " green" : "");
  if (mission) {
    setPresentationText($("active-name"),mission.name);
    setPresentationText($("active-samples"),number(mission.sampleCount));
    setPresentationText($("active-mode"),modeHelp(mission.mode));
    setPresentationText($("active-hint"),fresh
      ? localizedCopy("ui.app.messageMissionRecording")
      : localizedCopy("ui.app.messageMissionActiveWaitingForFreshTelemetry"));
    renderDuration();
  }
  if (
    streamAlive &&
    $("notice").textContent.startsWith("Display connection interrupted.")
  )
    notice();
  if (wasActive !== mission?.id && location.hash === "#history")
    loadHistory().catch((e) => notice(e.message));
}
function renderDuration() {
  if (current?.activeMission)
    setPresentationText($("active-duration"),duration(
      (Date.now() - Date.parse(current.activeMission.startedUtc)) / 1000,
    ));
}
async function command(button, operation) {
  button.disabled = true;
  try {
    notice();
    await operation();
  } catch (e) {
    notice(e.message);
  } finally {
    button.disabled = false;
  }
}
let starting=false;
const launchInputs=()=>({name:$("mission-name").value,mode:$("mission-mode").value,
  planId:$("selected-plan")?.value||null,armScience:$("arm-science")?.checked||false});
$("start-form").addEventListener("submit", (event) => {
  event.preventDefault();
  if(starting)return;
  starting=true;
  const startHash=location.hash;
  command($("start-button"), async () => {
    try{
    const mission=await startMissionWithReadiness(api,launchInputs(),{readLaunch:launchInputs,opener:$("start-button")});
    if(!mission)return;
    $("mission-name").value = "";
    $("mission-mode").value = "universal";
    setPresentationText($("mode-help"),modeHelp("universal"));
    render(await api("/api/state"));
    location.hash="#operations";
    }finally{starting=false;if(current)render(current);}
  }).then(()=>{if(current)render(current);if(location.hash===startHash&&!current?.activeMission&&!document.querySelector("dialog[open]"))$("start-button").focus({preventScroll:true});});
});
$("mission-mode").addEventListener("change", () => {
  setPresentationText($("mode-help"),modeHelp($("mission-mode").value));
});
$("end-button").addEventListener("click", () =>
  command($("end-button"), async () => {
    if (!current?.activeMission) return;
    await api(`/api/missions/${current.activeMission.id}/end`, {});
    render(await api("/api/state"));
  }),
);
$("pair-form").addEventListener("submit", (event) => {
  event.preventDefault();
  command(event.submitter, () => connect($("pair-code").value));
});
let navigationEpoch=0;
let dashboardTitle="AeroScience";
async function navigate() {
  const ownNavigation=++navigationEpoch;
  const route=resolveRoute(location.hash);
  if(route.redirect){location.replace(route.redirect);return;}
  const page=route.page;
  homeView?.dispose();homeView=null;
  explorerView?.dispose();explorerView=null;preparationView?.dispose();preparationView=null;
  if(page!=="science-workspace")disposeScienceWorkspace();
  if(page!=="downloads")disposeDownloadWorkspace();
  if(page!=="library")disposeScientificLibrary();
  if(page!=="help")disposeHelpCenter();
  if(page!=="my-research")disposePersonalResearch();
  if(page!=="operations")closeMissionOperations();
  for (const id of viewPages) $(id).hidden = id !== page;
  const workspace=workspaces.show(page,route);
  selectNavigation(route);
  setPresentationText($("breadcrumb"),{
    home: localizedCopy("ui.app.messageHOME"),
    'missions-explorer':localizedCopy("ui.app.messageMISSIONS"), 'mission-preparation':localizedCopy("ui.app.messageMISSIONSPREPARE"),
    control: localizedCopy("ui.app.messageFLIGHTOPERATIONS"),
    operations: localizedCopy("ui.app.messageFLIGHTOPERATIONS"),
    history: localizedCopy("ui.app.messageRESULTSHISTORY"),
    instruments: localizedCopy("ui.app.messageFLIGHTOPERATIONS"),
    planner: localizedCopy("ui.app.messageFLIGHTPLANNER"),
    "my-research": localizedCopy("ui.app.messageMYRESEARCH"),
    settings: localizedCopy("ui.app.messageSETTINGS"),
    "science-workspace": localizedCopy("ui.app.messageRESULTSHISTORY"),library:localizedCopy("ui.app.messageSCIENTIFICLIBRARY"),help:localizedCopy("ui.app.messageHELPSUPPORT"),downloads:localizedCopy("ui.app.messageSCIENTIFICLIBRARY"),"library-storage":localizedCopy("ui.app.messageSCIENTIFICLIBRARY"),
  }[page]);
  setPresentationText(document,`${dashboardTitle} · ${workspace?.querySelector('h1')?.textContent||({home:textForUI("nav.home"),planner:textForUI("nav.planner"),'my-research':textForUI("nav.myResearch"),settings:textForUI("nav.settings"),help:'Help & Support'}[page])||page}`,"title",true);
  if (!authorized) return;
  if(page==='home')homeView=homeDashboard($('home'),api,{snapshot:()=>current,draft:plannerDraft});
  if(page==='missions-explorer')explorerView=missionsExplorer($('missions-explorer'),api,{draft:plannerDraft});
  if(page==='mission-preparation')preparationView=missionPreparation($('mission-preparation'),api,{snapshot:()=>current,draft:plannerDraft,
    continueToFlight:({planId,armScience})=>{
      // Only the original form's conservative draft changes. Start Recording
      // still submits through its original readiness/revision/identity controller.
      if(armScience){$('arm-science').checked=true;const details=$('arm-science').closest('details');if(details)details.open=true;}
      location.hash='#operations?researchPlan='+encodeURIComponent(planId);
      window.scrollTo(0,0);
    }});
  if (page === "science-workspace") await scienceWorkspace($("science-workspace"),api);
  if (page === "help") await helpCenter($("help"),api);
  if (page === "library") await scientificLibrary($("library"),api);
  if (page === "downloads") await downloadWorkspace($("downloads"),api);
  if (page === "my-research") await personalResearch($("my-research"),api);
  if (page === 'operations') await openMissionOperations();
  if (page === "control") selection?.refresh();
  if (page === "history") await loadHistory();
  if (page === "settings") await loadSettings();
  if (page === "library-storage") await attachStorage();
  if (page === "planner" || page === "instruments") await loadResearch(page);
  if(ownNavigation===navigationEpoch){
    localizeTree(workspace||$(page));
    const organization=page==='my-research'&&route.query.get('section')==='organizations' ? $('research-organizations') : null;
    if(organization){organization.scrollIntoView();workstationShell.afterNavigation(organization);}
    else workstationShell.afterNavigation(workspace||$(page));
  }
}
async function loadHistory() {
  const request = ++historyRequest;
  const missions = await api(
    `/api/missions?limit=20&offset=${historyPage * 20}&search=${encodeURIComponent($("history-search").value)}&status=${encodeURIComponent($("history-status").value)}&sort=${encodeURIComponent($("history-sort").value)}`,
  );
  if (request !== historyRequest) return;
  const list = $("history-list");
  list.replaceChildren();
  if (!missions.length)
    list.append(
      el(
        "div",
        localizedCopy("ui.app.messageNoMissionsOnThisPageStartA"),
        "panel empty-state",
      ),
    );
  for (const m of missions) {
    const card = el("article", undefined, "panel history-card");
    const info = el("div");
    info.append(
      el(
        "span",
        m.status.toUpperCase(),
        "badge" + (m.status === "recording" ? " green" : ""),
      ),
      el("h2", m.name),
      el(
        "p",
        `${m.aircraft || textForUI("ui.app.messageNoAircraftData")} · ${date(m.startedUtc)} · ${modeLabel(m.mode)} · ${m.source === "simulated" ? textForUI("ui.app.messageSIMULATED") : m.source === "simulator" ? textForUI("ui.app.messageSIMULATOR") : textForUI("ui.app.messageNOTELEMETRY")}`,
      ),
    );
    const meta = el("div", undefined, "history-meta");
    meta.append(
      el(
        "span",
        localizedCopy("ui.app.messageSamples",{value1:(String(duration(m.durationSeconds))),value2:(String(number(m.sampleCount)))}),
        "mono",
      ),
    );
    const open = el("button", localizedCopy("ui.app.messageViewMission"), "secondary");
    open.addEventListener("click", () => command(open, () => showDetail(m.id)));
    const science=el("a",localizedCopy("ui.app.messageOpenScienceReview"),"button secondary");science.href=`#workspace/analysis?missions=${m.id}`;
    const replayLink=el("a",localizedCopy("ui.app.messageReplayFlight"),"button secondary");replayLink.href=`#workspace/replay?missions=${m.id}`;
    science.className="button primary";open.className="tertiary";
    const researchStatus=el("p",localizedCopy("ui.app.messageScientificReviewStatusLoading"),"history-science-status");info.append(researchStatus);
    api(`/api/missions/${m.id}/archive-summary`).then(summary=>{if(request!==historyRequest)return;setPresentationText(researchStatus,localizedCopy("ui.app.messageScience",{value1:(String(summary.review.status)),value2:(String(summary.instruments.map(id=>readableName(id)).join(", ")||"No enabled instruments in archived configuration"))}));}).catch(()=>{if(request===historyRequest)setPresentationText(researchStatus,localizedCopy("ui.app.messageScientificStatusUnavailableRecordedFlightDetailsRemain"));});
    const operationsLink=el("a",localizedCopy("ui.app.messageMissionOperations"),"button secondary");operationsLink.href=`#operations?flight=${m.id}`;meta.append(science,operationsLink,replayLink,open);
    card.append(info, meta);
    list.append(card);
  }
  $("history-prev").disabled = historyPage === 0;
  $("history-next").disabled = missions.length < 20;
  setPresentationText($("history-page"),localizedCopy("ui.app.messagePage",{value1:(String(historyPage + 1))}));
}
async function showDetail(id) {
  const detail = await api(`/api/missions/${id}`),
    m = detail.mission;
  const body = $("detail-content");
  body.replaceChildren(el("h3", m.name));
  const rows = el("dl");
  const coordinates = (v) =>
    v
      ? `${v.latitude.toFixed(5)}, ${v.longitude.toFixed(5)}`
      : textForUI("ui.app.messageNoRecordedPosition");
  for (const [key, value] of [
    [textForUI("ui.receivedEvidenceUi.messageStatus"), m.status],
    [textForUI("ui.research.messageScienceMode"), modeLabel(m.mode)],
    [textForUI("ui.personalResearchReview.messageAircraft"), m.aircraft || textForUI("ui.app.messageNotAvailable")],
    [textForUI("ui.app.messageDataSource"), m.source || textForUI("ui.app.messageNoSamples")],
    [textForUI("ui.app.messageStarted"), date(m.startedUtc)],
    [textForUI("ui.app.messageEnded"), m.endedUtc ? date(m.endedUtc) : textForUI("html.index.textRecording")],
    [textForUI("ui.app.messageDuration"), duration(m.durationSeconds)],
    [textForUI("ui.app.messageSamplesbc228b26"), number(m.sampleCount)],
    [textForUI("ui.research.messageDeparture"), coordinates(m.departure)],
    [textForUI("ui.app.messageLastRecordedPosition"), coordinates(m.arrival)],
    [
      textForUI("ui.app.messageMSLRange"),
      textForUI("ui.app.messageFt",{value1:(String(number(m.minimumAltitudeFeet))),value2:(String(number(m.maximumAltitudeFeet)))}),
    ],
  ]) {
    const row = el("div");
    row.append(el("dt", key), el("dd", value));
    rows.append(row);
  }
  body.append(
    rows,
    el(
      "p",
      localizedCopy("ui.app.messageEventTimelineUpTo1000EventsInterrupted"),
    ),
  );
  const events = el("ol", undefined, "event-list");
  for (const event of detail.events)
    events.append(el("li", `${date(event.timestampUtc)} · ${event.message}`));
  body.append(events);
  const download = el(
    "a",
    localizedCopy("ui.app.messageInspectTelemetryJSONFirst5000Samples"),
    "secondary",
  );
  download.href = `/api/missions/${id}/telemetry?limit=5000`;
  download.target = "_blank";
  download.rel = "noopener";
  body.append(download);
  if (m.status !== "recording") {
    const complete = el("a", localizedCopy("ui.app.messageExportCompleteMissionJSON"), "secondary");
    complete.href = `/api/missions/${id}/export`;
    complete.download = `AeroScience-mission-${id}.json`;
    body.append(complete);
  }
  await researchDetail(body, id, m);
  dialogHeader($("mission-detail"),{title:localizedCopy("html.index.textMissionDetail")});$("mission-detail").showModal();
}
let storagePanel=null,storageLoading=null;
async function attachStorage(){
 if(!storageLoading){storagePanel=el('section');setPresentationText(storagePanel,localizedCopy("ui.app.messageStorageAndData"),"aria-label");storageLoading=(async()=>{
  await storageManagement(storagePanel,api);const s=await api('/api/settings');
  for(const [title,value]of [[textForUI("ui.app.messageMissionDatabase"),s.missionStorage],[textForUI("ui.app.messageScientificDataLibrary"),s.scienceStorage]]){const item=el('article');item.append(el('h3',title));if(value)item.append(el('p',value.directory),el('p',value.available?localizedCopy("ui.app.messageMiBUsedGiBFree",{value1:(String(number(value.usedBytes/1048576,1))),value2:(String(number(value.freeBytes/1073741824,1)))}):value.error));storagePanel.append(item);}
  for(const [title,action]of [[textForUI("ui.app.messageChangeStorageLocations"),'storage'],[textForUI("ui.app.messageDataManagementBackups"),'data-management']]){const b=el('button',title,'secondary');b.disabled=!s.desktopAvailable;b.onclick=()=>command(b,()=>api('/api/desktop/'+action,{}));storagePanel.append(b);}
 })().catch(error=>{storageLoading=null;throw error;});}
 await storageLoading;const target=location.hash.startsWith('#library/storage')?$('library-storage'):location.hash.startsWith('#settings/storage')?settingsView?.panels.get('storage'):null;if(target){target.append(storagePanel);if(!storagePanel.querySelector('.library-location'))await libraryLocation(storagePanel,api);}
}
let settingsView=null;
async function loadSettings() {
  const container=$("settings-content");
  if(settingsView){settingsView.show();if(settingsLocation().category==="storage")await attachStorage();return;}
  const s=await api("/api/settings");container.replaceChildren();
  const view=settingsNavigation(container),panels=view.panels;settingsView=view;view.show();
  presentationControls(panels.get('general'));
  function paragraph(category,value){panels.get(category).append(el("p",value));}
  function desktop(category,label,action){const b=el("button",label,"secondary");b.disabled=!s.desktopAvailable;b.onclick=()=>command(b,()=>api(`/api/desktop/${action}`,{}));panels.get(category).append(b);}
  paragraph("general",`AeroScience ${s.version}`);
  paragraph("general",textForUI("ui.app.messageLiveConnection",{value1:(String(current?.connection?.message||s.telemetry))}));
  paragraph("general",textForUI("ui.app.messageMissionHistoryAndExportsRemainAvailableWithout"));desktop("general",localizedCopy("ui.app.messageOpenApplicationSettings"),"settings");
  await unitControls(panels.get("units"),api,()=>{});
  await mapSettingsUI(panels.get("maps"),api);
  panels.get("storage").append(el("p",localizedCopy("ui.app.messageStorageControlsAreSharedWithScientificLibrary")));
  if(settingsLocation().category==="storage")await attachStorage();
  await updateSettings(panels.get("updates"),api);
  try{await sourceSettings(panels.get("science"),api);}catch(error){paragraph("science",textForUI("ui.app.messageScientificSourcesUnavailable",{value1:(String(error.message))}));}
  if(s.toolbarStatus){paragraph("toolbar",textForUI("ui.app.messageInstalledBundled",{value1:(String(s.toolbarStatus.installedVersion||"Missing / unknown")),value2:(String(s.toolbarStatus.bundledVersion||"Unknown")),value3:(String(s.toolbarStatus.status))}));paragraph("toolbar",s.toolbarStatus.message);}
  const bridgeStatus=el("p");panels.get("toolbar").append(bridgeStatus);
  async function refreshBridge(){const status=await api("/api/toolbar/bridge");setPresentationText(bridgeStatus,localizedCopy("ui.app.messageCommBusEngineProtocolRequests",{value1:(String(status.state)),value2:(String(status.engineVersion)),value3:(String(status.protocol)),value4:(String(status.requests)),value5:(String(status.replies)),value6:(String(status.lastOperation||"none")),value7:(String(status.message))}));}
  const refreshBridgeButton=el("button",localizedCopy("ui.app.messageRefreshBridgeDiagnostics"),"secondary");refreshBridgeButton.onclick=()=>command(refreshBridgeButton,refreshBridge);panels.get("toolbar").append(refreshBridgeButton);await refreshBridge();
  desktop("toolbar",localizedCopy("ui.app.messageInstallUpdateRepairToolbar"),"toolbar");
  for(const [name,value] of [[textForUI("ui.app.messageTelemetry"),s.telemetry],[textForUI("ui.app.messageSamplingInterval"),textForUI("ui.app.messageMs",{value1:(String(s.samplingIntervalMs))})],["SimConnect",s.simConnect],[textForUI("ui.app.messageLANAccess"),s.enableLan?s.lanUrl:textForUI("ui.app.messageDisabledLocalhostOnly")],[textForUI("ui.app.messageDataDirectory"),s.dataDirectory||textForUI("ui.app.messageManagedByCompanion")],...(s.pairingCode?[[textForUI("ui.app.messagePairingCode"),s.pairingCode]]:[])])paragraph("advanced",`${name}: ${value}`);
  desktop("advanced",localizedCopy("ui.app.messageLANAndDesktopConfiguration"),"settings");desktop("advanced",localizedCopy("ui.helpCenter.messageCollectFeedbackLogs"),"feedback");
  paragraph("advanced",textForUI("ui.app.messageLANAndNativeRuntimeConfigurationMayRequire"));
  const support=panels.get("support");
  support.append(el("h2",localizedCopy("nav.help")),el("p",localizedCopy("ui.app.messageNeedHelpWithAeroScienceJoinOurDiscord")));
  for(const [label,url] of [[textForUI("ui.app.messageJoinOurDiscord"),"https://discord.gg/mpYyazCqvJ"],[textForUI("ui.app.messageReportAnIssueOnGitHub"),"https://github.com/JustSeanC/AeroScience/issues"],[textForUI("ui.app.messageReadThePilotGuides"),"#help"]]){
    const link=el("a",label,"secondary");link.href=url;link.target="_blank";link.rel="noopener noreferrer";support.append(link);
  }
  paragraph("support",textForUI("ui.app.messageDiscordMembershipIsOptionalAeroScienceAndYour"));
  desktop("support",localizedCopy("ui.helpCenter.messageCollectFeedbackLogs"),"feedback");
  paragraph("support",textForUI("ui.app.messageFeedbackLogCollectionRunsOnTheWindowsCompanion"));
}
$("close-detail").addEventListener("click", () => $("mission-detail").close());
$("history-sort").addEventListener("change",()=>{historyPage=0;loadHistory().catch(error=>notice(error.message));});
$("history-search-form").addEventListener("submit",event=>{event.preventDefault();historyPage=0;loadHistory().catch(e=>notice(e.message));});
$("refresh-history").addEventListener("click", (event) =>
  command(event.target, loadHistory),
);
$("history-prev").addEventListener("click", () => {
  historyPage = Math.max(0, historyPage - 1);
  loadHistory().catch((e) => notice(e.message));
});
$("history-next").addEventListener("click", () => {
  historyPage++;
  loadHistory().catch((e) => notice(e.message));
});
window.addEventListener("hashchange", () =>
  navigate().catch((e) => notice(e.message)),
);
setInterval(() => {
  setPresentationText($("utc-clock"),localizedCopy("ui.app.messageUTC",{value1:(String(new Date().toISOString().slice(11, 19)))}));
  renderDuration();
  if (
    current?.telemetry &&
    Date.now() - Date.parse(current.telemetry.timestampUtc) > 6000
  )
    clearTelemetry();
}, 1000);
initializeResearch({ api, notice, command });
streamlineMissionControl();
const journey=scienceJourney(document.querySelector('#control .mission-panel'),api);
// The guide is created after the original controllers. Move, rather than clone,
// their authoritative controls into the primary flight workflow.
document.getElementById('science-journey-controls').append(...document.querySelectorAll('#mission-intelligence [data-collection-actions], #mission-intelligence [data-collection-command-status]'));
document.querySelector('#control .mission-panel').append($('mission-intelligence'));
const selection=missionSelection($("mission-idle"),api,{onChange:()=>{if(current)updateStartButton(current);},onSelection:(plan,card)=>journey.selection(plan,card)});
try {
  const access = await api("/api/access");
  if (access.requiresPairing) {
    $("pairing").hidden = false;
    await navigate();
  } else await connect();
} catch (e) {
  notice(e.message);
  $("pairing").hidden = false;
}
