import {node,button,table,chart,link,numeric} from './workstation-ui.js';
const ruleKeys=['airspeedKnots','airspeedToleranceKnots','altitudeMslFeet','altitudeToleranceFeet','holdSeconds','testPoint'];
const pointKeys=['version','name','description','airspeedRequired','altitudeRequired','policy'];
export const customFlightRule=()=>({airspeedKnots:120,airspeedToleranceKnots:5,altitudeMslFeet:3000,altitudeToleranceFeet:100,holdSeconds:30,testPoint:{version:1,name:'Stable Operating Conditions',description:'Hold the selected indicated airspeed and MSL altitude at the same time for a continuous recorded interval.',airspeedRequired:true,altitudeRequired:true,policy:'all'}});
export const editableFlightTest=r=>!!r?.testPoint&&r.testPoint.version===1&&r.testPoint.policy==='all'&&typeof r.testPoint.airspeedRequired==='boolean'&&typeof r.testPoint.altitudeRequired==='boolean'
 &&Object.keys(r).every(k=>ruleKeys.includes(k))&&Object.keys(r.testPoint).every(k=>pointKeys.includes(k));
export const customFlightTargets=r=>!editableFlightTest(r)?"Unsupported custom definition — preserved read-only":`${r.testPoint.name}: ${[r.testPoint.airspeedRequired?`${numeric(r.airspeedKnots)} ± ${numeric(r.airspeedToleranceKnots)} knots IAS`:null,r.testPoint.altitudeRequired?`${numeric(r.altitudeMslFeet)} ± ${numeric(r.altitudeToleranceFeet)} feet MSL`:null].filter(Boolean).join(' AND ')} · ${numeric(r.holdSeconds)} continuous seconds`;

// Both modes patch one complete definition. Unknown fields remain retained and read-only.
export function customFlightFields(parent,initial,{prefix='',revision=0,cardVersion=7,cardFields={}}={}){
 let value=structuredClone(initial),mode='guided';const supported=editableFlightTest(value)&&cardVersion===7&&Object.keys(cardFields).every(key=>['version','id','revision','name','templateId','createdUtc','atmosphericProfile','atmosphericTransect','survey','observationFlight','experimentalSensor'].includes(key)||key==='procedure'&&cardFields[key]==null);
 const root=node('section',undefined,'custom-flight-authoring');root.setAttribute('aria-label','Custom test-card definition');const tabs=node('div',undefined,'action-row'),body=node('div',undefined,'research-grid'),preview=node('details'),code=node('pre');preview.append(node('summary','Definition & Revision Preview'),code);tabs.setAttribute('role','group');tabs.setAttribute('aria-label','Editing experience');root.append(tabs,body,preview);parent.append(root);
 const refreshPreview=()=>{code.textContent=JSON.stringify({cardVersion,definitionVersion:value?.testPoint?.version,nextRevision:revision+1,flightRegime:value,cardFields},null,2);};
 function update(run){run();refreshPreview();}
 const guided=button(tabs,'Guided Builder',()=>render('guided')),advanced=button(tabs,'Advanced Editor',()=>render('advanced'));
 function field(title,current,key,{text=false,min,max,help}={}){const label=node('label',prefix+title),input=node(text?'textarea':'input');if(!text){input.type='number';input.step='any';}input.required=true;input.value=current;input.setAttribute('aria-label',prefix+title);if(min!==undefined)input.min=min;if(max!==undefined)input.max=max;if(text)input.maxLength=500;
  input.oninput=()=>update(()=>{if(key.startsWith('point.'))value.testPoint[key.slice(6)]=input.value;else value[key]=Number(input.value);});label.append(input);if(help)label.append(node('span',help,'hint'));body.append(label);return input;
 }
 function condition(key,title){const label=node('label',prefix+title),check=node('input');check.type='checkbox';check.checked=value.testPoint[key];label.prepend(check);body.append(label);check.oninput=()=>{update(()=>{value.testPoint[key]=check.checked;});syncConditions();};return check;}
 let speedFields=[],altitudeFields=[];
 function syncConditions(){for(const input of speedFields)input.disabled=!value.testPoint.airspeedRequired;for(const input of altitudeFields)input.disabled=!value.testPoint.altitudeRequired;}
 function render(next){mode=next;guided.setAttribute('aria-pressed',String(mode==='guided'));advanced.setAttribute('aria-pressed',String(mode==='advanced'));body.replaceChildren();speedFields=[];altitudeFields=[];
  body.append(node('h3',mode==='guided'?'Build Your Test Point':'Advanced Test-Point Definition'));
  if(!supported){body.append(node('p','This definition contains unsupported features or a newer version. Its complete content is preserved below. Saving is disabled; it will never be flattened into a single point.'));if(mode==='guided')button(body,'Open in Advanced Editor',()=>{render('advanced');advanced.focus();});preview.open=true;refreshPreview();return;}
  body.append(node('p','One named test point. When both conditions are selected, both must hold at the same time (AND). These example targets do not suit every simulated aircraft.'));
  const pointName=field('Test-point name',value.testPoint.name,'point.name',{text:true,help:'Give this one test point a short name.'});pointName.maxLength=100;pointName.rows=1;
  field('What do you want to test?',value.testPoint.description,'point.description',{text:true,help:'Describe your goal. Saving does not start recording or change aircraft settings.'});
  condition('airspeedRequired','Require indicated airspeed (IAS)');
  speedFields=[field('Indicated airspeed target (knots IAS)',value.airspeedKnots,'airspeedKnots',{min:1,max:2000}),field('Airspeed tolerance (± knots IAS)',value.airspeedToleranceKnots,'airspeedToleranceKnots',{min:.001,help:'Allowed difference from the IAS target; must be positive and smaller than the target.'})];
  condition('altitudeRequired','Require altitude (MSL)');
  altitudeFields=[field('Altitude target (feet MSL)',value.altitudeMslFeet,'altitudeMslFeet',{min:-2000,max:100000}),field('Altitude tolerance (± feet MSL)',value.altitudeToleranceFeet,'altitudeToleranceFeet',{min:.001,max:10000})];
  field('Required continuous hold (seconds)',value.holdSeconds,'holdSeconds',{min:1,max:3600,help:'One uninterrupted hold. Missing required readings, pauses and gaps cannot be added together.'});syncConditions();
  if(mode==='advanced'){
   table(body,'Evaluation & availability contract',['Setting','Supported behavior'],[
    ['Conditions','One or both of IAS / MSL, simultaneous AND only'],['IAS source / unit','Retained simulator indicated airspeed / knots IAS'],['Altitude source / unit','Retained simulator altitude above mean sea level / feet MSL'],['Availability','Only selected signals are tested; no Mach, load factor or avionics internals are inferred. The recorder must retain supported timestamps/source identity.'],['Missing data','Insufficient Evidence; invalid selected readings, pauses, source/aircraft changes or gaps over 1.5 seconds cannot pass the custom interval.'],['Version','Card 7 / named-point definition 1 / flight-regime-custom-1'],['Not implemented','Multiple points, OR groups, sequences, repetitions, automatic detection, aircraft eligibility or organization policy']]);
   preview.open=true;
  }
  refreshPreview();
 }
 render(mode);return{read:()=>structuredClone(value),isSupported:()=>supported};
}

