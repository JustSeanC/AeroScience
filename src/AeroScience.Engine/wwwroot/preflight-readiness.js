import {message as localizedCopy,setPresentationText} from './i18n.js';
import {t as textForUI,t} from './i18n.js';
// Advisory only: this module never saves, records or prepares scientific sources.
export function preflightReadiness(parent, readDraft, api,{id='preflight-readiness',active=()=>true,buttonClass='',saved=false}={}) {
  const panel=document.createElement('section'); panel.id=id;
  const title=document.createElement('h3');setPresentationText(title,localizedCopy("ui.preflightReadiness.messagePreflightScienceReadiness"));
  const status=document.createElement('p');status.setAttribute('role','status');setPresentationText(status,saved?localizedCopy('missions.readinessUnassessed'):localizedCopy("ui.preflightReadiness.messageNotYetAssessedEvaluateThisDraft"));
  const results=document.createElement('div');const run=document.createElement('button');run.type='button';setPresentationText(run,localizedCopy("ui.preflightReadiness.messageEvaluateScienceReadiness"));
  run.className=buttonClass;
  panel.append(title,status,run,results);parent.append(panel);let generation=0,current=null,timer,disposed=false;
  const fingerprint=()=>JSON.stringify(readDraft());
  const invalidate=()=>{if(disposed)return;generation++;current=null;setPresentationText(status,localizedCopy("ui.preflightReadiness.messageStalePlanOrContextChangedReassess"));results.dataset.stale='true';};
  parent.addEventListener('input',invalidate);parent.addEventListener('change',invalidate);
  run.onclick=async()=>{
    if(disposed||!active())return;
    const ticket=++generation;let draft,hash;
    try {draft=readDraft();hash=JSON.stringify(draft);setPresentationText(status,localizedCopy("ui.preflightReadiness.messageAssessingDraft"));
      const response=await api('/api/plans/readiness',draft);
      if(disposed||!active()||!panel.isConnected||ticket!==generation||hash!==fingerprint())return;
      if(response?.version!==1||!Array.isArray(response.checks)||!response.checks.every(c=>['ready','caution','unavailable','notYetAssessed'].includes(c.status)&&Array.isArray(c.reasons)&&Array.isArray(c.actions))||!Array.isArray(response.limitations)||!Number.isFinite(Date.parse(response.assessedUtc))||Date.parse(response.assessedUtc)>Date.now()+2000||response.expiresUtc&&(!Number.isFinite(Date.parse(response.expiresUtc))||Date.parse(response.expiresUtc)<=Date.now()))throw Error(t('missions.invalidReadiness'));
      current=response;results.replaceChildren();delete results.dataset.stale;
      const counts=['ready','caution','unavailable','notYetAssessed'].map(value=>response.checks.filter(c=>c.status===value).length);
      setPresentationText(status,localizedCopy("ui.preflightReadiness.messageAssessedReadyCautionUnavailable",{value1:(String(new Date(response.assessedUtc).toLocaleTimeString())),value2:(String(counts[0])),value3:(String(counts[1])),value4:(String(counts[2])),value5:(String(counts[3]))}));
      for(const category of [...new Set(response.checks.map(c=>c.category))]){
        const group=document.createElement('details');group.open=response.checks.some(c=>c.category===category&&c.status==='unavailable');const heading=document.createElement('summary');setPresentationText(heading,category);group.append(heading);
        for(const check of response.checks.filter(c=>c.category===category)){
          const item=document.createElement('article');const label={ready:textForUI("ui.missionOperations.messageReady"),caution:textForUI("ui.missionOperations.messageCaution"),unavailable:textForUI("localization.unavailableValue"),notYetAssessed:textForUI("ui.preflightReadiness.messageNotYetAssessed")}[check.status]||check.status;
          const h=document.createElement('h4');setPresentationText(h,`${label} · ${check.id}`);item.append(h);
          for(const line of [...check.reasons,...check.actions]){const p=document.createElement('p');setPresentationText(p,line);item.append(p);}
          if(check.inputs?.length){const diagnostics=document.createElement('details'),title=document.createElement('summary');setPresentationText(title,localizedCopy("ui.operatingAssessments.messageAdvancedDiagnostics"));diagnostics.append(title);for(const line of check.inputs){const p=document.createElement('p');setPresentationText(p,line);diagnostics.append(p);}item.append(diagnostics);}group.append(item);
        }results.append(group);
      }
      const method=document.createElement('details'),methodTitle=document.createElement('summary');setPresentationText(methodTitle,localizedCopy("ui.preflightReadiness.messageAssessmentMethodLimitations"));method.append(methodTitle);for(const line of response.limitations){const p=document.createElement('p');setPresentationText(p,line);method.append(p);}results.append(method);
    }catch(error){if(!disposed&&ticket===generation){invalidate();setPresentationText(status,error.message);}}
  };
  async function observe(){
    if(disposed||!panel.isConnected)return;
    if(current&&active()){try{const context=await api('/api/plans/readiness/context');if(current&&(context.contextSha256!==current.contextSha256||Date.now()>=Date.parse(current.expiresUtc)))invalidate();}catch{if(current)invalidate();}}
    if(!disposed&&panel.isConnected)timer=setTimeout(observe,10000);
  }
  timer=setTimeout(observe,10000);
  return {invalidate,dispose(){disposed=true;generation++;clearTimeout(timer);parent.removeEventListener('input',invalidate);parent.removeEventListener('change',invalidate);run.onclick=null;}};
}
