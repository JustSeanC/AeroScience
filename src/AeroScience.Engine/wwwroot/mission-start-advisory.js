import {message as localizedCopy,setPresentationText} from './i18n.js';
import {t as textForUI} from './i18n.js';
import {node as el} from './workstation-ui.js';
import {dialogHeader} from './dialog-ui.js';

const scopeName = scope => ({entire:textForUI("ui.downloadActivity.messageEntireMission"),'first-half':textForUI("ui.downloadActivity.messageFirstHalfOfMission"),areas:textForUI("ui.missionStartAdvisory.messageCustomAreas")}[scope] || textForUI("localization.unknownValue"));
const sameLaunch = (a,b) => ['planId','name','mode','armScience'].every(key => a[key] === b[key]);
const sameRevision = (a,b) => a.planId === b.planId && a.planRevision === b.planRevision;

// All checks are read-only. The normal mission command remains the sole recording authority.
export async function startMissionWithReadiness(api, launch, {readLaunch, opener}={}) {
  async function context() {
    if (readLaunch && !sameLaunch(launch,readLaunch())) throw new Error(textForUI("ui.missionStartAdvisory.messageMissionLaunchInformationChangedContinueSetupAnd"));
    const state=await api('/api/state');
    if (state.activeMission) throw new Error('A mission is already recording. This advisory cannot start another flight.');
    if ((state.selectedPlanId || null)!==launch.planId) throw new Error(textForUI("ui.missionStartAdvisory.messageTheSelectedMissionPlanChangedContinueSetup"));
    if (!launch.planId) return null;
    try {
      const result=await api(`/api/plans/${launch.planId}/preparation/start-readiness`);
      if (result.planId!==launch.planId || !result.planRevision) throw new Error(textForUI("ui.missionStartAdvisory.messagePreparationResponseDoesNotIdentifyTheSelected"));
      return result;
    } catch {
      // A preparation outage is not a recording blocker. Still bind the saved plan version.
      const plan=await api(`/api/plans/${launch.planId}/revision`);
      if (plan.planId!==launch.planId || !plan.planRevision) throw new Error(textForUI("ui.missionStartAdvisory.messageTheSavedMissionPlanCouldNotBe"));
      return {...plan,status:'Unavailable',checkedUtc:new Date().toISOString(),
        message:textForUI("ui.missionStartAdvisory.messageScientificPreparationReadinessIsUnavailableAllReadiness")};
    }
  }
  const initial=await context();
  async function submit(value) {
    if(readLaunch&&!sameLaunch(launch,readLaunch()))throw new Error(textForUI("ui.missionStartAdvisory.messageMissionLaunchInformationChangedContinueSetupAnd"));
    return api('/api/missions',{...launch,checkSelectedPlan:true,
      ...(value?.planRevision?{expectedPlanRevision:value.planRevision}:{})});
  }
  if (!initial || ['Ready','NotRequired'].includes(initial.status)) return submit(initial);

  const dialog=el('dialog');dialog.className='preparation-review mission-start-advisory';
  setPresentationText(dialog,localizedCopy("ui.missionStartAdvisory.messageSomeScientificDataIsNotReady"),"aria-label");
  const body=el('div',undefined,'preparation-review-body');body.tabIndex=0;body.setAttribute('role','region');setPresentationText(body,localizedCopy("ui.missionStartAdvisory.messageScientificDataAdvisoryDetails"),"aria-label");body.append(el('h2',localizedCopy("ui.missionStartAdvisory.messageSomeScientificDataIsNotReady")));
  body.append(el('p',localizedCopy("ui.missionStartAdvisory.messageYouCanStartYourFlightNowFlight")),
    el('p',localizedCopy("ui.missionStartAdvisory.messageSomePlannedResearchRegionsMayNotHave")));
  const summary=el('section');setPresentationText(summary,localizedCopy("ui.missionStartAdvisory.messageCurrentExternalScientificDataReadiness"),"aria-label");body.append(summary);
  body.append(el('h3',localizedCopy("ui.missionStartAdvisory.messageRecordingCollectionAndExternalDataAreSeparate")),el('p',localizedCopy("ui.missionStartAdvisory.messageStartMissionAnywayStartsFlightRecordingSubject")),
    el('p',localizedCopy("ui.missionStartAdvisory.messageAeroScienceWillAttemptToPrepareDataFor")),
    el('p',localizedCopy("ui.missionStartAdvisory.messageDataArrivingAfterTheAircraftHasPassed")));
  const feedback=el('p');feedback.setAttribute('role','status');body.append(feedback);dialog.append(body);
  const footer=el('footer',undefined,'dialog-actions');dialog.append(footer);
  const button=(title,run)=>{const b=el('button',title);b.type='button';b.onclick=run;footer.append(b);return b;};
  let closed=false,busy=false,checking=false,invalid=false,timer,generation=0,submitting=false;
  function show(value) {
    summary.replaceChildren(el('p',value.message),el('p',localizedCopy("ui.missionStartAdvisory.messageReadinessCheckedPreparationCanChangeConfirmation",{value1:(String(new Date(value.checkedUtc).toLocaleTimeString()))})));
    const p=value.preparation;
    if (!p) {summary.append(el('p',localizedCopy("ui.missionStartAdvisory.messageExternalDataTotalReadyAwaitingAnd",{value1:(String(value.status==='Stale'?'Stale assessment':value.status==='NotAssessed'?'Not Yet Assessed':'Unavailable'))})));return;}
    const total=p.ready+p.awaiting+p.unavailable;
    summary.append(el('p',localizedCopy("ui.missionStartAdvisory.messageEntireMissionReadyPlannedInstrumentRegionUnits",{value1:(String(p.ready)),value2:(String(total)),value3:(String(p.awaiting)),value4:(String(p.unavailable)),value5:(String(value.status==='Ready'?'complete':'incomplete'))})));
    summary.append(el('p',localizedCopy("ui.missionStartAdvisory.messagePreflightScopeSelectedUnitsReady",{value1:(String(scopeName(p.scope))),value2:(String(p.selectedReady)),value3:(String(p.selectedUnits)),value4:(String(p.selectedAwaiting)),value5:(String(p.selectedUnavailable)),value6:(String(p.selectedUnits>0&&p.selectedAwaiting===0&&p.selectedUnavailable===0?'complete':'incomplete'))})));
    if (p.scope!=='entire') summary.append(el('p',localizedCopy("ui.missionStartAdvisory.messagePreparationOutsideYourPreflightSelectionMayStill")));
    const list=el('ul');for (const i of value.instruments||[]) list.append(el('li',localizedCopy("ui.missionStartAdvisory.messageReadyAwaiting",{value1:(String(i.name)),value2:(String(i.ready)),value3:(String(i.total)),value4:(String(i.awaiting)),value5:(String(i.unavailable)),value6:(String(i.awaiting||i.unavailable?' — affected instrument':''))})));summary.append(list);
    if (value.status==='Ready') summary.append(el('p',localizedCopy("ui.missionStartAdvisory.messagePreparationCompletedWhileThisAdvisoryWasOpen")));
  }
  show(initial);
  return new Promise((resolve,reject)=>{
    function invalidate(error) {invalid=true;start.disabled=true;view.disabled=true;summary.replaceChildren(el('p',localizedCopy("ui.missionStartAdvisory.messagePreviousReadinessCountsAreNoLongerCurrent")));setPresentationText(feedback,error.message);}
    async function refresh() {
      if (closed || busy || checking || invalid) return;
      checking=true;const request=++generation;
      try {const value=await context();if (closed||busy||request!==generation) return;if (!sameRevision(initial,value)) {invalidate(new Error(textForUI("ui.missionStartAdvisory.messageTheSavedPlanRevisionChangedContinueSetup")));return;}show(value);}
      catch(error) {if (!closed&&!busy&&request===generation) {summary.replaceChildren(el('p',localizedCopy("ui.missionStartAdvisory.messageCurrentExternalDataCountsAreUnknownThe")));invalidate(error);}}
      finally {checking=false;if(!closed&&!invalid&&!busy)timer=setTimeout(refresh,2500);}
    }
    const start=button(localizedCopy("ui.missionStartAdvisory.messageStartMissionAnyway"),async()=>{
      if (busy || invalid || closed) return;busy=true;++generation;clearTimeout(timer);start.disabled=true;view.disabled=true;
      try {
        const value=await context();if(closed)return;
        if(!sameRevision(initial,value)){invalidate(new Error(textForUI("ui.missionStartAdvisory.messageTheSavedPlanRevisionChangedContinueSetup")));return;}
        show(value);submitting=true;setPresentationText(feedback,localizedCopy("ui.missionStartAdvisory.messageStartingMissionUsingTheNormalRecordingChecks"));
        const mission=await submit(value);dialog.close();resolve(mission);
      } catch(error) {if(!closed){setPresentationText(feedback,error.message);submitting=false;}else reject(error);}
      finally {busy=false;if(!closed&&!invalid){start.disabled=false;view.disabled=false;clearTimeout(timer);timer=setTimeout(refresh,2500);}}
    });
    const view=button(localizedCopy("ui.missionStartAdvisory.messageViewDataPreparation"),async()=>{if(busy||invalid)return;busy=true;++generation;clearTimeout(timer);try{const value=await context();if(closed)return;if(!sameRevision(initial,value)){invalidate(new Error(textForUI("ui.missionStartAdvisory.messageTheSavedPlanRevisionChangedContinueSetup")));return;}dialog.close();location.hash='#downloads?plan='+encodeURIComponent(launch.planId);}catch(error){if(!closed)invalidate(error);}finally{busy=false;}});
    button(localizedCopy("ui.missionStartAdvisory.messageContinueSetup"),()=>dialog.close());
    dialog.addEventListener('close',()=>{closed=true;clearTimeout(timer);dialog.remove();if(!submitting)resolve(null);});
    document.body.append(dialog);dialogHeader(dialog,{title:localizedCopy("ui.missionStartAdvisory.messageSomeScientificDataIsNotReady"),opener});dialog.showModal();timer=setTimeout(refresh,2500);
  });
}
