import {renderSensorOperating,markSensorOperatingStale} from './sensor-operating-status.js';
const requests=new WeakMap();
export async function operatingAssessments(parent,api,missionId=null,at=null){
 if(!missionId){const own=(requests.get(parent)||0)+1;requests.set(parent,own);try{const overview=await api('/api/instruments/operating-status');if(parent.isConnected&&requests.get(parent)===own)renderSensorOperating(parent,overview);}catch(error){if(parent.isConnected&&requests.get(parent)===own)markSensorOperatingStale(parent,'Sensor status unavailable: '+error.message);}return;}
 const reports=await api(missionId?`/api/missions/${missionId}/operating${at?'?at='+encodeURIComponent(at):''}`:'/api/instruments/operating');
 parent.replaceChildren();
 const heading=document.createElement('h3');heading.textContent='Instrument operating assessments';parent.append(heading);
 if(!reports.length){const p=document.createElement('p');p.textContent='Not yet assessed. No operating compliance or scientific accuracy is inferred.';parent.append(p);return;}
 for(const report of reports){
  const details=document.createElement('details'),title=document.createElement('summary');title.textContent=report.configuration.settings.instrumentId+' · '+report.mode+' · '+report.assessment.assessedUtc;details.append(title);
  const limitation=document.createElement('p');limitation.textContent=report.limitation;details.append(limitation);
  for(const rule of report.assessment.rules){
   const p=document.createElement('p');p.textContent=`${rule.parameter}: ${rule.observedValue??'Unknown'} ${rule.unit} · Operating: ${rule.compliance} · Derived validity: ${rule.derivationValidity} · Source QA: ${rule.sourceQuality}. ${rule.reasons.join(' ')}`;details.append(p);
  }
  parent.append(details);
 }
}
