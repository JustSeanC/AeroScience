import {message as localizedCopy,setPresentationText} from './i18n.js';
import {t as textForUI} from './i18n.js';
import {renderSensorOperating,markSensorOperatingStale} from './sensor-operating-status.js';
import {node,numeric,dateTime,informationDetails,scientificNotice} from './workstation-ui.js';
const requests=new WeakMap();
export async function operatingAssessments(parent,api,missionId=null,at=null){
 if(!missionId){const own=(requests.get(parent)||0)+1;requests.set(parent,own);try{const overview=await api('/api/instruments/operating-status');if(parent.isConnected&&requests.get(parent)===own)renderSensorOperating(parent,overview);}catch(error){if(parent.isConnected&&requests.get(parent)===own)markSensorOperatingStale(parent,textForUI("ui.operatingAssessments.messageSensorStatusUnavailable",{value1:(String(error.message))}));}return;}
 const [reports,catalog]=await Promise.all([api(`/api/missions/${missionId}/operating${at?'?at='+encodeURIComponent(at):''}`),api('/api/instruments')]);
 parent.replaceChildren();
 const heading=document.createElement('h3');setPresentationText(heading,localizedCopy("ui.operatingAssessments.messageInstrumentOperatingAssessments"));parent.append(heading);
 if(!reports.length){const p=document.createElement('p');setPresentationText(p,localizedCopy("ui.operatingAssessments.messageNotYetAssessedNoOperatingComplianceOr"));parent.append(p);return;}
 for(const report of reports){
  const card=node('article',undefined,'sensor-operating-card'),id=report.configuration.settings.instrumentId,name=catalog.find(c=>c.id===id)?.name||id;
  card.append(node('h4',name),node('p',localizedCopy("ui.operatingAssessments.messageRecordedAssessment",{value1:(String(dateTime(report.assessment.assessedUtc)))})));
  const invalid=report.assessment.rules.some(r=>r.derivationValidity==='invalid');
  if(invalid){const warning=node('p');scientificNotice(warning,'critical',{problem:localizedCopy("ui.operatingAssessments.messageRecordedGeometryOrCalibrationWasInvalid"),consequence:localizedCopy("ui.operatingAssessments.messageAffectedDerivedResultsCannotQualify"),action:localizedCopy("ui.operatingAssessments.messageInspectSensorDetailsAndTheOriginalEvidence")});card.append(warning);}
  const details=informationDetails(card,localizedCopy("ui.operatingAssessments.messageSensorDetails"),node('p',report.limitation));
  for(const rule of report.assessment.rules){
   // Historic reports remain immutable. Rejected raw AGL belongs in diagnostics,
   // never a plausible ground-intersection measurement in the ordinary results.
   const value=rule.derivationValidity==='invalid'||rule.rule.id==='geometry.ground-intersection'&&rule.derivationValidity!=='supported'?textForUI("ui.operatingAssessments.messageUnavailableSeeOriginalDiagnostics"):numeric(rule.observedValue)+' '+rule.unit;
   const label=rule.rule.id==='geometry.ground-intersection'?textForUI("ui.operatingAssessments.messageGroundPlaneHeightValidatedAGL"):rule.parameter;
   details.append(node('p',`${label}: ${value} · ${rule.derivationValidity}. ${rule.reasons.join(' ')}`));
  }
  informationDetails(card,localizedCopy("ui.operatingAssessments.messageAdvancedDiagnostics"),node('pre',JSON.stringify(report,null,2)));parent.append(card);
 }
}
