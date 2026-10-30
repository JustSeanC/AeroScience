import {node,button,readableName} from './workstation-ui.js';

// Shared portal/desktop presentation. No tracks, tile provider calls, storage or simulator commands.
export function organizationLiveUi({root,read,write,back,active=()=>true,admin=false,headingLevel='h2',readCampaigns=null}){
 const depth=Number(headingLevel.slice(1)),detailHeading='h'+(depth+1),sectionHeading='h'+(depth+2),cardHeading='h'+Math.min(6,depth+3);
 let disposed=false,busy=false,data=null,selected=null,receivedAt=0,map,layer,markers=new Map();
 const current=()=>!disposed&&root.isConnected&&active();
 const title=node(headingLevel,'Live Flights'),notice=node('p','Checking current sharing…'),controls=node('div',undefined,'actions');notice.setAttribute('role','status');
 root.replaceChildren(title,notice,controls);button(controls,'Back',()=>{dispose();back();});button(controls,'Refresh Live Flights',refresh);
 const label=node('label','Filter aircraft, pilot label or campaign'),filter=node('input');filter.type='search';label.append(filter);root.append(label);
 const layout=node('div',undefined,'live-flight-layout'),mapBox=node('div',undefined,'live-flight-map'),side=node('section');
 mapBox.setAttribute('role','region');mapBox.setAttribute('aria-label','Current shared aircraft map');side.setAttribute('aria-label','Shared flight list');layout.append(mapBox,side);root.append(layout);
 const list=node('div'),detail=node('section');detail.setAttribute('aria-label','Selected shared flight');side.append(list,detail);
 root.append(node('p','Read-only current locations on an offline reference grid. A point is not a track or scientific coverage. All active members may view sharing. Current telemetry may be newer than the last recorded sample. Live local status is not submitted or approved science.','notice'));
 const settings=node('section');root.append(settings);
 if(globalThis.L){map=L.map(mapBox,{attributionControl:false,worldCopyJump:false,minZoom:1,maxZoom:15}).setView([20,0],2);layer=L.layerGroup().addTo(map);
  for(let lon=-180;lon<=180;lon+=30)L.polyline([[-80,lon],[80,lon]],{color:'#6c7b8e',weight:1,opacity:.3,interactive:false}).addTo(map);
  for(let lat=-60;lat<=60;lat+=30)L.polyline([[lat,-180],[lat,180]],{color:'#6c7b8e',weight:1,opacity:.3,interactive:false}).addTo(map);
  map.invalidateSize();
 }else mapBox.append(node('p','Interactive map unavailable. The current locations and details remain available in the list.'));
 function age(v){return v.receivedUtc?Math.max(0,(Date.parse(data.checkedUtc)-Date.parse(v.receivedUtc))/1000+(performance.now()-receivedAt)/1000):Infinity;}
 function number(value,unit){return Number.isFinite(value)?`${value.toFixed(1)} ${unit}`:'Not available';}
 function visible(){const search=filter.value.trim().toLocaleLowerCase();return(data?.flights||[]).filter(v=>[v.displayLabel,v.current?.aircraft,...v.cards.map(c=>c.campaignName)].filter(Boolean).join(' ').toLocaleLowerCase().includes(search));}
 function choose(id,focusMap=false){selected=id;renderDetail();const marker=markers.get(id);if(focusMap&&marker){map.setView(marker.getLatLng(),Math.max(map.getZoom(),6));marker.openTooltip();}}
 function renderDetail(){detail.replaceChildren();const v=(data?.flights||[]).find(v=>v.sessionId===selected);if(!v)return;
  const c=age(v)<30?v.current:null;detail.append(node(detailHeading,v.displayLabel),node('p',`Flight ${v.flightId}`),node('p',`Started ${new Date(v.flightStartedUtc).toLocaleString()} · consent expires ${new Date(v.consentExpiresUtc).toLocaleString()}`),node('p',c?.source==='simulated'?'Synthetic simulator fixture — not installed-MSFS acceptance':c?.source==='simulator'?'Simulator-reported current state':'Source unavailable'));
  detail.append(node('p',`Aircraft: ${c?.aircraft||'Not available'}`),node('p',`Source: ${readableName(c?.state||'unavailable')} · original UTC ${c?.sourceUtc||'Not available'}`),node('p',`Altitude: ${number(c?.altitudeMslFeet,'feet MSL')} · Airspeed: ${number(c?.indicatedAirspeedKnots,'knots IAS')}`),node('p',`Heading: ${number(c?.headingDegrees,'degrees true')}`));
  if(c?.latitude!==null&&c?.latitude!==undefined)detail.append(node('p',`Position: ${c.latitude.toFixed(5)}, ${c.longitude.toFixed(5)}`));else detail.append(node('p','No fresh position available. No previous track is retained.'));
  detail.append(node(sectionHeading,'Research and Science'));
  for(const card of c?v.cards:[]){const row=node('article');row.append(node(cardHeading,card.name),node('p',`${card.campaignName||'Published campaign'} · issued revision ${card.issuedRevision}`),node('p',`${readableName(card.progress)} · ${card.active?'Activated':'Not activated'}${card.pointName?' · '+card.pointName:''}`));if(card.outcome)row.append(node('p',`Original local point outcome: ${readableName(card.outcome)} (not organization approval)`));if(card.attemptId)row.append(node('p',`Local attempt ${card.attemptId}`));row.append(node('p',card.meaning));detail.append(row);}
  if(!c||!v.cards.length)detail.append(node('p','No current organizational card status shared. Personal card definitions and notes are not included.'));
  if(c?.science){detail.append(node('p',`Science collection: ${readableName(c.science.phase||'unavailable')}${c.science.sessionId?' · session '+c.science.sessionId:''}`));for(const instrument of c.science.instruments)detail.append(node('p',`${readableName(instrument.instrumentId)}: ${readableName(instrument.state)} · source ${readableName(instrument.sourceState)}`));if(c.science.truncated)detail.append(node('p','Only the first eight instrument summaries are shared.'));}
  if(c?.cardsTruncated)detail.append(node('p','Card status is limited to eight selected organizational cards.'));
 }
 function render(){if(!current())return;for(const v of data?.flights||[])if(age(v)>=30){v.current=null;v.cards=[];}const focused=document.activeElement?.dataset?.liveSession,previousMarkers=[...markers.keys()];list.replaceChildren();layer?.clearLayers();markers.clear();
  for(const v of visible()){const seconds=age(v),fresh=seconds<30&&v.current?.latitude!==null&&v.current?.latitude!==undefined;
   const row=node('article');const pick=button(row,v.displayLabel,()=>choose(v.sessionId,true));pick.dataset.liveSession=v.sessionId;
   row.append(node('p',seconds>=30?'Position expired / waiting for update':seconds>=15?'Stale update':v.current?readableName(v.current.state):'Waiting for first update'),node('p',v.receivedUtc?`Last received ${new Date(v.receivedUtc).toLocaleTimeString()}`:'No update received'));list.append(row);
   if(fresh&&map){const m=L.circleMarker([v.current.latitude,v.current.longitude],{radius:8,color:seconds>=15?'#ad7800':'#0b9d9c',fillOpacity:.8}).addTo(layer);m.bindTooltip(node('span',v.displayLabel));m.on('click',()=>choose(v.sessionId));markers.set(v.sessionId,m);}
  }
  if(!visible().length)list.append(node('p',data?.policy.enabled===false?'Live monitoring is disabled by the organization.':'Nobody is sharing a matching flight. Sharing is always a pilot’s choice.'));
  if(focused)list.querySelectorAll('button').forEach(b=>{if(b.dataset.liveSession===focused)b.focus();});if(map&&previousMarkers.some(id=>!markers.has(id))){map.stop();map.setView([20,0],2,{animate:false});}renderDetail();
 }
 function clear(message){data=null;selected=null;layer?.clearLayers();markers.clear();if(map)map.setView([20,0],2,{animate:false});list.replaceChildren(node('p',message));detail.replaceChildren();notice.textContent=message;}
 async function refresh(){if(!current()||busy||document.hidden)return;busy=true;try{const result=await read();if(!current())return;data=result;receivedAt=performance.now();notice.textContent=`Current server check ${new Date(result.checkedUtc).toLocaleTimeString()} · ${result.flights.length} / ${result.maximumFlights} shared flights · updates every 5 seconds`;render();if(admin&&write&&!settings.children.length)policy(result.policy);}catch(e){if(current()){clear('Live Flights unavailable. Saved positions are cleared. '+(e.message||'Sign in or try again.'));settings.replaceChildren();}}finally{busy=false;}}
 function policy(p){settings.replaceChildren(node(detailHeading,'Live Monitoring Policy'));const form=node('form'),enabledLabel=node('label'),enabled=node('input');enabled.type='checkbox';enabled.checked=p.enabled;enabledLabel.append(enabled,document.createTextNode('Enable organization Live Flights'));form.append(enabledLabel);
  const restricted=new Set(p.restrictedCampaigns),choices=node('section');choices.setAttribute('aria-label','Restricted live campaigns');form.append(choices);
  async function campaigns(offset=0){choices.replaceChildren(node('p','Loading published campaigns…'));try{const page=await readCampaigns(offset);if(!current())return;choices.replaceChildren(node('p','Restrict sharing for sensitive campaigns. Other selected restrictions are preserved when changing pages.'));
   for(const c of page.items){const label=node('label'),box=node('input');box.type='checkbox';box.checked=restricted.has(c.id);label.append(box,document.createTextNode(c.name));box.onchange=()=>box.checked?restricted.add(c.id):restricted.delete(c.id);choices.append(label);}
   if(offset)button(choices,'Previous Campaign Choices',()=>campaigns(Math.max(0,offset-20)));if(page.nextOffset!==null)button(choices,'More Campaign Choices',()=>campaigns(page.nextOffset));
  }catch(e){choices.replaceChildren(node('p','Campaign choices unavailable. Saved restrictions are preserved. '+e.message));}}
  if(readCampaigns)campaigns();else choices.append(node('p','Campaign choices unavailable. Saved restrictions remain unchanged.'));
  form.append(node('p','Pilots with active claims in restricted campaigns cannot share any flight with this organization, even without campaign tags. Any policy change revokes existing sharing; pilots must explicitly consent again.'));
  const save=node('button','Save Live Monitoring Policy');save.type='submit';form.append(save);form.onsubmit=async e=>{e.preventDefault();save.disabled=true;try{await write({version:1,requestId:crypto.randomUUID(),expectedRevision:p.revision,enabled:enabled.checked,restrictedCampaigns:[...restricted]});settings.replaceChildren();await refresh();}catch(error){notice.textContent=error.message;}finally{save.disabled=false;}};settings.append(form);
 }
 filter.addEventListener('input',render);const poll=setInterval(()=>{if(!current())dispose();else refresh();},5000),expiry=setInterval(()=>{if(!current())dispose();else if(data)render();},1000);
 function hidden(){if(document.hidden)clear('Live view paused while this page is hidden. Positions cleared.');else refresh();}document.addEventListener('visibilitychange',hidden);
 function dispose(){if(disposed)return;disposed=true;clearInterval(poll);clearInterval(expiry);document.removeEventListener('visibilitychange',hidden);layer?.clearLayers();map?.remove();data=null;}
 refresh();return{dispose,refresh};
}
