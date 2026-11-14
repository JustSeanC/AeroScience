import {message as localizedCopy,setPresentationText} from './i18n.js';
import {t as textForUI} from './i18n.js';
import {openSubmissionBuilder} from './submission-builder.js';
import {scienceMap} from './science-map.js';
import {comparison} from './science-compare.js';
import {summaryView} from './science-analysis.js';
import {coverage,imagery,replay,exports as researchExports} from './mission-workspace.js';
import {node as el,numeric as number,dateTime as local,select,button,link,disclosure,status,errorMessage,readableName} from './workstation-ui.js';
import {renderResearchContext} from './scientific-context.js';
import {terrainCompletion} from './terrain-completion.js';
import {lidarWorkflow} from './lidar-workflow.js';
import {surveyResults} from './survey-results.js';
let cleanup=()=>{},generation=0;
const pages=[['map',localizedCopy("ui.scienceWorkspace.messageScienceMap")],['compare',localizedCopy("ui.scienceAnalysis.messageCompare")],['analysis',localizedCopy("ui.scienceWorkspace.messageResearchAnalysis")],['results',localizedCopy("ui.scienceWorkspace.messageResultsExports")],['replay',localizedCopy("ui.app.messageReplayFlight")],['timeline',localizedCopy("ui.scienceWorkspace.messageEvidenceTimeline")]];
export async function scienceWorkspace(parent,api){
  const own=++generation;cleanup();const disposals=[],controller=new AbortController();
  cleanup=()=>{controller.abort();disposals.splice(0).forEach(dispose=>dispose());};
  const path=location.hash.split('?')[0].split('/')[1]||'map';
  const params=new URLSearchParams(location.hash.split('?')[1]||'');
  const ids=(params.get('missions')||'').split(',').filter(Boolean),instrument=params.get('instrument')||'';
  const title=pages.find(([id])=>id===path)?.[1]||textForUI("ui.scienceWorkspace.messageScienceMap");
  parent.replaceChildren();const heading=el('div',undefined,'page-heading');heading.append(el('div'));heading.firstChild.append(el('p',localizedCopy("ui.scienceWorkspace.messageSCIENCEWORKSPACE"),'eyebrow'),el('h2',title));parent.append(heading);

  const tools=disclosure(heading,localizedCopy("ui.scienceWorkspace.messageRelatedWorkspaces"));link(tools,localizedCopy("nav.instrumentSetup"),'#science');link(tools,localizedCopy("nav.recordedFlights"),'#history');link(tools,localizedCopy("results.library"),'#library');
  const nav=el('nav',undefined,'workspace-subnav');setPresentationText(nav,localizedCopy("ui.scienceWorkspace.messageScienceResultsAnalysisPages"),"aria-label");
  for(const [key,label] of pages){const a=link(nav,label,'#workspace/'+key+(params.size?'?'+params:''));if(path===key)a.setAttribute('aria-current','page');}parent.append(nav);
  const feedback=status(parent,localizedCopy("ui.scienceWorkspace.messageLoadingTheLocalResearchArchive"));
  const ownHash=location.hash;
  const safeApi=async(path,body,options={})=>{const value=await api(path,body,{...options,signal:options.signal||controller.signal});if(own!==generation||location.hash!==ownHash)throw Error('View changed.');return value;};
  try{
    const [missions,catalog]=await Promise.all([safeApi('/api/missions?limit=100'),safeApi('/api/instruments')]);
    for(const id of ids.filter(id=>!missions.some(m=>m.id===id)).slice(0,10)){const detail=await safeApi('/api/missions/'+id);missions.push(detail.mission);}
    if(own!==generation)return;
    const controls=el('div',undefined,'workspace-controls workspace-context');if(path==='compare')disclosure(parent,localizedCopy("ui.scienceWorkspace.messageMissionArchiveScopeOlderFlights")).append(controls);else parent.append(controls);
    const choice=select(controls,path==='map'?localizedCopy("ui.scienceWorkspace.messageMissionScope"):localizedCopy("ui.scienceWorkspace.messageReviewMission"),[["",path==='map'?localizedCopy("ui.scienceWorkspace.messageAllMissionsChooseMission"):localizedCopy("ui.scienceWorkspace.messageChooseAMission")],...missions.map(m=>[m.id,m.name+' · '+local(m.startedUtc)])],ids[0]||'');
    const sensor=select(controls,localizedCopy("ui.missionOperations.messageInstrument"),[['',localizedCopy("ui.downloadActivity.messageAllInstruments")],...catalog.map(item=>[item.id,item.name])],instrument);
    const multiple=disclosure(controls,localizedCopy("ui.scienceWorkspace.messageMultipleMissionsOlderArchive")),additional=el('select');additional.multiple=true;additional.size=4;setPresentationText(additional,localizedCopy("ui.scienceWorkspace.messageAdditionalMissions"),"aria-label");
    for(const mission of missions){const option=el('option',mission.name);option.value=mission.id;option.selected=ids.slice(1).includes(mission.id);additional.append(option);}multiple.append(el('p',localizedCopy("ui.scienceWorkspace.messageThePickerListsRecentFlightsOpenOlder")),additional);link(multiple,localizedCopy("ui.scienceWorkspace.messageSearchMissionHistory"),'#history');
    const change=()=>{const query=new URLSearchParams(params),list=[choice.value,...[...additional.selectedOptions].map(option=>option.value)].filter(Boolean);if(list.length)query.set('missions',[...new Set(list)].join(','));else query.delete('missions');if(sensor.value)query.set('instrument',sensor.value);else query.delete('instrument');query.delete('measurement');location.hash='#workspace/'+path+(query.size?'?'+query:'');};
    choice.onchange=()=>{if(!choice.value)for(const option of additional.options)option.selected=false;change();};sensor.onchange=additional.onchange=change;
    feedback.remove();const body=el('section',undefined,'science-page');parent.append(body);let researchCards=[];
    if(ids.length===1&&path!=='compare'){
      const frozen=await safeApi(`/api/missions/${ids[0]}/research-operations`);researchCards=frozen.cards.map(row=>row.card);
      if(frozen.cards.length){const context=el('section',undefined,'panel science-research-objectives');setPresentationText(context,localizedCopy("ui.scienceWorkspace.messageRecordedResearchObjectives"),"aria-label");body.append(context);for(const [index,row]of frozen.cards.entries()){const target=index===0?context:disclosure(context,localizedCopy("ui.scienceWorkspace.messageFrozenRevision",{value1:(String(row.card.name)),value2:(String(row.card.revision))}));renderResearchContext(target,row.card);}}
    }
    if(path==='map'){await scienceMap(body,safeApi,params,ids,instrument,disposals,catalog,researchCards);return;}
    if(path==='compare'){await comparison(body,safeApi,missions,ids,disposals,catalog);return;}
    if(!choice.value){const empty=el('div',undefined,'panel empty-state');empty.append(el('h2',localizedCopy("ui.scienceWorkspace.messageChooseARecordedMission")),el('p',localizedCopy("ui.scienceWorkspace.messageReviewSavedScientificMeasurementsReplayTheFlight")));link(empty,localizedCopy("ui.scienceWorkspace.messageFindAMission"),'#history','button primary');body.append(empty);return;}
    const id=choice.value,summary=await safeApi(`/api/missions/${id}/science-summary`);
    body.append(el('h2',summary.mission.name));
    if(summary.mission.status!=='recording'){try{const eligible=await safeApi(`/api/submissions/choices?flightId=${id}`);if(eligible.choices.length)button(body,localizedCopy("ui.organizationAssignmentUi.messageSubmitEvidence"),()=>openSubmissionBuilder(api,{flightId:id,suggestedProductId:params.get('product')||undefined}));}catch(error){if(error.name==='AbortError'||error.message==='View changed.')throw error;}}
    if(summary.mission.status!=='recording'){const research=await safeApi(`/api/missions/${id}/research`);const starter=research.planSnapshot?.researchStarter?.starterId;if(starter==='experimental-sensor-checkout')link(body,localizedCopy("ui.personalResearch.messageReviewSensorCheckout"),`#my-research?flight=${id}`);if(starter==='scientific-observation-flight')link(body,localizedCopy("ui.personalResearch.messageReviewScientificObservations"),`#my-research?flight=${id}`);if(starter==='terrain-temperature-survey')link(body,localizedCopy("ui.personalResearch.messageReviewTerrainTemperatureSurvey"),`#my-research?flight=${id}`);if(['vertical-atmospheric-profile','atmospheric-transect'].includes(starter))link(body,starter==='atmospheric-transect'?localizedCopy("ui.personalResearch.messageReviewAtmosphericTransect"):localizedCopy("ui.personalResearch.messageReviewAtmosphericProfile"),`#my-research?flight=${id}`);}
    await downlink(body,id,safeApi,disposals,catalog);
    if(path==='replay'){await replay(body,id,summary.mission,safeApi,{},stop=>disposals.push(stop));return;}
    if(path==='analysis'){await summaryView(body,summary,safeApi,instrument,{disposals,catalog,analysis:true});return;}
    if(path==='results'&&summary.mission.status!=='recording'){
      let result;try{result=await surveyResults(body,safeApi,{id,summary,params,disposals});}catch(error){if(error.name==='AbortError'||error.message==='View changed.')throw error;errorMessage(body,error);}
      const preparation=result?.legacy?body:disclosure(body,localizedCopy("ui.scienceWorkspace.messagePrepareSourceBackedTerrainAndHistoricalLiDAR"));
      const terrain=el('div');preparation.append(terrain);
      try{await terrainCompletion(terrain,id,safeApi,disposals);}catch(error){if(error.name==='AbortError'||error.message==='View changed.')throw error;errorMessage(terrain,error);}
      const lidar=el('div');preparation.append(lidar);
      try{await lidarWorkflow(lidar,id,safeApi,disposals);}catch(error){if(error.name==='AbortError'||error.message==='View changed.')throw error;errorMessage(lidar,error);}
    }
    const exports=el('section',undefined,'panel export-workspace');body.append(exports);
    exports.append(el('h3',localizedCopy("ui.scienceWorkspace.messageTakeYourResearchWithYou")),el('p',summary.review.provisional?localizedCopy("ui.scienceWorkspace.messageScienceProcessingIsIncompleteExportsAreLabeled"):localizedCopy("ui.scienceWorkspace.messageExportRecordedFlightAndScienceWithoutChanging")));
    if(summary.mission.status!=='recording')researchExports(exports,id);else exports.append(el('p',localizedCopy("ui.scienceWorkspace.messageEndTheMissionBeforeExportingACompleted")));
    const review=disclosure(body,localizedCopy("ui.scienceWorkspace.messageRecordedInstrumentsAndSourceQuality"));if(path==='results'&&params.get('view')==='evidence')review.open=true;await summaryView(review,summary,safeApi,instrument,{disposals,catalog});
    const technical=disclosure(body,localizedCopy("ui.scienceWorkspace.messageAdvancedCoverageNativeGridCompositesAndRetained"));let built=false;
    technical.addEventListener('toggle',async()=>{if(technical.open&&!built){built=true;try{await coverage(technical,id,safeApi,{});await imagery(technical,id,safeApi);}catch(error){built=false;errorMessage(technical,error);}}});
  }catch(error){if(error.name==='AbortError'||error.message==='View changed.')return;feedback.remove();errorMessage(parent,error,()=>scienceWorkspace(parent,api));}
}
async function downlink(parent,id,api,disposals,catalog){
  const box=el('section',undefined,'panel science-review-status');setPresentationText(box,localizedCopy("ui.scienceWorkspace.messageScientificReviewStatus"),"aria-label");parent.append(box);
  const title=el('h3'),summary=el('p'),actions=el('div',undefined,'action-row'),advanced=disclosure(box,localizedCopy("ui.scienceWorkspace.messageAdvancedRecoveryEvidenceAndLimitations"));box.prepend(title,summary,actions);
  let stopped=false,timer,lastAnnouncement='';const announcement=status(box,'');announcement.classList.add('sr-only');disposals.push(()=>{stopped=true;clearTimeout(timer);});
  async function render(){
    if(stopped||!box.isConnected)return;const data=await api(`/api/missions/${id}/downlink`);if(stopped)return;
    setPresentationText(title,data.review.provisional?(data.review.flightStatus==='Recording'?localizedCopy("ui.scienceWorkspace.messageFlightRecordingSciencePending"):localizedCopy("ui.scienceWorkspace.messageFlightCompleteDownlinkingScienceData")):localizedCopy("ui.scienceWorkspace.messageScientificReview",{value1:(String(data.review.status))}));
    setPresentationText(summary,data.review.provisional?localizedCopy("ui.scienceWorkspace.messageRecoveredStillPendingMapsStatistics",{value1:(String(data.review.recovered)),value2:(String(data.review.pending))}):localizedCopy("ui.scienceWorkspace.messageRecoveredPermanentGapsCancelled",{value1:(String(data.review.recovered)),value2:(String(data.review.permanentGaps)),value3:(String(data.review.cancelled))}));
    if(title.textContent!==lastAnnouncement){lastAnnouncement=title.textContent;setPresentationText(announcement,lastAnnouncement);}actions.replaceChildren();
    const states=data.items.map(item=>item.state);
    const control=async action=>{if(action==='cancel'&&!confirm(localizedCopy("ui.scienceWorkspace.messageCancelRemainingPostFlightScienceProcessingRecordedFlight")))return;await api(`/api/missions/${id}/downlink/control`,{action});await render();};
    if(data.review.pending||data.review.cancelled){if(states.includes('Paused'))button(actions,localizedCopy("ui.scienceWorkspace.messageResumeDownlink"),()=>control('resume'));else button(actions,localizedCopy("ui.scienceWorkspace.messagePauseDownlink"),()=>control('pause'));button(actions,localizedCopy("ui.scienceWorkspace.messageCancelDownlink"),()=>control('cancel'),'danger');}
    advanced.replaceChildren(el('summary',localizedCopy("ui.scienceWorkspace.messageAdvancedRecoveryEvidenceAndLimitations")),el('p',data.review.message));
    const groups=new Map();for(const item of data.items){if(!groups.has(item.instrumentId))groups.set(item.instrumentId,[]);groups.get(item.instrumentId).push(item);}
    for(const [instrument,slots] of groups)advanced.append(el('p',localizedCopy("ui.scienceWorkspace.messageAcquisitionSlotsOnThisPage",{value1:(String(readableName(instrument,catalog))),value2:(String(slots.length)),value3:(String([...new Set(slots.map(item=>item.state))].join(', ')))})));
    advanced.append(el('pre',JSON.stringify(data.items.map(item=>({id:item.id,instrument:item.instrumentId,flightUtc:item.flightUtc,state:item.state,reason:item.reason})),null,2)));
    if(!stopped)timer=setTimeout(()=>render().catch(error=>{if(error.name!=='AbortError')errorMessage(box,error,render);}),5000);
  }
  await render();
}
export function disposeScienceWorkspace(){generation++;cleanup();}
