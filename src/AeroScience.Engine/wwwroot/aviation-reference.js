import {dialogHeader} from './dialog-ui.js';
import {node as el,bytes,button,link} from './workstation-ui.js';
import {openAviationDownloads} from './aviation-downloads.js';
import {openTfrReferences,officialTfrNoticeUrl} from './tfr-layers.js';
const utc=value=>value?new Date(value).toISOString().replace('T',' ').replace('Z',' UTC'):'Unknown';
const productNames={sectional:'VFR Sectional',terminalArea:'Terminal Area Chart',helicopter:'Helicopter Route Chart',ifrLow:'IFR Low Enroute',ifrHigh:'IFR High Enroute',nasr:'FAA NASR reference original'};

// The host owns consent/cache/refresh. Reading or closing this view never downloads originals.
export async function openAviationReference(api,map) {
 const dialog=el('dialog');dialog.className='aviation-reference-dialog';dialog.setAttribute('aria-label','FAA Catalog & Notices');
 dialog.append(el('h2','FAA Catalog & Notices'),el('p','Use TFR notices and published boundaries on your existing map. These are flight-simulation references, not a real-world preflight service. Offline geographic basemaps are already included. FAA chart originals remain optional downloads; chart-interior rendering is deferred.'));
 const access=el('section'),tools=el('div',undefined,'action-row'),kindLabel=el('label','FAA reference product'),kind=el('select');kind.setAttribute('aria-label','FAA reference product');
 for(const [value,name] of [['tfr','TFR notices & published boundaries'],['vfr','VFR / TAC / Helicopter catalogs'],['ifr','IFR Low / High catalogs'],['nasr','NASR references / Class airspace / SAA']]){const o=el('option',name);o.value=value;kind.append(o);}
 kindLabel.append(kind);tools.append(kindLabel);const refresh=button(tools,'Refresh FAA References',()=>load(true));
 button(tools,'Review TFRs & Mission Intersections',()=>openTfrReferences(api,map));
 const message=el('p');message.setAttribute('role','status');message.setAttribute('aria-live','polite');
 const metadata=el('p',undefined,'footnote'),limitations=el('ul'),records=el('div',undefined,'aviation-reference-records'),paging=el('div',undefined,'action-row');
 let offset=0,total=0,epoch=0,closed=false,configured=false,loading=false;
 const previous=button(paging,'Previous FAA Page',()=>{offset=Math.max(0,offset-50);load();}),next=button(paging,'Next FAA Page',()=>{offset+=50;load();});
 button(tools,'Manage FAA Downloads',()=>openAviationDownloads(api));
 dialog.append(access,tools,message,metadata,limitations,records,paging);
 button(dialog,'Close',()=>dialog.close());document.body.append(dialog);dialogHeader(dialog);dialog.showModal();
 // Local cache only: paired browsers never poll FAA themselves. Do not rebuild
 // focused records, overlap requests, or keep polling hidden/closed views.
 const poll=setInterval(()=>{if(!closed&&!loading&&!document.hidden&&kind.value==='tfr'&&[...document.querySelectorAll('dialog[open]')].at(-1)===dialog&&!records.contains(document.activeElement))load();},15000);
 dialog.addEventListener('close',()=>{closed=true;epoch++;clearInterval(poll);dialog.remove();});
 async function configuration(){
  const own=++epoch;try{const [{configuration:c,canConfigure},tfr,storage]=await Promise.all([api('/api/aviation/configuration'),api('/api/aviation/tfr/configuration'),api('/api/aviation/downloads/configuration')]);if(closed||own!==epoch)return;
   access.replaceChildren(el('p','Aviation files use a managed folder automatically. No folder setup or chart download is required for TFR updates.'));
   const path=el('p',`Aviation data folder: ${storage.storageDirectory}`);path.className='storage-path';path.style.overflowWrap='anywhere';access.append(path);
   button(access,'Copy Aviation Folder Path',async()=>{try{await navigator.clipboard.writeText(storage.storageDirectory);message.textContent='Aviation folder path copied.';}catch{message.textContent='Copy the displayed aviation folder path manually; clipboard access is unavailable.';}});
   configured=c.onlineEnabled;
   if(canConfigure){
    const label=el('label',undefined,'checkbox-label'),enabled=el('input');enabled.type='checkbox';enabled.checked=c.onlineEnabled;enabled.setAttribute('aria-label','Allow FAA metadata retrieval');label.append(enabled,'Allow FAA metadata retrieval (separate consent)');access.append(label);
    const automaticLabel=el('label',undefined,'checkbox-label'),automatic=el('input');automatic.type='checkbox';automatic.checked=c.configured?tfr.tfrMonitoringEnabled:true;automatic.setAttribute('aria-label','Automatically update TFR notices and boundaries');automaticLabel.append(automatic,'Automatically update TFR notices and boundaries on this companion');access.append(automaticLabel);
    access.append(el('p','With FAA access enabled, automatic updates run at most once every five minutes, subject to network policy and provider backoff. Paired devices share the host cache. Exact operational packets remain a deliberate per-notice retrieval; fresh boundaries do not prove a restriction is active.'));
    const advanced=el('details'),summary=el('summary','Advanced aviation storage & network');advanced.append(summary,el('p',storage.usingDefaultStorage?'Using the default managed aviation folder.':'Using your existing custom aviation folder.'));
    const networkLabel=el('label',undefined,'checkbox-label'),unmetered=el('input');unmetered.type='checkbox';unmetered.checked=storage.unmeteredOnly;unmetered.setAttribute('aria-label','Use only confirmed unmetered networks for aviation data');networkLabel.append(unmetered,'Use only confirmed unmetered networks for aviation data');advanced.append(networkLabel);
    if(!c.configured){const folderLabel=el('label','Optional custom aviation folder on the companion PC'),folder=el('input');folder.type='text';folder.setAttribute('aria-label','New aviation storage folder');folderLabel.append(folder);advanced.append(folderLabel);button(advanced,'Set Aviation Storage Folder',async()=>{try{await api('/api/aviation/downloads/configuration',{unmeteredOnly:unmetered.checked,storageDirectory:folder.value||null});await configuration();}catch(error){message.textContent=error.message;}});}
    access.append(advanced);
    const save=button(access,'Save FAA Access',async()=>{save.disabled=true;try{
     await api('/api/aviation/downloads/configuration',{unmeteredOnly:unmetered.checked});
     await api('/api/aviation/configuration',{onlineEnabled:enabled.checked});
     // Disabling consent stops retrieval without discarding a saved monitoring choice.
     if(enabled.checked||!automatic.checked)await api('/api/aviation/tfr/configuration',{onlineEnabled:automatic.checked});
     await configuration();
    }catch(error){if(!closed)message.textContent=error.message;}finally{save.disabled=false;}});
   }else access.append(el('p',c.onlineEnabled?'FAA access is enabled on the companion PC. Automatic TFR updates: '+(tfr.tfrMonitoringEnabled?'On.':'Off.'):'Enable FAA access on the companion PC; paired devices cannot change this consent.'));
   const privacy=el('details');privacy.append(el('summary','FAA metadata privacy & limits'),el('p',c.privacy));access.append(privacy);
   refresh.disabled=!configured;await load();
  }catch(error){if(!closed&&own===epoch)message.textContent=error.message;}
 }
 async function load(update=false){
  const own=++epoch,selected=kind.value;loading=true;refresh.disabled=true;message.textContent=update?'Refreshing bounded FAA metadata…':'Reading retained FAA metadata…';
  try{
   if(update&&selected==='tfr'){await api('/api/aviation/tfr/refresh',{});document.dispatchEvent(new Event('aeroscience:tfr-updated'));}
   const page=await api(selected==='tfr'?'/api/aviation/tfr/notices?offset='+offset:'/api/aviation/catalog/'+selected+(update?'/refresh':'?offset='+offset),update&&selected!=='tfr'?{}:undefined);
   if(closed||own!==epoch||kind.value!==selected)return;
   if(update&&offset){offset=0;return await load();}
   total=page.total;message.textContent=`${page.status} · ${total} ${selected==='tfr'?'published notices':'catalog records'}. ${page.cache?.error?'Last refresh: '+page.cache.error+'. Previous successful records are retained.':''}`;
   metadata.textContent=`Last successful retrieval: ${utc(page.cache?.lastSuccessUtc)}. Last attempt: ${utc(page.cache?.lastAttemptUtc)}. ${selected==='tfr'?'Updates refresh the host inventory and published geometry; exact activation and completeness remain unverified.':'Original chart transfer size: Unknown until published. Opening this catalog does not download originals.'}`;
   limitations.replaceChildren(...(page.limitations||(selected==='tfr'?['Published boundaries and unplotted notices are available in TFR review. Missing geometry, cancellation or operational evidence cannot clear a route.']:[])).map(text=>el('li',text)));records.replaceChildren();
   for(const item of page.charts||[]){const card=el('article',undefined,'panel');card.append(el('h3',item.originalFileName.replace(/\.zip$/,'')),el('p',`${productNames[item.product]||item.product} · Catalog edition ${item.edition}`),el('p',`Original size: ${bytes(item.originalBytes)}. Effective UTC: ${utc(item.effectiveUtc)}. Expiry: ${utc(item.expiresUtc)}.`),el('p','Geographic coverage / CRS / neatline: '+(item.geographicVerification||'Not verified')),el('p',item.attribution));link(card,'Official FAA Original ZIP',item.sourceUrl);button(card,'Download & Retain Original',()=>openAviationDownloads(api,{kind:selected,item}));records.append(card);}
   for(const item of page.notices||[]){const card=el('article',undefined,'panel');card.append(el('h3',`NOTAM ${item.notamId} · ${item.type}`),el('p',`${item.state} / ${item.facility}`),el('p',item.title),el('p','Published geometry and available time/altitude evidence can be inspected in TFR review. A notice listing is not proof of activation or usable geographic coverage.'));const a=link(card,'Find this notice on the official FAA site',officialTfrNoticeUrl(item.notamId));a.target='_blank';a.rel='noopener noreferrer';records.append(card);}
   if(!records.childElementCount)records.append(el('p','No retained records on this page. This does not establish absence of coverage or restrictions.'));
   previous.disabled=offset===0;next.disabled=offset+50>=total;
  }catch(error){if(!closed&&own===epoch){message.textContent=error.message;records.replaceChildren(el('p','Current availability is Unknown. Existing flight recording and scientific data are unchanged.'));}}
  finally{if(!closed&&own===epoch){loading=false;refresh.disabled=!configured;}}
 }
 kind.onchange=()=>{offset=0;load();};await configuration();
}
