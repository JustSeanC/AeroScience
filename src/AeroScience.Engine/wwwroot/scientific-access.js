import {presentationTextNode} from './i18n.js';
import {message as localizedCopy,setPresentationText} from './i18n.js';
import {t as textForUI} from './i18n.js';
import {informationDetails} from './workstation-ui.js';
import {renderInstrumentSummaries,instrumentSummaries} from './preparation-summary.js';
import {openOnlineAccess} from './online-access.js';
import {openDownloadActivity,allowanceText} from "./download-activity.js";
import { openHyperspectralImport } from "./hyperspectral-import.js";
import { planningDistance } from "./science-display.js";
const node = (tag, text) => { const n = document.createElement(tag); if (text !== undefined) setPresentationText(n,text); return n; };
const bytes = n => n == null ? textForUI("localization.unknownValue") : textForUI("ui.hyperspectralImport.messageMiB",{value1:(String((n / 1048576).toFixed(1)))});
function button(label, run) { const b = node("button", label); b.type = "button"; b.className = "secondary"; b.onclick = async () => { b.disabled = true; try { await run(); } catch (e) { alert(e.message); } finally { b.disabled = false; } }; return b; }
const titles = { get terrain(){return textForUI("ui.scientificAccess.messageTerrainElevation");}, get thermal(){return textForUI("ui.missionWorkspace.messageThermal");}, get multispectral(){return textForUI("ui.scientificAccess.messageMultispectral");}, get hyperspectral(){return textForUI("ui.missionWorkspace.messageHyperspectral");} };
export async function scientificAccessSettings(parent, api) {
  const section = node("section"); section.className = "panel research-panel";
  section.append(node("h2", localizedCopy("ui.scientificAccess.messageAutomaticScientificDataAccess"))); parent.append(section);
  const { configuration, canConfigure } = await api("/api/science/access");
  section.append(node("p", configuration.privacy));informationDetails(section,localizedCopy("ui.scientificAccess.messageScientificSourceTypes"),node("p",localizedCopy("ui.scientificAccess.messageHistoricalSatelliteAndElevationDataAreExternal")));
  if (configuration.warning) section.append(node("p", configuration.warning));
  const form = node("form"); const p = configuration.preferences;let fields=form;
  function checkbox(label, checked) { const l = node("label"); const input = node("input"); input.type = "checkbox"; input.checked = checked; input.disabled = !canConfigure; l.append(input, presentationTextNode(" " + label)); fields.append(l, node("br")); return input; }
  function input(label, value, type = "number") { const l = node("label", label + " "); const n = node("input"); n.type = type; n.value = value ?? ""; n.disabled = !canConfigure; l.append(n); fields.append(l, node("br")); return n; }
  const enabled = checkbox(localizedCopy("ui.scientificAccess.messageAllowAutomaticScientificDataDiscoveryAndDownloads"), p.onlineEnabled);
  const consent = checkbox(localizedCopy("ui.scientificAccess.messageIUnderstandAndAcceptTheSeparateScientificData"), p.privacyAccepted);
  const unmetered = checkbox(localizedCopy("ui.scientificAccess.messageDownloadOnUnmeteredNetworksOnlyUnknownNetwork"), p.unmeteredOnly);
  section.append(node("p", localizedCopy("ui.scientificAccess.messageWindowsNetworkCost",{value1:(String(configuration.networkCost))})));
  const background = checkbox(localizedCopy("ui.scientificAccess.messageAllowBoundedBackgroundRetrievalAroundTheActive"), p.backgroundRetrieval);
  const advanced=informationDetails(form,localizedCopy("ui.scientificAccess.messageAdvancedTransferSourceSelection"));fields=advanced;
  const maximum = input(localizedCopy("ui.scientificAccess.messageMaximumNetworkTransferPerPreparationMiB"), p.maximumDownloadBytes / 1048576); maximum.min = 1; maximum.max = 512;
  const nasaMaximum = input(localizedCopy("ui.scientificAccess.messageNASAOriginalSceneDownloadCapMiB"), (p.nasaOriginalMaximumBytes ?? 1073741824) / 1048576); nasaMaximum.min = 1; nasaMaximum.max = 20480;
  advanced.append(node("p", localizedCopy("ui.scientificAccess.messageNASADownloadImportUsesCompleteOriginal")));
  const lookahead=input(localizedCopy("ui.scientificAccess.messageAdvancedLookaheadSeconds0AdaptiveMaximum"),p.lookaheadSeconds??0);lookahead.min=0;lookahead.max=900;
  const hourly=input(localizedCopy("ui.scientificAccess.messageOverallScientificTransferBudgetPerRollingHour"),(p.prefetchHourlyBytes??268435456)/1048576);hourly.min=1;hourly.max=20480;
  const unlimited=checkbox(localizedCopy("ui.scientificAccess.messageUnlimitedInFlightHourlyTransferAllowance"),p.prefetchHourlyBytes==null);
  const preflight=input(localizedCopy("ui.scientificAccess.messagePreflightTransferAllowanceMiBPerApplicationSession"),(p.preflightTransferBytes??1073741824)/1048576);preflight.min=1;preflight.max=20480;
  const preUnlimited=checkbox(localizedCopy("ui.scientificAccess.messageUnlimitedPreflightTransferAllowance"),p.preflightTransferBytes===null);
  const toggle=()=>{hourly.disabled=!canConfigure||unlimited.checked;preflight.disabled=!canConfigure||preUnlimited.checked;};unlimited.onchange=preUnlimited.onchange=toggle;toggle();
  advanced.append(node("p",localizedCopy("ui.scientificAccess.messageUnlimitedCanUseSubstantialNetworkDataThese")));
  const concurrent = input(localizedCopy("ui.scientificAccess.messageConcurrentPreparations"), p.concurrentDownloads); concurrent.min = 1; concurrent.max = 8;
  advanced.append(node("p",localizedCopy("ui.scientificAccess.messageOnePreparationSlotIsTheConservativeDefault")));
  const days = input(localizedCopy("ui.scientificAccess.messageSearchLookbackDays"), p.lookbackDays); days.min = 1; days.max = 3650;
  const cloud = input(localizedCopy("ui.scientificAccess.messageMaximumSceneCloudCoverLocal"), p.maximumCloudPercent); cloud.min = 0; cloud.max = 100;
  const date = input(localizedCopy("ui.scientificAccess.messagePreferredLatestObservationDateBlankNow"), p.preferredDateUtc?.slice(0, 10), "date");
  const policy = node("select"); setPresentationText(policy,localizedCopy("ui.scientificAccess.messageSourceDateQualityPolicy"),"aria-label"); for (const [value, label] of [["clear-recent", textForUI("ui.scientificAccess.messageClearestScenesWithinTheDateWindow")], ["newest", textForUI("ui.scientificAccess.messageNewestAcceptableScene")]]) { const o = node("option", label); o.value = value; o.selected = value === p.selectionPolicy; policy.append(o); } policy.disabled = !canConfigure; advanced.append(node("label", localizedCopy("ui.scientificAccess.messageSourceDateQualityPolicybe8dc161")), policy);
  const result = node("p");
  const combinedAccess=button(localizedCopy("ui.scientificAccess.messageReviewCombinedOnlineAccess"),()=>openOnlineAccess(api,{opener:combinedAccess,onAllowed:state=>{
    if(!section.isConnected)return;
    enabled.checked=state.science.configuration.preferences.onlineEnabled;
    consent.checked=state.science.configuration.preferences.privacyAccepted;
    setPresentationText(result,localizedCopy("ui.scientificAccess.messageOnlineMapsScientificAccessAndAutomaticFAATFR"));
  }}));
  section.append(combinedAccess);informationDetails(section,localizedCopy("ui.scientificAccess.messageCombinedOnlineAccess"),node("p",localizedCopy("ui.scientificAccess.messageOneDisclosureCanAllowMapsScientificAccess")));
  form.append(node("br"), button(localizedCopy("ui.scientificAccess.messageSaveScientificAccessPreferences"), async () => { if (!form.reportValidity()) return; await api("/api/science/access", { ...(await api("/api/science/access")).configuration.preferences, onlineEnabled: enabled.checked, privacyAccepted: consent.checked, unmeteredOnly: unmetered.checked, backgroundRetrieval: background.checked, maximumDownloadBytes: Math.round(Number(maximum.value) * 1048576), nasaOriginalMaximumBytes: Math.round(Number(nasaMaximum.value) * 1048576), concurrentDownloads: Number(concurrent.value), lookaheadSeconds:Number(lookahead.value),prefetchHourlyBytes:unlimited.checked?null:Math.round(Number(hourly.value)*1048576),preflightTransferBytes:preUnlimited.checked?null:Math.round(Number(preflight.value)*1048576), lookbackDays: Number(days.value), maximumCloudPercent: Number(cloud.value), preferredDateUtc: date.value ? date.value + "T23:59:59Z" : null, selectionPolicy: policy.value }); setPresentationText(result,enabled.checked ? localizedCopy("ui.scientificAccess.messageSavedEnableAnInstrumentAndPrepareA") : localizedCopy("ui.scientificAccess.messageOfflineOnlyScientificOperationSavedRetainedSourcesAnd")); }), button(localizedCopy("ui.scientificAccess.messageCancelCurrentDownloads"), () => api("/api/science/prepare/cancel", {})), result);
  if (!canConfigure) form.append(node("p", localizedCopy("ui.scientificAccess.messageChangeScientificAccessControlsInTheCompanion")));
  section.append(form, node("p", localizedCopy("ui.scientificAccess.messagePreparedSourceSubsetsAreRetainedScientificOriginals")));
  fields=informationDetails(form,localizedCopy("ui.scientificAccess.messageAdvancedPrepareASmallArea"));
  const lat = input(localizedCopy("ui.scientificAccess.messagePrepareLatitude"), "", "number"), lon = input(localizedCopy("ui.scientificAccess.messagePrepareLongitude"), "", "number"); lat.step = lon.step = "any"; lat.min = -80; lat.max = 84; lon.min = -180; lon.max = 179.9999;
  fields.append(button(localizedCopy("ui.scientificAccess.messagePrepareASmallArea"), async () => { if (!lat.value || !lon.value || !form.reportValidity()) { setPresentationText(result,localizedCopy("ui.scientificAccess.messageEnterAValidLatitudeAndLongitude")); return; } await api("/api/science/prepare", { latitude: Number(lat.value), longitude: Number(lon.value) }); setPresentationText(result,localizedCopy("ui.scientificAccess.messageSmallAreaTerrainThermalAndMultispectralPreparationsRequested")); }));
  await scientificReadiness(section, api);
}
export async function scientificReadiness(parent, api, map = null) {
  const section=node("section");section.className="panel research-panel scientific-readiness";
  const heading=node("h3",localizedCopy("ui.scientificAccess.messageScientificDataPreparation")),status=node("p"),warning=node("p"),summaries=node("div"),help=node("a",localizedCopy("ui.scientificAccess.messageConfigureScientificDataAccess")),advanced=node("details");help.href="#settings/science?section=access";
  advanced.append(node("summary",localizedCopy("ui.scientificAccess.messageAdvancedIndividualRegionsDiagnostics")),button(localizedCopy("ui.scientificSources.messageViewDownloadActivity"),()=>openDownloadActivity(api)));
  section.append(heading,status,summaries,warning,help,advanced);informationDetails(section,localizedCopy("ui.scientificAccess.messageSourceReadinessCollection"),node("p",localizedCopy("ui.scientificAccess.messageReadinessIsSeparateFromValidPixelSurveyCoverage")));parent.append(section);
  let timer;
  async function refresh(){if(!section.isConnected){clearTimeout(timer);return;}
    if(!document.hidden&&!section.closest("[hidden]")){try{
      const [state,prediction,access,plans]=await Promise.all([api("/api/state"),api("/api/science/prediction"),api("/api/science/access"),api("/api/science/plan-readiness")]);if(!section.isConnected)return;
      const selected=state.activeMission?state.research?.planId:state.selectedPlanId;
      const plan=plans.find(p=>p.planId===selected);
      if(selected){
        const definition=await api(`/api/plans/${selected}`);setPresentationText(heading,localizedCopy("ui.downloadActivity.messageDataPreparation",{value1:(String(definition.name))}));
        const items=await api(`/api/plans/${selected}/preparation/instruments`);
        renderInstrumentSummaries(summaries,items.length?items:(definition.instruments||[]).filter(c=>c.enabled).map(c=>({instrumentId:c.instrumentId,name:c.instrumentId,status:["terrain-scanner","thermal-imager","multispectral-imager","hyperspectral-imager"].includes(c.instrumentId)?"Not Prepared":"Live simulator telemetry required; no remote preparation",total:0,ready:0,awaiting:0,unavailable:0})));
        setPresentationText(status,plan?localizedCopy("ui.scientificAccess.messageReadyAwaitingUnavailable",{value1:(String(plan.status)),value2:(String(plan.ready)),value3:(String(plan.awaiting)),value4:(String(plan.unavailable))}):localizedCopy("ui.scientificAccess.messagePlanSelectedNotPreparedSelectingA"));
        if(map&&plan){const regions=await api(`/api/plans/${selected}/preparation/regions`);map.setReadiness(regions);}
      }else{setPresentationText(heading,state.activeMission?localizedCopy("ui.scientificAccess.messageDataPreparationActiveFlight"):localizedCopy("ui.scientificAccess.messageScientificDataPreparation"));renderInstrumentSummaries(summaries,instrumentSummaries(prediction.areas));setPresentationText(status,prediction.areas.length?localizedCopy("ui.scientificAccess.messageActiveFlightCorridorSourceReadinessDoesNot"):localizedCopy("ui.scientificAccess.messageNoFlightCorridorOrMissionPreparationSelected"));map?.setReadiness(prediction.areas);}
      setPresentationText(warning,prediction.lookaheadSeconds>0&&(prediction.readyAheadSeconds??0)<prediction.queue.estimatedPreparationSeconds?localizedCopy("ui.scientificAccess.messageAircraftMayOutrunSourcePreparationFlightRecording"):"");
      help.hidden=access.configuration.preferences.onlineEnabled;
    }catch(e){setPresentationText(warning,e.message);}}
    timer=setTimeout(refresh,3000);
  }await refresh();return section;
}
