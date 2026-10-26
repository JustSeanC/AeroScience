import {node,button,metric,table,chart,numeric,dateTime,link,disclosure,newIdentity} from './workstation-ui.js';

const outcomes={pass:'Pass',fail:'Fail',insufficientEvidence:'Insufficient Evidence'};
const outcome=row=>row.result?outcomes[row.result.outcome]||'Unknown':row.markedAttempt?.status==='recording'?'In Progress':row.markedAttempt?.status==='interrupted'?'Interrupted — Awaiting Evaluation':'Awaiting Evaluation';
const targets=card=>{const r=card.flightRegime;return `${numeric(r.airspeedKnots)} ± ${numeric(r.airspeedToleranceKnots)} knots IAS · ${numeric(r.altitudeMslFeet)} ± ${numeric(r.altitudeToleranceFeet)} feet MSL · ${numeric(r.holdSeconds)} second hold`;};
const identity=row=>row.kind+'/'+row.id;

export async function researchExperimentReview(parent,api,card,isCurrent=()=>true){
 const root=node('section',undefined,'panel research-panel');root.id='research-experiment-review';root.setAttribute('aria-label','Experiment overview and comparison');
 root.append(node('h2','Experiment: '+card.name));parent.replaceChildren(root);
 const status=node('p','Loading saved attempts…');status.setAttribute('role','status');root.append(status);
 const overview=node('div'),history=node('div'),comparison=node('section'),inspection=node('section');
 comparison.setAttribute('aria-label','Attempt comparison');inspection.setAttribute('aria-label','Selected attempt evidence');inspection.tabIndex=-1;
 const tools=node('div',undefined,'action-row'),selected=new Map();let offset=0,epoch=0,inspectEpoch=0;
 const active=()=>root.isConnected&&isCurrent();root.append(overview,tools,history,comparison,inspection);
 const compare=button(tools,'Compare Selected Attempts',async()=>{
  const own=++epoch,input=[...selected.values()].map(row=>({id:row.id,kind:row.kind,resultId:row.result?.id||null}));
  status.textContent='Checking the selected saved evidence…';comparison.replaceChildren();inspection.replaceChildren();inspectEpoch++;
  try{const result=await api(`/api/research/cards/${card.id}/comparison`,{attempts:input});if(!active()||own!==epoch)return;
   status.textContent=`${result.entries.length} attempts compared. Each keeps its own requirements and outcome.`;
   comparison.append(node('h3','What Changed Between Attempts?'));
   for(const text of result.notices)comparison.append(node('p',text));
   const rows=result.entries.map((entry,index)=>{
    const row=entry.attempt,audit=entry.evidence,action=node('div');button(action,'Inspect Evidence',()=>inspect(entry));
    const hold=row.result&&audit?.matchesSavedEvidence&&audit.retainedSamples>1
     ?`${numeric(row.result.longestSupportedHoldSeconds)} s${row.result.outcome==='insufficientEvidence'?' (partial, not qualified)':''}`:'Unavailable';
    return [`${index+1}. ${row.flightName} · Flight: ${dateTime(row.flightStartedUtc)} · Attempt: ${dateTime(row.startUtc)}`,`Revision ${row.card.revision} · ${targets(row.card)}`,outcome(row),hold,
     audit?`${audit.retainedSamples} retained / ${audit.expectedSamples} expected · ${audit.matchesSavedEvidence?'Matches saved evidence':'Evidence mismatch'} · ${audit.gapCount} sample gaps · ${audit.pauseOrConnectionEdges} pause/connection edges`:'Not evaluated; evidence is not scored',action];
   });
   table(comparison,'Research attempt comparison',['Flight / attempt (local time)','Frozen requirements','Saved outcome','Recorded continuous hold','Evidence availability','Review'],rows);
   const qualified=e=>e.attempt.result&&e.evidence?.matchesSavedEvidence&&e.evidence.retainedSamples>1&&e.attempt.result.outcome!=='insufficientEvidence';
   chart(comparison,'Continuous Hold Compared With Each Requirement',[
    {label:'Required hold (seconds)',backgroundColor:'#7fa4ed',data:result.entries.map((e,i)=>({x:i,y:e.attempt.card.flightRegime.holdSeconds}))},
    {label:'Verified recorded hold (seconds)',backgroundColor:'#62dec6',data:result.entries.map((e,i)=>({x:i,y:qualified(e)?e.attempt.result.longestSupportedHoldSeconds:null}))}
   ],{type:'bar',categories:result.entries.map((_,i)=>'Attempt '+(i+1)),xLabel:'Selected attempts — separate requirements',yLabel:'Continuous hold (seconds)'});
   comparison.append(node('p','The table supplies exact values and accessible chart alternatives. Unevaluated, insufficient or mismatched evidence has no achieved-hold bar. Demo evidence stays labeled; no trend or success rate is calculated.'));
  }catch(error){if(active()&&own===epoch)status.textContent=error.message;}
 });
 const selectionStatus=node('span');tools.append(selectionStatus);
 const sync=()=>{selectionStatus.textContent=`${selected.size} selected · choose 2–8 attempts, including across history pages`;compare.disabled=selected.size<2;};sync();
 button(tools,'Clear Comparison Selection',()=>{selected.clear();comparison.replaceChildren();inspection.replaceChildren();inspectEpoch++;return load();});
 button(tools,'Refresh Experiment',()=>{epoch++;selected.clear();inspection.replaceChildren();comparison.replaceChildren();inspectEpoch++;return load();});
 async function inspect(entry){
  const own=++inspectEpoch,row=entry.attempt,audit=entry.evidence;inspection.replaceChildren(node('h3',`${row.flightName} — ${outcome(row)}`),node('p',targets(row.card)));
  if(audit){
   inspection.append(node('p',`${audit.matchesSavedEvidence?'Retained sample and plan hashes match the saved result.':'Evidence is missing or changed. The saved outcome is historical and has not been rewritten.'} ${audit.retainedSamples} retained samples; ${audit.invalidSamples} paused or invalid samples. Source: ${audit.sources.map(s=>s==='simulator'?'Recorded simulator telemetry':s==='simulated'?'Synthetic/demo telemetry':'Unknown').join(', ')||'No retained telemetry'}.`));
   table(inspection,'Original attempt samples — bounded first/last preview',['Sequence','Original UTC','IAS (knots)','Altitude (feet MSL)','Aircraft','Source'],audit.preview.map(s=>[s.sequence,s.frame.timestampUtc,numeric(s.frame.indicatedAirspeedKnots),numeric(s.frame.altitudeMslFeet),s.frame.aircraft,s.frame.source]));
   table(inspection,'Recorded evidence gaps and pause edges',['Original UTC','Recorded issue','Gap (seconds)'],audit.breaks.map(b=>[b.timestampUtc,b.kind,b.seconds==null?'Not a measured duration':numeric(b.seconds)]));
   inspection.append(node('p',`${audit.gapCount} sample gaps; ${audit.pauseOrConnectionEdges} recorded pause/connection/gap events.${audit.moreBreaks?' Only the first 20 issues are listed.':''} Preview samples are not interpolated. Open replay for the full recording.`));
  }else inspection.append(node('p','No saved evidence audit is open here. A recording alone is not a successful test. Use Compare Selected Attempts to verify evaluated evidence.'));
  const actions=node('div',undefined,'action-row');inspection.append(actions);link(actions,'Open Recorded Flight Replay',`#workspace/replay?missions=${row.flightId}`);link(actions,'Open Scientific Results',`#workspace/results?missions=${row.flightId}`);
  const details=disclosure(inspection,'Technical Evidence References');details.append(node('p',`Flight ${row.flightId} · Attempt ${row.id} · ${row.startUtc} → ${row.endUtc||'Still recording'}`));
  if(row.result)details.append(node('p',`Evaluation ${row.result.id} · ${row.result.evaluatorVersion} · Original sequences ${row.result.evidence.firstSequence}–${row.result.evidence.lastSequence}`),node('p',`Evidence SHA-256 ${row.result.evidence.sha256} · Plan SHA-256 ${row.result.evidence.planSha256}`));
  inspection.append(node('p','Flight-Regime results use aircraft telemetry. Modeled science and historical external datasets remain separately identified in the original science timeline and results. No original recordings are copied.'));
  const timeline=node('div');inspection.append(timeline);inspection.focus();
  if(row.flightStatus!=='recording'){const {researchFlightTimeline}=await import('./personal-research-flight.js');if(active()&&own===inspectEpoch)await researchFlightTimeline(timeline,api,row.flightId,{attemptId:row.markedAttempt?.id,at:row.startUtc});}
 }
 async function load(){
  const own=++epoch;status.textContent='Loading experiment overview and attempt history…';compare.disabled=true;history.replaceChildren(node('p','Loading attempts…'));
  const [summary,page]=await Promise.all([api(`/api/research/cards/${card.id}/overview`),api(`/api/research/cards/${card.id}/history?offset=${offset}`)]);
  if(!active()||own!==epoch)return;overview.replaceChildren(node('h3','What Were You Trying to Accomplish?'),node('p',`Current revision ${summary.card.revision} · ${targets(summary.card)}`));
  const counts=node('div',undefined,'review-kpis');overview.append(counts);
  metric(counts,'Marked attempts',summary.markedAttempts,`${summary.postflightIntervals} distinct postflight intervals also reviewed`);
  metric(counts,'Evaluated attempts',summary.evaluatedAttempts,`${summary.savedEvaluations} saved evaluations; repeated evaluations do not create new attempts`);
  metric(counts,'Pass / Fail / Insufficient Evidence',`${summary.pass} / ${summary.fail} / ${summary.insufficientEvidence}`,'Latest saved result for each attempt, across all card revisions. Demo results remain demo evidence.');
  metric(counts,'Awaiting evaluation',summary.awaitingEvaluation,`${summary.interrupted} interrupted · ${summary.inProgress} in progress. These activity counts may overlap evaluated outcomes.`);
  overview.append(node('h3','Did the Experiment Work?'),node('p',summary.currentRevisionDemonstrated
   ?`A simulator Pass was recorded for current revision ${summary.card.revision}. Inspect evidence to verify the retained samples. This is not aircraft certification.`
   :summary.currentRevisionDemoPass?`Demo only: a saved Pass exists for revision ${summary.card.revision}. An installed simulator-flight demonstration is not established.`
   :`The required conditions are not yet demonstrated by a retained simulator Pass for current revision ${summary.card.revision}. Earlier revisions may have different requirements.`));
  if(summary.mostRecent)overview.append(node('p',`Most recent attempt: ${summary.mostRecent.flightName} · ${dateTime(summary.mostRecent.startUtc)} (local time) · revision ${summary.mostRecent.card.revision} · ${outcome(summary.mostRecent)}`));
  else overview.append(node('p','No recorded attempts or evaluated intervals yet. Select this card in Mission Planner before your next flight.'));
  overview.append(node('p','Marked attempts and postflight intervals are separate records and may overlap. Counts describe saved results across revisions, not independent trials or a statistical success rate.'));
  history.replaceChildren(node('h3','Attempt History'));
  for(const [i,row]of page.items.entries()){
   const item=node('article',undefined,'research-panel');item.setAttribute('aria-label',`Attempt ${offset+i+1} — ${row.flightName}`);item.append(node('h4',`${row.flightName} · ${dateTime(row.startUtc)} (local time)`),node('p',`Revision ${row.card.revision} · ${targets(row.card)}`),node('strong',outcome(row)),node('p',row.result?.explanation||row.markedAttempt?.endReason||'Recording is not an evaluated or successful attempt.'));
   if(selected.has(identity(row)))selected.set(identity(row),row);
   const label=node('label',`Compare attempt ${offset+i+1} — ${row.flightName}`),check=node('input');check.type='checkbox';check.checked=selected.has(identity(row));check.onchange=()=>{if(check.checked){if(selected.size>=8){check.checked=false;status.textContent='Compare at most eight attempts at once. Your retained history is not limited.';return;}selected.set(identity(row),row);}else selected.delete(identity(row));epoch++;comparison.replaceChildren();inspection.replaceChildren();inspectEpoch++;status.textContent='Selection changed. Compare Selected Attempts checks these saved results.';sync();};label.prepend(check);item.append(label);
   if(row.markedAttempt&&row.endUtc&&row.flightStatus!=='recording'&&!row.result){const requestId=newIdentity();button(item,'Evaluate Attempt',async()=>{await api(`/api/research/recorded-attempts/${row.id}/evaluate`,{requestId});selected.delete(identity(row));comparison.replaceChildren();return load();});}
   button(item,'Review Flight & Timeline',()=>inspect({attempt:row,evidence:null}));history.append(item);
  }
  if(!page.items.length)history.append(node('p','No attempts on this page. Unevaluated flights without marked attempts are not counted as tests.'));
  const navigation=node('div',undefined,'action-row');history.append(navigation);
  const previous=button(navigation,'Newer Attempts',()=>{offset=Math.max(0,offset-20);return load();});previous.disabled=offset===0;
  navigation.append(node('span',`${page.total} attempts / intervals in this experiment; ${page.items.length} on this page`));
  const next=button(navigation,'Older Attempts',()=>{offset=page.nextOffset;return load();});next.disabled=page.nextOffset==null;
  status.textContent='Choose attempts to compare their actual requirements, outcomes and evidence. No personal research is sent online.';sync();
 }
 try{await load();}catch(error){if(active())status.textContent=error.message;}
}