const supportedFrame=(f,r)=>!f.isPaused&&['simulator','simulated'].includes(f.source)&&typeof f.aircraft==='string'&&!!f.aircraft.trim()
 &&/(Z|[+]00:00)$/i.test(f.timestampUtc)&&(!r.testPoint.airspeedRequired||Number.isFinite(f.indicatedAirspeedKnots)&&f.indicatedAirspeedKnots>=0)
 &&(!r.testPoint.altitudeRequired||Number.isFinite(f.altitudeMslFeet));

export async function showCustomFlightEvidence(parent,api,result,isCurrent=()=>true){
 const root=node('section');root.setAttribute('role','region');root.setAttribute('aria-label','Custom Flight Test Evidence');parent.replaceChildren(root);const status=node('p','Checking retained telemetry and frozen requirements…');status.setAttribute('role','status');root.append(node('h3',result.card.name),node('p',customFlightTargets(result.card.flightRegime)),node('p',result.card.flightRegime.testPoint.description),status);
 try{const data=await api(`/api/research/attempts/${result.id}/flight-test`);if(!root.isConnected||!isCurrent())return;const e=data.evidence,r=data.result,p=r.card.flightRegime;
  status.textContent=e.matchesSavedEvidence?`Frozen requirements, original telemetry and result match. ${r.outcome} · longest supported hold ${numeric(r.longestSupportedHoldSeconds)} seconds.`:'Evidence or frozen criteria do not match. This historical result is not verified.';
  root.append(node('p',`${e.invalidSamples} unsupported/paused readings · ${e.gapCount} sample gaps · ${e.pauseOrConnectionEdges} recorded break events. Only the selected signals are requirements.`));
  if(e.matchesSavedEvidence){
   const first=Date.parse(r.startUtc);for(const [required,key,title,unit,color]of [[p.testPoint.airspeedRequired,'indicatedAirspeedKnots','Indicated airspeed','knots IAS','#7fa4ed'],[p.testPoint.altitudeRequired,'altitudeMslFeet','Altitude','feet MSL','#62dec6']])if(required)chart(root,title+' — retained preview',[{label:'Original recorded '+title,data:e.preview.map(s=>({x:(Date.parse(s.frame.timestampUtc)-first)/1000,y:supportedFrame(s.frame,p)?s.frame[key]:null})),backgroundColor:color,pointRadius:4}],{type:'scatter',xLabel:'Seconds in original evidence interval',yLabel:unit});
   table(root,'Original custom-test measurements — bounded first/last preview',['Sequence','Original UTC','IAS (knots)','Altitude (feet MSL)','Source','Required conditions'],e.preview.map(s=>{const f=s.frame,met=(!p.testPoint.airspeedRequired||Math.abs(f.indicatedAirspeedKnots-p.airspeedKnots)<=p.airspeedToleranceKnots)&&(!p.testPoint.altitudeRequired||Math.abs(f.altitudeMslFeet-p.altitudeMslFeet)<=p.altitudeToleranceFeet);return[s.sequence,f.timestampUtc,f.indicatedAirspeedKnots>=0?numeric(f.indicatedAirspeedKnots):'Unavailable',numeric(f.altitudeMslFeet),f.source,!supportedFrame(f,p)?'Excluded — paused or unsupported required input':met?'Inside selected targets':'Outside selected targets'];}));
  }
  root.append(node('p','A bounded preview is not a continuous measured curve. Use the original flight timeline/replay for the full recording. Demo inputs remain synthetic; no aircraft certification is claimed.'),node('p',`Revision ${r.card.revision} · ${r.evaluatorVersion} · ${r.startUtc} → ${r.endUtc} · evidence SHA-256 ${r.evidence.sha256} · plan SHA-256 ${r.evidence.planSha256}`));
 }catch(error){if(root.isConnected&&isCurrent())status.textContent='Original evidence unavailable: '+error.message;}
 link(root,'Open Recorded Flight Replay',`#workspace/replay?missions=${result.flightId}`);const timeline=node('div');root.append(timeline);const {researchFlightTimeline}=await import('./personal-research-flight.js');if(root.isConnected&&isCurrent())await researchFlightTimeline(timeline,api,result.flightId,{at:result.startUtc});
}
