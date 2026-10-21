export async function operatingAssessments(parent,api,missionId=null,at=null){
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
