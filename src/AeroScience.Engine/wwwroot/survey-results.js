import {presentationTextNode} from './i18n.js';
import {message as localizedCopy,setPresentationText} from './i18n.js';
import {contextualHelp} from './contextual-help.js';
import {node as el,button,select,link,disclosure,metric,status,errorMessage,numeric,table,legend,dateTime} from './workstation-ui.js';
import {t as textForUI,t} from './i18n.js';
import {displayReading,onUnitsChanged} from './science-display.js';
import {surveyMap,elevation} from './survey-map.js';
import {modeAvailable,pointModes,pointRange,pointColor,filteredPoints} from './survey-points.js';
import {cloudView} from './survey-cloud.js';
import {surveyProfile} from './survey-profile.js';
import {surveyExports} from './survey-exports.js';
const text=key=>localizedCopy('results.'+key),terrainKind='terrain-elevation-index',lidarKind='lidar-point-index';
const preferenceKey='AeroScience.survey-view.v1';
function preferences(){try{const p=JSON.parse(localStorage.getItem(preferenceKey));return {terrain:p?.terrain!==false,relief:p?.relief===true,gaps:p?.gaps!==false,corridor:p?.corridor!==false,track:p?.track!==false,originals:p?.originals===true,opacity:Number.isFinite(p?.opacity)?Math.max(.1,Math.min(1,p.opacity)):.85};}catch{return {terrain:true,relief:false,gaps:true,corridor:true,track:true,originals:false,opacity:.85};}}
export function resultsHref(params,view){const q=new URLSearchParams(params);q.set('view',view);return '#workspace/results?'+q;}
export async function surveyResults(parent,api,{id,summary,params,disposals}){
 const root=el('section',undefined,'survey-results');setPresentationText(root,text('title'),"aria-label");parent.append(root);
 disposals.push(()=>root.remove());
 const requested=params.get('view'),view=['overview','explore','evidence'].includes(requested)?requested:'overview';
 const nav=el('nav',undefined,'workspace-subnav');setPresentationText(nav,text('views'),"aria-label");for(const key of ['overview','explore','evidence']){const a=link(nav,text(key),resultsHref(params,key));if(key===view)a.setAttribute('aria-current','page');}root.append(nav);contextualHelp(root,'evidence');
 const products=await api(`/api/missions/${id}/survey-results`),manifests=[],issues=[];
 // The latest retained artifact of each scientific kind, not summed revisions.
 for(const kind of ['continuous-terrain-survey',terrainKind,lidarKind]){const a=products.find(p=>p.kind===kind&&p.state==='Retained');if(a){try{manifests.push(await api(`/api/science/artifacts/${a.id}/survey-manifest`));}catch(e){if(e.name==='AbortError'||e.message==='View changed.')throw e;issues.push(e.message);}}}
 const originals=summary.instruments.reduce((n,i)=>n+i.measurementValues,0),acquisitions=summary.instruments.reduce((n,i)=>n+i.remoteProducts,0);
 if(view==='overview'){
  root.append(el('h3',text('overviewTitle')),el('p',summary.objective||text('noObjective')));
  const metrics=el('div',undefined,'finding-grid');root.append(metrics);
  metric(metrics,text('observations'),numeric(originals,0),`${numeric(acquisitions,0)} ${text('acquisitions')}`);
  metric(metrics,text('corridor'),manifests.some(p=>p.kind==='continuous-terrain-survey')?text('available'):text('unavailable'),text('modeledOnly'));
  metric(metrics,text('terrainCells'),numeric(manifests.find(p=>p.kind===terrainKind)?.values??0,0),text('historicalCells'));
  metric(metrics,text('lidarPoints'),numeric(manifests.find(p=>p.kind===lidarKind)?.values??0,0),text('historicalReturns'));
  root.append(el('p',text('separate')),el('p',summary.instruments.map(i=>i.name).join(' · ')||text('noInstruments')));
  const next=el('div',undefined,'action-row');root.append(next);link(next,text(manifests.some(p=>p.kind===terrainKind||p.kind===lidarKind)?'exploreData':'reviewOriginal'),resultsHref(params,manifests.some(p=>p.kind===terrainKind||p.kind===lidarKind)?'explore':'evidence'),'button primary');link(next,text('prepare'),resultsHref(params,'prepare'));
  for(const p of manifests){const item=disclosure(root,`${kindName(p.kind)} · ${p.sha256.slice(0,12)}`);describeManifest(item,p);}
  if(!manifests.length)root.append(el('p',text('noProducts'),'science-notice'));
 }else{
  const choices=products;
  const tools=el('div',undefined,'workspace-controls');root.append(tools);
  const product=select(tools,text('product'),[['',text('choose')],...choices.map(p=>[p.id,`${kindName(p.kind)} · ${p.sha256.slice(0,12)} · ${p.state}`])],params.get('product')||choices.find(p=>p.state==='Retained')?.id||'');
  const source=select(tools,text('source'),[['',text('choose')]],''),session=select(tools,text('session'),[['',text('allSessions')]],params.get('session')||'');
  const pane=el('div');root.append(pane);const readStatus=status(root,text('loading'));
  let generation=0,controller,stopped=false,manifest,values=[],geometry=[],track=[],next=null,map=null,cloud=null,profile=null,windowQuery={},memoryBytes=0,budgetLimited=false,readingPage=false,bulkReading=false,disposeView=()=>{};
  const prefs=preferences();
  const cancel=()=>{generation++;controller?.abort();disposeView();disposeView=()=>{};profile?.destroy();profile=null;cloud?.destroy();cloud=null;map?.destroy();map=null;values=[];geometry=[];track=[];next=null;memoryBytes=0;budgetLimited=false;readingPage=bulkReading=false;manifest=null;windowQuery={};};
  disposals.push(()=>{stopped=true;cancel();clearInterval(poll);window.removeEventListener('aeroscience-sources-changed',revalidate);});
  const reading=v=>elevation(v.value,v.unit,displayReading);
  function inspect(value){if(!value)return;map?.select(value);profile?.highlight(value);evidence.replaceChildren();evidence.append(el('h4',text('selected')),el('p',`${numeric(reading(value).value)} ${reading(value).unit} · ${value.quality}`));
   const raw={...value};if(raw.point)raw.point={...raw.point,original:{...raw.point.original,rawGpsTimeBits:undefined}};
   evidence.append(el('pre',JSON.stringify(raw,null,2)));link(evidence,text('originalArtifact'),`/api/science/artifacts/${value.artifactId}`);if(manifest){const s=manifest.sources.find(s=>s.id===value.sourceId);if(s)evidence.append(el('p',`${s.name} · ${s.surface.toUpperCase()} · ${s.verticalReference} · ${s.unit} · ${dateTime(s.observationUtc)}`));}
  }
  let evidence=el('section'),rows=el('div'),scale=el('div'),loadButton,overviewButton,inViewButton;
  function render(){if(!manifest)return;
   const shown=manifest.kind===lidarKind?filteredPoints(values,prefs):values;
   map?.update(values,prefs);cloud?.update(values,manifest.sources.find(s=>s.id===source.value),prefs);for(const key of ['corridor','track','originals'])map?.show(key,prefs[key]);
   rows.replaceChildren();table(rows,text('values'),[text('identity'),text('value'),text('raw'),text('quality'),text('source')],shown.map(v=>[v.column==null?v.id:`${v.column}, ${v.row}`,`${numeric(reading(v).value)} ${reading(v).unit}`,v.decodedSourceValue==null?text('missing'):String(v.decodedSourceValue),v.quality,v.sourceId]),{onSelect:i=>inspect(shown[i])});
   const valid=values.filter(v=>v.value!=null&&v.quality==='valid').map(v=>reading(v).value),unit=values.length?reading(values[0]).unit:manifest.sources[0]?.unit;
   if(manifest.kind===lidarKind){if(prefs.mode==='classification'){scale.replaceChildren(el('strong',text('classification')));for(const code of [...new Set(shown.map(v=>v.point.original.classification))].sort((a,b)=>a-b)){const row=el('p',String(code)),swatch=el('span',undefined,'point-class-swatch');swatch.style.background=pointColor(shown.find(v=>v.point.original.classification===code),'classification');swatch.setAttribute('aria-hidden','true');row.prepend(swatch);scale.append(row);}scale.append(el('p',text('lod')));}else if(prefs.mode==='rgb'){scale.replaceChildren(el('strong',text('rgb')),el('p',text('pointLimits')));}else{const [low,high]=pointRange(shown,prefs.mode),s=manifest.sources.find(s=>s.id===source.value),a=elevation(low,s?.unit,displayReading),b=elevation(high,s?.unit,displayReading);legend(scale,text(prefs.mode||'elevation'),prefs.mode==='elevation'?a.value:low,prefs.mode==='elevation'?b.value:high,prefs.mode==='elevation'?a.unit:'',text('lod'));}}else legend(scale,text('terrainLegend'),valid.length?Math.min(...valid):NaN,valid.length?Math.max(...valid):NaN,unit,text('legendScope'));
   root.dataset.loadedBytes=memoryBytes;root.dataset.loadedValues=values.length;
   setPresentationText(readStatus,`${values.length} ${text('loaded')} · ${manifest.values} ${text('retainedValues')} · ${manifest.missing} ${text('missingCells')}. ${budgetLimited?text('budgetLimit'):next==null?text('endPages'):text('morePages')}`);
   if(loadButton)loadButton.disabled=readingPage||bulkReading||next==null||budgetLimited||values.length>=32768;
   if(overviewButton)overviewButton.disabled=loadButton.disabled;
   if(inViewButton)inViewButton.disabled=readingPage||bulkReading||view!=='explore'||manifest.kind==='continuous-terrain-survey';
  }
  async function readPage(own,request){if(manifest.kind==='continuous-terrain-survey'){render();return;}if(readingPage)return;readingPage=true;render();try{const page=await request(`/api/science/artifacts/${manifest.id}/survey-page`,{expectedSha256:manifest.sha256,cursor:next??0,limit:Math.min(1024,32768-values.length),sourceId:source.value,sessionId:session.value||null,...windowQuery});if(own!==generation||stopped)return;for(const value of page.values){const bytes=new TextEncoder().encode(JSON.stringify(value)).length;if(memoryBytes+bytes>16777216||values.length>=32768){budgetLimited=true;break;}memoryBytes+=bytes;values.push(value);}next=page.nextCursor;}catch(e){if(own===generation&&!stopped){cancel();pane.replaceChildren(el('p',text('invalidated'),'science-notice'));setPresentationText(readStatus,text('invalidated'));errorMessage(pane,e);}throw new DOMException(textForUI("ui.surveyResults.messageObsoleteOrUnavailableSurveyPage"),'AbortError');}finally{if(own===generation&&!stopped){readingPage=false;render();}}}
  async function load(){cancel();pane.replaceChildren();evidence=el('section',undefined,'survey-inspector');rows=el('div');scale=el('div');const own=generation;controller=new AbortController();const request=async(path,body)=>{const r=await api(path,body,{signal:controller.signal});if(own!==generation||stopped)throw new DOMException(textForUI("ui.surveyResults.messageViewChanged"),'AbortError');return r;};
   if(!product.value){setPresentationText(readStatus,text('noProducts'));return;}
   setPresentationText(readStatus,text('loading'));try{
    manifest=await request(`/api/science/artifacts/${product.value}/survey-manifest`);
    const chosen=source.value;source.replaceChildren();for(const s of manifest.sources){const o=el('option',`${s.surface.toUpperCase()} · ${s.name} · ${s.verticalReference}`);o.value=s.id;source.append(o);}if(manifest.sources.some(s=>s.id===chosen))source.value=chosen;
    const explanation=el('section',undefined,'survey-source');pane.append(explanation);describeManifest(explanation,manifest,source.value);
    const layerTools=el('fieldset',undefined,'survey-layers');layerTools.append(el('legend',text('layers')));pane.append(layerTools);
    prefs.historical=true;prefs.mode='elevation';prefs.ground=false;prefs.classification='';prefs.returns='';
    for(const key of [manifest.kind===lidarKind?'historical':'terrain',...(manifest.kind===terrainKind?['relief']:[]),'gaps','corridor','track','originals']){const label=el('label'),input=el('input');input.type='checkbox';input.dataset.surveyLayer=key;input.checked=prefs[key];label.append(input,presentationTextNode(text(key+'Layer')));layerTools.append(label);input.onchange=()=>{prefs[key]=input.checked;save();render();};}
    if(manifest.kind===lidarKind){
     const originalSource=manifest.sources.find(s=>s.id===source.value),mode=select(layerTools,text('pointColor'),pointModes.map(v=>[v,text(v)]),'elevation');for(const o of mode.options){o.disabled=!modeAvailable(o.value,originalSource);if(o.disabled)o.textContent+=' — '+text(o.value==='heightAboveGround'?'noCompatibleHeight':'attributeAbsent');}mode.onchange=()=>{if(!modeAvailable(mode.value,originalSource))mode.value='elevation';prefs.mode=mode.value;render();};
     const ground=el('input');ground.type='checkbox';const label=el('label',text('groundOnly'));label.prepend(ground);layerTools.append(label);ground.disabled=!modeAvailable('classification',originalSource);ground.onchange=()=>{prefs.ground=ground.checked;render();};
     const classes=select(layerTools,text('classificationFilter'),[['',text('allClasses')],...Array.from({length:256},(_,i)=>[String(i),String(i)])],'');classes.disabled=!modeAvailable('classification',originalSource);classes.onchange=()=>{prefs.classification=classes.value;render();};
     const returns=select(layerTools,text('returnFilter'),[['',text('allReturns')],...Array.from({length:15},(_,i)=>[String(i+1),String(i+1)])],'');returns.disabled=!modeAvailable('return',originalSource);returns.onchange=()=>{prefs.returns=returns.value;render();};
     pane.append(el('p',text('pointLimits')));
    }
    const alpha=el('input');alpha.type='range';alpha.min='.1';alpha.max='1';alpha.step='.05';alpha.value=prefs.opacity;const alphaLabel=el('label',text('opacity'));alphaLabel.append(alpha);layerTools.append(alphaLabel);alpha.oninput=()=>{prefs.opacity=Number(alpha.value);save();render();};
    if(view==='explore'){const holder=el('section',undefined,'survey-map');pane.append(holder);map=surveyMap(holder,inspect);if(manifest.kind===lidarKind){const cloudHolder=el('div');pane.append(cloudHolder);const mode=select(layerTools,text('viewMode'),[['map',text('map2d')],['cloud',text('cloud3d')]],'map');mode.onchange=()=>{if(mode.value==='cloud'){if(!cloud)cloud=cloudView(cloudHolder,inspect);cloudHolder.hidden=false;render();}else cloudHolder.hidden=true;};}pane.append(scale);}
    const actions=el('div',undefined,'action-row');pane.append(actions);
    loadButton=button(actions,text('nextPage'),()=>readPage(own,request));
    overviewButton=button(actions,text('loadOverview'),async()=>{if(bulkReading)return;bulkReading=true;render();let pages=0;try{do{await readPage(own,request);pages++;await new Promise(r=>setTimeout(r,0));}while(next!=null&&!budgetLimited&&pages<16&&own===generation);}finally{if(own===generation&&!stopped){bulkReading=false;render();}}});overviewButton.disabled=manifest.kind==='continuous-terrain-survey';
    inViewButton=button(actions,text('viewWindow'),async()=>{if(!map)return;const b=map.map.getBounds();values=[];next=null;memoryBytes=0;budgetLimited=false;windowQuery={west:Math.max(-180,b.getWest()),east:Math.min(180,b.getEast()),south:Math.max(-90,b.getSouth()),north:Math.min(90,b.getNorth())};await readPage(own,request);});inViewButton.disabled=view!=='explore'||manifest.kind==='continuous-terrain-survey';
    pane.append(evidence,rows);
    let cursor=0,geometryBytes=0,geometryVertices=0;do{const page=await request(`/api/science/artifacts/${manifest.geometryId}/survey-geometry?sha=${manifest.geometrySha256}&cursor=${cursor}&limit=8`);const bytes=new TextEncoder().encode(JSON.stringify(page)).length,vertices=page.records.reduce((n,r)=>n+(r.chunk?.geometry.coordinates.reduce((n,p)=>n+p.reduce((n,r)=>n+r.length,0),0)||0),0);if(geometryBytes+bytes>8388608||geometryVertices+vertices>262144)break;geometryBytes+=bytes;geometryVertices+=vertices;geometry.push(...page.records);cursor=page.nextCursor;}while(cursor!=null&&geometry.length<256);
    if(cursor!=null)pane.append(el('p',text('geometryBound'),'science-notice'));
    const selected=session.value,sessions=[...new Set(geometry.filter(r=>r.chunk).map(r=>r.chunk.sessionId))];session.replaceChildren(el('option',text('allSessions')));session.firstChild.value='';for(const id of sessions){const o=el('option',id);o.value=id;session.append(o);}if(sessions.includes(selected))session.value=selected;
    map?.geometry(geometry,session.value);
    const gaps=geometry.filter(r=>r.gap);if(gaps.length){const detail=disclosure(pane,text('gaps'));detail.append(el('pre',JSON.stringify(gaps.map(r=>r.gap),null,2)));}
    let after=0,trackPages=0;do{const page=await request(`/api/missions/${id}/survey-track?after=${after}&limit=2048`);track.push(...page.points);after=page.nextAfter;trackPages++;}while(after!=null&&trackPages<4);map?.track(track);if(after!=null)pane.append(el('p',text('trackBound')));
    const originals=await request('/api/science/workspace/map',{missions:[id],instrument:params.get('instrument')||null,limit:100});map?.originals(originals.products);if(originals.hasMore)pane.append(el('p',text('originalBound')));
    await readPage(own,request);surveyExports(actions,()=>({manifest,values,geometry}));
    if(manifest.kind===terrainKind&&view==='explore')profile=surveyProfile(pane,request,{manifest,source:manifest.sources.find(s=>s.id===source.value),session:()=>session.value,track,pickLine:callback=>map?.pickLine(callback),onSelect:async sample=>{map?.cursor(sample.position);if(sample.column==null){evidence.replaceChildren(el('p',text('missing')));return;}const loaded=values.find(v=>v.column===sample.column&&v.row===sample.row&&v.sourceId===source.value);if(loaded){inspect(loaded);return;}const p=sample.position,page=await request(`/api/science/artifacts/${manifest.id}/survey-page`,{expectedSha256:manifest.sha256,limit:32,sourceId:source.value,sessionId:session.value||null,west:p.longitude-1e-8,east:p.longitude+1e-8,south:p.latitude-1e-8,north:p.latitude+1e-8});const value=page.values.find(v=>v.column===sample.column&&v.row===sample.row);if(value)inspect(value);}});
    disposeView=onUnitsChanged(render);
   }catch(e){if(e.name!=='AbortError'&&e.message!=='View changed.'){values=[];map?.update([],prefs);rows.replaceChildren();evidence.replaceChildren();setPresentationText(readStatus,text('invalidated'));errorMessage(pane,e);}}
  }
  function save(){try{localStorage.setItem(preferenceKey,JSON.stringify(prefs));}catch{/* Display preferences are optional. */}}
  async function revalidate(){if(stopped||!manifest||document.hidden)return;const own=generation,selected=manifest;try{const p=await api(`/api/science/artifacts/${selected.id}/survey-manifest`);if(own!==generation)return;if(p.sha256!==selected.sha256)throw Error(text('invalidated'));}catch(e){if(stopped||own!==generation||e.name==='AbortError')return;cancel();pane.replaceChildren(el('p',text('invalidated'),'science-notice'));setPresentationText(readStatus,text('invalidated'));}}
  const poll=setInterval(revalidate,15000);window.addEventListener('aeroscience-sources-changed',revalidate);
  product.onchange=source.onchange=session.onchange=load;await load();
 }
 for(const issue of issues)root.append(el('p',issue,'science-notice'));
 const advanced=disclosure(root,text('advanced'));link(advanced,text('originalMap'),'#workspace/map?'+new URLSearchParams([...params].filter(([k])=>k!=='view')));link(advanced,text('analysis'),'#workspace/analysis?'+new URLSearchParams(params));link(advanced,text('compare'),'#workspace/compare?'+new URLSearchParams(params));link(advanced,text('library'),'#library');
 return {view,legacy:!requested||requested==='prepare'};
}
function kindName(kind){return text(kind==='terrain-elevation-index'?'terrainCells':kind===lidarKind?'lidarPoints':'corridor');}
function describeManifest(parent,m,selected){
 parent.append(el('p',m.evidenceClass),el('p',`${m.values} ${text('retainedValues')} · ${m.missing} ${text('missingCells')}`));
 for(const s of m.sources.filter(s=>!selected||s.id===selected))parent.append(el('p',`${s.surface.toUpperCase()} · ${s.name} · ${s.project} · ${s.horizontalCrs} · ${s.verticalReference} (${s.unit}) · ${dateTime(s.observationUtc)} · ${s.license} · ${s.citation}`));
 const detail=disclosure(parent,text('provenance'));detail.append(el('pre',JSON.stringify({artifactId:m.id,sha256:m.sha256,geometryId:m.geometryId,geometrySha256:m.geometrySha256,inputFingerprint:m.inputFingerprint,algorithm:m.algorithm,sources:m.sources,limitations:m.limitations,originalScienceCredit:false},null,2)));link(detail,text('originalArtifact'),`/api/science/artifacts/${m.id}`);
}
