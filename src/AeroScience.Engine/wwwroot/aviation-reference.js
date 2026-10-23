import {dialogHeader} from './dialog-ui.js';
import {node as el,bytes,button,link} from './workstation-ui.js';
import {openAviationDownloads} from './aviation-downloads.js';
const utc=value=>value?new Date(value).toISOString().replace('T',' ').replace('Z',' UTC'):'Unknown';
const productNames={sectional:'VFR Sectional',terminalArea:'Terminal Area Chart',helicopter:'Helicopter Route Chart',ifrLow:'IFR Low Enroute',ifrHigh:'IFR High Enroute',nasr:'FAA NASR reference original'};

// The host owns consent/cache/refresh. Reading or closing this view never downloads chart originals.
export async function openAviationReference(api) {
 const dialog=el('dialog');dialog.className='aviation-reference-dialog';dialog.setAttribute('aria-label','FAA Catalog & Notices');
 dialog.append(el('h2','FAA Catalog & Notices'),el('p','FAA reference metadata, not a real-world preflight product. Local chart rendering and verified TFR polygons are not yet available.'));
 const access=el('section'),tools=el('div',undefined,'action-row'),kindLabel=el('label','FAA reference product'),kind=el('select');kind.setAttribute('aria-label','FAA reference product');
 for(const [value,name] of [['vfr','VFR / TAC / Helicopter catalogs'],['ifr','IFR Low / High catalogs'],['nasr','NASR references / Class airspace / SAA'],['tfr','TFR notice summaries']]){const o=el('option',name);o.value=value;kind.append(o);}
 kindLabel.append(kind);tools.append(kindLabel);const refresh=button(tools,'Refresh FAA References',()=>load(true));
 const message=el('p');message.setAttribute('role','status');message.setAttribute('aria-live','polite');
 const metadata=el('p',undefined,'footnote'),limitations=el('ul'),records=el('div',undefined,'aviation-reference-records'),paging=el('div',undefined,'action-row');
 let offset=0,total=0,epoch=0,closed=false,configured=false;
 const previous=button(paging,'Previous FAA Page',()=>{offset=Math.max(0,offset-50);load();}),next=button(paging,'Next FAA Page',()=>{offset+=50;load();});
 button(tools,'Manage FAA Downloads',()=>openAviationDownloads(api));
 dialog.append(access,tools,message,metadata,limitations,records,paging);
 button(dialog,'Close',()=>dialog.close());document.body.append(dialog);dialogHeader(dialog);dialog.showModal();
 dialog.addEventListener('close',()=>{closed=true;epoch++;dialog.remove();});
 async function configuration(){
  const own=++epoch;try{const {configuration:c,canConfigure}=await api('/api/aviation/configuration');if(closed||own!==epoch)return;
   const privacy=el('details'),summary=el('summary','FAA metadata privacy & limits');privacy.append(summary,el('p',c.privacy));privacy.open=!c.configured;access.replaceChildren(privacy);configured=c.onlineEnabled;
   if(canConfigure){const label=el('label',undefined,'checkbox-label'),enabled=el('input');enabled.type='checkbox';enabled.checked=c.onlineEnabled;enabled.setAttribute('aria-label','Allow FAA metadata retrieval');label.append(enabled,'Allow FAA metadata retrieval (separate consent)');access.append(label);
    if(!c.configured){const folderLabel=el('label','Optional aviation-only folder on the companion PC'),folder=el('input');folder.type='text';folder.setAttribute('aria-label','New aviation storage folder');folderLabel.append(folder);access.append(folderLabel);button(access,'Set Aviation Storage Folder',async()=>{try{await api('/api/aviation/downloads/configuration',{unmeteredOnly:true,storageDirectory:folder.value||null});message.textContent='Aviation storage choice saved. Enable FAA access to initialize it; no existing files were moved.';}catch(error){message.textContent=error.message;}});}
    const save=button(access,'Save FAA Access',async()=>{save.disabled=true;try{await api('/api/aviation/configuration',{onlineEnabled:enabled.checked});configured=enabled.checked;await load();}catch(error){if(!closed)message.textContent=error.message;}finally{save.disabled=false;}});
   }else access.append(el('p',c.onlineEnabled?'FAA reference access is enabled on the companion PC.':'Enable FAA reference access on the companion PC; paired devices cannot change this consent.'));
   refresh.disabled=!configured;await load();
  }catch(error){if(!closed&&own===epoch)message.textContent=error.message;}
 }
 async function load(update=false){
  const own=++epoch,selected=kind.value;refresh.disabled=true;message.textContent=update?'Refreshing bounded FAA metadata…':'Reading retained FAA metadata…';
  try{const page=await api('/api/aviation/catalog/'+selected+(update?'/refresh':'?offset='+offset),update?{}:undefined);
   if(closed||own!==epoch||kind.value!==selected)return;
   if(update&&offset){offset=0;return await load();}
   total=page.total;message.textContent=`${page.status} · ${total} catalog records. ${page.cache?.error?'Last refresh: '+page.cache.error+'. Previous successful records are retained.':''}`;
   metadata.textContent=`Last successful retrieval: ${utc(page.cache?.lastSuccessUtc)}. Last attempt: ${utc(page.cache?.lastAttemptUtc)}. Original chart transfer size: Unknown until published. Opening this catalog does not download originals.`;
   limitations.replaceChildren(...page.limitations.map(text=>el('li',text)));records.replaceChildren();
   for(const item of page.charts){const card=el('article',undefined,'panel');card.append(el('h3',item.originalFileName.replace(/\.zip$/,'')),el('p',`${productNames[item.product]||item.product} · Catalog edition ${item.edition}`),el('p',`Original size: ${bytes(item.originalBytes)}. Effective UTC: ${utc(item.effectiveUtc)}. Expiry: ${utc(item.expiresUtc)}.`),el('p','Geographic coverage / CRS / neatline: '+(item.geographicVerification||'Not verified')),el('p',item.attribution));link(card,'Official FAA Original ZIP',item.sourceUrl);button(card,'Download & Retain Original',()=>openAviationDownloads(api,{kind:selected,item}));records.append(card);}
   for(const item of page.notices){const card=el('article',undefined,'panel');card.append(el('h3',`NOTAM ${item.notamId} · ${item.type}`),el('p',`${item.state} / ${item.facility} · Published date text: ${item.creationDateText}`),el('p',item.description),el('p',`${item.geometryStatus}. Exact activation UTC, altitude and mission overlap: Unknown. No polygon has been drawn.`));link(card,'Find this notice on the official FAA site',item.officialUrl);records.append(card);}
   if(!records.childElementCount)records.append(el('p','No retained records on this page. This does not establish absence of coverage or restrictions.'));
   previous.disabled=offset===0;next.disabled=offset+50>=total;
  }catch(error){if(!closed&&own===epoch){message.textContent=error.message;records.replaceChildren(el('p','Current availability is Unknown. Existing flight recording and scientific data are unchanged.'));}}
  finally{if(!closed&&own===epoch)refresh.disabled=!configured;}
 }
 kind.onchange=()=>{offset=0;load();};await configuration();
}
