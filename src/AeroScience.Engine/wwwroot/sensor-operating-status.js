import {node, dateTime} from './workstation-ui.js';

const operatingLabels={withinCheckedLimits:'Within Checked Limits',caution:'Caution',cannotAssess:'Cannot Assess',notCollecting:'Not Collecting',disabled:'Disabled'};
const dataLabels={ready:'Ready',partiallyAvailable:'Partially Available',unavailable:'Unavailable',pending:'Pending',unknown:'Unknown'};
const views=new WeakMap();
const text=(element,value)=>{if(element.textContent!==value)element.textContent=value;};

// Both pages consume the same typed backend projection. No rule/message interpretation here.
export function renderSensorOperating(parent,overview,{compact=false,stale=null}={}){
 let view=views.get(parent);
 if(!view){
  parent.classList.add('sensor-operating-panel');parent.setAttribute('aria-label','Sensor Operating Status');
  const heading=node('h2','Sensor Operating Status'),notice=node('p'),cards=node('div',undefined,'sensor-operating-grid'),disabled=node('details'),disabledTitle=node('summary','Disabled instruments'),disabledCards=node('div',undefined,'sensor-operating-grid');
  disabled.append(disabledTitle,disabledCards);parent.replaceChildren(heading,notice,cards,disabled);
  view={notice,cards,disabled,disabledCards,disabledTitle,items:new Map(),timer:null};views.set(parent,view);
 }
 clearTimeout(view.timer);view.compact=compact;
 parent.classList.toggle('sensor-operating-compact',compact);
 if(!overview||overview.version!==1){const message=stale||'Sensor status is unavailable or unsupported. No operating compliance or source readiness is assumed.';if(view.overview?.version===1){renderSensorOperating(parent,view.overview,{compact,stale:message});return;}text(view.notice,message);return;}
 view.overview=overview;
 const age=Date.now()-Date.parse(overview.generatedUtc);
 if(!stale&&(age>6000||age< -2000))stale='Status response is stale. Reconnect and wait for current evidence.';
 text(view.notice,(stale?stale+' ':'')+'Supported operating checks only — not physical hardware health or certification. Scientific data availability is assessed separately.');
 const remaining=new Set(view.items.keys());let enabled=0,disabledCount=0;
 for(const card of overview.instruments){
  let item=view.items.get(card.instrumentId);
  if(!item){
   const root=node('article',undefined,'sensor-operating-card'),name=node('h3'),profile=node('p'),session=node('p'),operating=node('strong'),issue=node('p'),guidance=node('p'),uncertainty=node('p',undefined,'sensor-operating-uncertainty'),data=node('strong'),source=node('p'),acquisition=node('p'),time=node('p',undefined,'footnote');
   root.dataset.sensor=card.instrumentId;root.setAttribute('aria-label',card.name);
   const opBlock=node('section'),dataBlock=node('section');opBlock.setAttribute('aria-label',card.name+' Operating Conditions');dataBlock.setAttribute('aria-label',card.name+' Scientific Data');opBlock.append(operating,issue,guidance,uncertainty);dataBlock.append(data,source,acquisition);
   const advice=node('ul');opBlock.append(advice);
   const details=node('details'),summary=node('summary','View Technical Details · '+card.instrumentId),technical=node('div');details.append(summary,technical);
   root.append(name,profile,session,opBlock,dataBlock,time,details);item={root,name,profile,session,operating,issue,guidance,uncertainty,advice,data,source,acquisition,time,details,technical};view.items.set(card.instrumentId,item);
  }
  remaining.delete(card.instrumentId);const holder=card.enabled?view.cards:view.disabledCards;if(item.root.parentNode!==holder)holder.append(item.root);if(card.enabled)enabled++;else disabledCount++;
  const status=stale&&card.operatingStatus!=='disabled'&&card.operatingStatus!=='notCollecting'?'cannotAssess':card.operatingStatus;
  item.root.dataset.operating=status;text(item.name,card.name);text(item.profile,`Reference profile: ${card.profileName}${card.profileVersion==null?'':` · version ${card.profileVersion}`}`);
  text(item.session,`Collection: ${stale?'Last known — ':''}${card.collectionState}${card.sessionId?' · Current science session':''}`);
  text(item.operating,'Operating Conditions: '+(operatingLabels[status]||'Cannot Assess'));
  text(item.issue,stale?'Live evidence is no longer current. Earlier favorable results are not current operating clearance.':card.primaryIssue);
  text(item.guidance,stale?'Check the companion connection and wait for fresh evidence.':card.guidance||'');item.guidance.hidden=!item.guidance.textContent;
  text(item.uncertainty,[`${card.checkedRules} checked operating rules; ${card.unassessedRules} applicable rules unassessed.`,...(compact?['Other capabilities, including undocumented hardware limits and detector sampling, remain unassessed.']:(card.uncertainty||[]))].join(' '));
  const advice=JSON.stringify(stale?[]:card.additionalAdvice||[]);if(item.advice.dataset.key!==advice){item.advice.dataset.key=advice;item.advice.replaceChildren(...JSON.parse(advice).map(value=>node('li',value)));}item.advice.hidden=!item.advice.children.length;
  text(item.data,'Scientific Data: '+(stale?'Unknown':dataLabels[card.scientificData.status]||'Unknown'));
  text(item.source,stale?'The previous source assessment is not current; no new measurement success is inferred.':card.scientificData.explanation);
  text(item.acquisition,card.scientificData.acquisitionMessage?'Acquisition context: '+card.scientificData.acquisitionMessage:'');item.acquisition.hidden=!item.acquisition.textContent;
  text(item.time,`Last assessment: ${dateTime(card.assessedUtc)} · Evidence: ${stale?'stale':card.evidenceFresh?'fresh':'missing, stale or from before a collection change'}`);
  // Preserve the details/summary nodes and keyboard focus between telemetry refreshes.
  const key=JSON.stringify([card.report,card.configuration,card.evidenceFresh,stale]);
  if(item.technical.dataset.key!==key){item.technical.dataset.key=key;item.technical.replaceChildren();
   const c=card.configuration,r=card.report;
   item.technical.append(node('p',`Effective configuration ${c?.id||'Unknown'} · revision ${c?.revision??'Unknown'} · effective ${dateTime(c?.effectiveUtc)} · current session ${card.sessionId||'none'}.`),node('p',`Assessment ${dateTime(card.assessedUtc)} · original telemetry ${dateTime(card.evidenceUtc)} · ${stale?'stale':card.evidenceFresh?'fresh':'not current'}.`));
   if(c){item.technical.append(node('p',`Selected profile: ${card.profileName} · ${c.settings.payload?.profile.id||'no hardware profile'} · version ${card.profileVersion??'Unknown'}. Saved acquisition interval ${c.settings.intervalMs} ms.`),node('p',`Calibration: ${c.calibration?.status||'unknown'} · ${c.calibration?.id||'no definition'} · revision ${c.calibration?.revision??'Unknown'} · author ${c.calibration?.author||'not supplied'} · defined ${dateTime(c.calibration?.definedUtc)}.`));for(const p of c.calibration?.parameters||[])item.technical.append(node('p',`${p.id}: ${p.value} ${p.unit}. ${p.basis}`));for(const value of [...(c.calibration?.references||[]),...(c.calibration?.limitations||[]),...(c.settings.payload?.profile.references||[])])item.technical.append(node('p',value));}
   if(!r)item.technical.append(node('p','No report for this session and effective configuration. Previous session/configuration evidence remains in archived operating reports.'));
   else {item.technical.append(node('p',`${r.mode} · ${r.policy} · configuration ${r.assessment.configurationId} · recorded session ${r.assessment.sessionId||'none'}. ${r.limitation}`));
    const f=r.input.frame;if(f)item.technical.append(node('p',`Original aircraft input: ${f.altitudeAglFeet??'Unknown'} ft AGL · ${f.altitudeMslFeet} ft MSL · ${f.groundspeedKnots} kt groundspeed · pitch ${f.pitchDegrees}° · bank ${f.bankDegrees}°. Position ${f.latitude}, ${f.longitude}; ${dateTime(f.timestampUtc)}.`));
    for(const rule of r.assessment.rules)item.technical.append(node('p',`${rule.parameter}: ${rule.observedValue??'Unknown'} ${rule.unit} · Operating: ${rule.compliance} · Derived validity: ${rule.derivationValidity} · Source QA: ${rule.sourceQuality}. Rule ${rule.rule.id} revision ${rule.rule.revision}. ${rule.reasons.join(' ')}`));
    for(const evidence of r.assessment.evidence)item.technical.append(node('p',`Evidence ${evidence.id} · ${evidence.origin} · ${dateTime(evidence.observedUtc)} · SHA-256 ${evidence.sha256}`));
   }
  }
 }
 for(const id of remaining){view.items.get(id).root.remove();view.items.delete(id);}
 view.disabled.hidden=!disabledCount;text(view.disabledTitle,`Disabled instruments (${disabledCount})`);
 if(!enabled&&!stale)text(view.notice,'No instruments enabled. Enable an instrument to assess supported checks. Disabled instruments remain listed below.');
 if(!stale){const expires=Math.min(Date.parse(overview.generatedUtc)+6000,...overview.instruments.filter(c=>c.evidenceFresh&&c.operatingStatus!=='notCollecting'&&c.enabled).flatMap(c=>[Date.parse(c.assessedUtc)+6000,Date.parse(c.evidenceUtc)+6000]));
  view.timer=setTimeout(()=>{if(parent.isConnected)markSensorOperatingStale(parent,'Live sensor evidence has expired.');},Math.max(20,expires-Date.now()+20));}
}
export function markSensorOperatingStale(parent,message='Sensor status unavailable or stale.'){
 const view=views.get(parent);renderSensorOperating(parent,view?.overview,{compact:view?.compact,stale:message});
}
