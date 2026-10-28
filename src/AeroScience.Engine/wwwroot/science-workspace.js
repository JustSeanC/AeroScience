import {openSubmissionBuilder} from './submission-builder.js';
import {scienceMap} from './science-map.js';
import {comparison} from './science-compare.js';
import {summaryView} from './science-analysis.js';
import {coverage,imagery,replay,exports as researchExports} from './mission-workspace.js';
import {node as el,numeric as number,dateTime as local,select,button,link,disclosure,status,errorMessage,readableName} from './workstation-ui.js';
let cleanup=()=>{},generation=0;
const pages=[['map','Science Map'],['compare','Compare'],['analysis','Research Analysis'],['results','Results & Exports'],['replay','Replay Flight'],['timeline','Evidence Timeline']];
export async function scienceWorkspace(parent,api){
  const own=++generation;cleanup();const disposals=[],controller=new AbortController();
  cleanup=()=>{controller.abort();disposals.splice(0).forEach(dispose=>dispose());};
  const path=location.hash.split('?')[0].split('/')[1]||'map';
  const params=new URLSearchParams(location.hash.split('?')[1]||'');
  const ids=(params.get('missions')||'').split(',').filter(Boolean),instrument=params.get('instrument')||'';
  const title=pages.find(([id])=>id===path)?.[1]||'Science Map';
  parent.replaceChildren();const heading=el('div',undefined,'page-heading');heading.append(el('div'));heading.firstChild.append(el('p','SCIENCE WORKSPACE','eyebrow'),el('h1',title));parent.append(heading);
  heading.append(el('p','Science Results & Analysis: examine recorded observations, scientific maps, comparisons, replay and exported products. Configure payloads and control live sessions in Instruments & Collection.'));
  link(heading,'Instruments & Collection','#science');link(heading,'Mission History','#history');link(heading,'External Data Manager','#library');
  const nav=el('nav',undefined,'workspace-subnav');nav.setAttribute('aria-label','Science Results & Analysis pages');
  for(const [key,label] of pages){const a=link(nav,label,'#workspace/'+key+(params.size?'?'+params:''));if(path===key)a.setAttribute('aria-current','page');}parent.append(nav);
  const feedback=status(parent,'Loading the local research archive…');
  const ownHash=location.hash;
  const safeApi=async(path,body,options={})=>{const value=await api(path,body,{...options,signal:options.signal||controller.signal});if(own!==generation||location.hash!==ownHash)throw Error('View changed.');return value;};
  try{
    const [missions,catalog]=await Promise.all([safeApi('/api/missions?limit=100'),safeApi('/api/instruments')]);
    for(const id of ids.filter(id=>!missions.some(m=>m.id===id)).slice(0,10)){const detail=await safeApi('/api/missions/'+id);missions.push(detail.mission);}
    if(own!==generation)return;
    const controls=el('div',undefined,'workspace-controls workspace-context');if(path==='compare')disclosure(parent,'Mission archive scope & older flights').append(controls);else parent.append(controls);
    const choice=select(controls,path==='map'?'Mission scope':'Review mission',[["",path==='map'?'All Missions / choose mission':'Choose a mission'],...missions.map(m=>[m.id,m.name+' · '+local(m.startedUtc)])],ids[0]||'');
    const sensor=select(controls,'Instrument',[['','All instruments'],...catalog.map(item=>[item.id,item.name])],instrument);
    const multiple=disclosure(controls,'Multiple missions / older archive'),additional=el('select');additional.multiple=true;additional.size=4;additional.setAttribute('aria-label','Additional missions');
    for(const mission of missions){const option=el('option',mission.name);option.value=mission.id;option.selected=ids.slice(1).includes(mission.id);additional.append(option);}multiple.append(el('p','The picker lists recent flights. Open older missions directly from the searchable Mission History.'),additional);link(multiple,'Search Mission History','#history');
    const change=()=>{const query=new URLSearchParams(params),list=[choice.value,...[...additional.selectedOptions].map(option=>option.value)].filter(Boolean);if(list.length)query.set('missions',[...new Set(list)].join(','));else query.delete('missions');if(sensor.value)query.set('instrument',sensor.value);else query.delete('instrument');query.delete('measurement');location.hash='#workspace/'+path+(query.size?'?'+query:'');};
    choice.onchange=()=>{if(!choice.value)for(const option of additional.options)option.selected=false;change();};sensor.onchange=additional.onchange=change;
    feedback.remove();const body=el('section',undefined,'science-page');parent.append(body);
    if(path==='map'){await scienceMap(body,safeApi,params,ids,instrument,disposals,catalog);return;}
    if(path==='compare'){await comparison(body,safeApi,missions,ids,disposals,catalog);return;}
    if(!choice.value){const empty=el('div',undefined,'panel empty-state');empty.append(el('h2','Choose a recorded mission'),el('p','Review saved scientific measurements, replay the flight or export its research. MSFS and Internet access are not required.'));link(empty,'Find a mission','#history','button primary');body.append(empty);return;}
    const id=choice.value,summary=await safeApi(`/api/missions/${id}/science-summary`);
    body.append(el('h2',summary.mission.name));
    if(summary.mission.status!=='recording'){try{const eligible=await safeApi(`/api/submissions/choices?flightId=${id}`);if(eligible.choices.length)button(body,'Submit Evidence',()=>openSubmissionBuilder(api,{flightId:id}));}catch(error){if(error.name==='AbortError'||error.message==='View changed.')throw error;}}
    if(summary.mission.status!=='recording'){const research=await safeApi(`/api/missions/${id}/research`);const starter=research.planSnapshot?.researchStarter?.starterId;if(starter==='experimental-sensor-checkout')link(body,'Review Sensor Checkout',`#my-research?flight=${id}`);if(starter==='scientific-observation-flight')link(body,'Review Scientific Observations',`#my-research?flight=${id}`);if(starter==='terrain-temperature-survey')link(body,'Review Terrain & Temperature Survey',`#my-research?flight=${id}`);if(['vertical-atmospheric-profile','atmospheric-transect'].includes(starter))link(body,starter==='atmospheric-transect'?'Review Atmospheric Transect':'Review Atmospheric Profile',`#my-research?flight=${id}`);}
    await downlink(body,id,safeApi,disposals,catalog);
    if(path==='replay'){await replay(body,id,summary.mission,safeApi,{},stop=>disposals.push(stop));return;}
    if(path==='analysis'){await summaryView(body,summary,safeApi,instrument,{disposals,catalog,analysis:true});return;}
    const exports=el('section',undefined,'panel export-workspace');body.append(exports);
    exports.append(el('h3','Take your research with you'),el('p',summary.review.provisional?'Science processing is incomplete. Exports are labeled provisional; export again after review finishes to include the later derived results.':'Export recorded flight and science without changing your archive. Historical source dates remain independent of flight time.'));
    if(summary.mission.status!=='recording')researchExports(exports,id);else exports.append(el('p','End the mission before exporting a completed research package.'));
    const review=disclosure(body,'Recorded instruments and source quality');await summaryView(review,summary,safeApi,instrument,{disposals,catalog});
    const technical=disclosure(body,'Advanced — coverage, native-grid composites and retained imagery');let built=false;
    technical.addEventListener('toggle',async()=>{if(technical.open&&!built){built=true;try{await coverage(technical,id,safeApi,{});await imagery(technical,id,safeApi);}catch(error){built=false;errorMessage(technical,error);}}});
  }catch(error){if(error.name==='AbortError'||error.message==='View changed.')return;feedback.remove();errorMessage(parent,error,()=>scienceWorkspace(parent,api));}
}
async function downlink(parent,id,api,disposals,catalog){
  const box=el('section',undefined,'panel science-review-status');box.setAttribute('aria-label','Scientific review status');parent.append(box);
  const title=el('h3'),summary=el('p'),actions=el('div',undefined,'action-row'),advanced=disclosure(box,'Advanced — recovery evidence and limitations');box.prepend(title,summary,actions);
  let stopped=false,timer,lastAnnouncement='';const announcement=status(box,'');announcement.classList.add('sr-only');disposals.push(()=>{stopped=true;clearTimeout(timer);});
  async function render(){
    if(stopped||!box.isConnected)return;const data=await api(`/api/missions/${id}/downlink`);if(stopped)return;
    title.textContent=data.review.provisional?(data.review.flightStatus==='Recording'?'Flight Recording / Science Pending':'Flight Complete / Downlinking Science Data'):'Scientific Review — '+data.review.status;
    summary.textContent=data.review.provisional?`${data.review.recovered} recovered · ${data.review.pending} still pending. Maps, statistics and exports are provisional.`:`${data.review.recovered} recovered · ${data.review.permanentGaps} permanent gaps · ${data.review.cancelled} cancelled. Missing observations were not invented.`;
    if(title.textContent!==lastAnnouncement){lastAnnouncement=title.textContent;announcement.textContent=lastAnnouncement;}actions.replaceChildren();
    const states=data.items.map(item=>item.state);
    const control=async action=>{if(action==='cancel'&&!confirm('Cancel remaining post-flight science processing? Recorded flight data and existing scientific products will be preserved.'))return;await api(`/api/missions/${id}/downlink/control`,{action});await render();};
    if(data.review.pending||data.review.cancelled){if(states.includes('Paused'))button(actions,'Resume Downlink',()=>control('resume'));else button(actions,'Pause Downlink',()=>control('pause'));button(actions,'Cancel Downlink',()=>control('cancel'),'danger');}
    advanced.replaceChildren(el('summary','Advanced — recovery evidence and limitations'),el('p',data.review.message));
    const groups=new Map();for(const item of data.items){if(!groups.has(item.instrumentId))groups.set(item.instrumentId,[]);groups.get(item.instrumentId).push(item);}
    for(const [instrument,slots] of groups)advanced.append(el('p',`${readableName(instrument,catalog)}: ${slots.length} acquisition slots on this page · ${[...new Set(slots.map(item=>item.state))].join(', ')} · Time remaining unknown`));
    advanced.append(el('pre',JSON.stringify(data.items.map(item=>({id:item.id,instrument:item.instrumentId,flightUtc:item.flightUtc,state:item.state,reason:item.reason})),null,2)));
    if(!stopped)timer=setTimeout(()=>render().catch(error=>{if(error.name!=='AbortError')errorMessage(box,error,render);}),5000);
  }
  await render();
}
export function disposeScienceWorkspace(){generation++;cleanup();}
