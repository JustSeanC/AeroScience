import {renderSensorOperating,markSensorOperatingStale} from './sensor-operating-status.js';
import {node,numeric,dateTime,informationDetails,scientificNotice} from './workstation-ui.js';
const requests=new WeakMap();
export async function operatingAssessments(parent,api,missionId=null,at=null){
 if(!missionId){const own=(requests.get(parent)||0)+1;requests.set(parent,own);try{const overview=await api('/api/instruments/operating-status');if(parent.isConnected&&requests.get(parent)===own)renderSensorOperating(parent,overview);}catch(error){if(parent.isConnected&&requests.get(parent)===own)markSensorOperatingStale(parent,'Sensor status unavailable: '+error.message);}return;}
 const [reports,catalog]=await Promise.all([api(`/api/missions/${missionId}/operating${at?'?at='+encodeURIComponent(at):''}`),api('/api/instruments')]);
 parent.replaceChildren();
 const heading=document.createElement('h3');heading.textContent='Instrument operating assessments';parent.append(heading);
 if(!reports.length){const p=document.createElement('p');p.textContent='Not yet assessed. No operating compliance or scientific accuracy is inferred.';parent.append(p);return;}
 for(const report of reports){
  const card=node('article',undefined,'sensor-operating-card'),id=report.configuration.settings.instrumentId,name=catalog.find(c=>c.id===id)?.name||id;
  card.append(node('h4',name),node('p','Recorded assessment · '+dateTime(report.assessment.assessedUtc)));
  const invalid=report.assessment.rules.some(r=>r.derivationValidity==='invalid');
  if(invalid){const warning=node('p');scientificNotice(warning,'critical',{problem:'Recorded geometry or calibration was invalid.',consequence:'Affected derived results cannot qualify.',action:'Inspect Sensor Details and the original evidence.'});card.append(warning);}
  const details=informationDetails(card,'Sensor Details',node('p',report.limitation));
  for(const rule of report.assessment.rules){
   // Historic reports remain immutable. Rejected raw AGL belongs in diagnostics,
   // never a plausible ground-intersection measurement in the ordinary results.
   const value=rule.derivationValidity==='invalid'||rule.rule.id==='geometry.ground-intersection'&&rule.derivationValidity!=='supported'?'Unavailable — see original diagnostics':numeric(rule.observedValue)+' '+rule.unit;
   const label=rule.rule.id==='geometry.ground-intersection'?'Ground-plane height (validated AGL)':rule.parameter;
   details.append(node('p',`${label}: ${value} · ${rule.derivationValidity}. ${rule.reasons.join(' ')}`));
  }
  informationDetails(card,'Advanced Diagnostics',node('pre',JSON.stringify(report,null,2)));parent.append(card);
 }
}
