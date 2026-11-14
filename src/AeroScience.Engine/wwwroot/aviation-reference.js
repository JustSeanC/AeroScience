import {message as localizedCopy,setPresentationText} from './i18n.js';
import {t as textForUI} from './i18n.js';
import {dialogHeader} from './dialog-ui.js';
import {node as el,bytes,button,link} from './workstation-ui.js';
import {openAviationDownloads} from './aviation-downloads.js';
import {openTfrReferences,officialTfrNoticeUrl} from './tfr-layers.js';
const utc=value=>value?new Date(value).toISOString().replace('T',' ').replace('Z',' UTC'):textForUI("localization.unknownValue");
const productNames={get sectional(){return textForUI("ui.aviationReference.messageVFRSectional");},get terminalArea(){return textForUI("ui.aviationReference.messageTerminalAreaChart");},get helicopter(){return textForUI("ui.aviationReference.messageHelicopterRouteChart");},get ifrLow(){return textForUI("ui.aviationReference.messageIFRLowEnroute");},get ifrHigh(){return textForUI("ui.aviationReference.messageIFRHighEnroute");},get nasr(){return textForUI("ui.aviationReference.messageFAANASRReferenceOriginal");}};

// The host owns consent/cache/refresh. Reading or closing this view never downloads originals.
export async function openAviationReference(api,map) {
 const dialog=el('dialog');dialog.className='aviation-reference-dialog';setPresentationText(dialog,localizedCopy("ui.aviationReference.messageFAACatalogNotices"),"aria-label");
 dialog.append(el('h2',localizedCopy("ui.aviationReference.messageFAACatalogNotices")),el('p',localizedCopy("ui.aviationReference.messageUseTFRNoticesAndPublishedBoundariesOn")));
 const access=el('section'),tools=el('div',undefined,'action-row'),kindLabel=el('label',localizedCopy("ui.aviationReference.messageFAAReferenceProduct")),kind=el('select');setPresentationText(kind,localizedCopy("ui.aviationReference.messageFAAReferenceProduct"),"aria-label");
 for(const [value,name] of [['tfr',textForUI("ui.aviationReference.messageTFRNoticesPublishedBoundaries")],['vfr',textForUI("ui.aviationReference.messageVFRTACHelicopterCatalogs")],['ifr',textForUI("ui.aviationReference.messageIFRLowHighCatalogs")],['nasr',textForUI("ui.aviationReference.messageNASRReferencesClassAirspaceSAA")]]){const o=el('option',name);o.value=value;kind.append(o);}
 kindLabel.append(kind);tools.append(kindLabel);const refresh=button(tools,localizedCopy("ui.aviationReference.messageRefreshFAAReferences"),()=>load(true));
 button(tools,localizedCopy("ui.aviationReference.messageReviewTFRsMissionIntersections"),()=>openTfrReferences(api,map));
 const message=el('p');message.setAttribute('role','status');message.setAttribute('aria-live','polite');
 const metadata=el('p',undefined,'footnote'),limitations=el('ul'),records=el('div',undefined,'aviation-reference-records'),paging=el('div',undefined,'action-row');
 let offset=0,total=0,epoch=0,closed=false,configured=false,loading=false;
 const previous=button(paging,localizedCopy("ui.aviationReference.messagePreviousFAAPage"),()=>{offset=Math.max(0,offset-50);load();}),next=button(paging,localizedCopy("ui.aviationReference.messageNextFAAPage"),()=>{offset+=50;load();});
 button(tools,localizedCopy("ui.aviationReference.messageManageFAADownloads"),()=>openAviationDownloads(api));
 dialog.append(access,tools,message,metadata,limitations,records,paging);
 button(dialog,localizedCopy("ui.aviationReference.messageClose"),()=>dialog.close());document.body.append(dialog);dialogHeader(dialog);dialog.showModal();
 // Local cache only: paired browsers never poll FAA themselves. Do not rebuild
 // focused records, overlap requests, or keep polling hidden/closed views.
 const poll=setInterval(()=>{if(!closed&&!loading&&!document.hidden&&kind.value==='tfr'&&[...document.querySelectorAll('dialog[open]')].at(-1)===dialog&&!records.contains(document.activeElement))load();},15000);
 dialog.addEventListener('close',()=>{closed=true;epoch++;clearInterval(poll);dialog.remove();});
 async function configuration(){
  const own=++epoch;try{const [{configuration:c,canConfigure},tfr,storage]=await Promise.all([api('/api/aviation/configuration'),api('/api/aviation/tfr/configuration'),api('/api/aviation/downloads/configuration')]);if(closed||own!==epoch)return;
   access.replaceChildren(el('p',localizedCopy("ui.aviationReference.messageAviationFilesUseAManagedFolderAutomatically")));
   const path=el('p',localizedCopy("ui.aviationReference.messageAviationDataFolder",{value1:(String(storage.storageDirectory))}));path.className='storage-path';path.style.overflowWrap='anywhere';access.append(path);
   button(access,localizedCopy("ui.aviationReference.messageCopyAviationFolderPath"),async()=>{try{await navigator.clipboard.writeText(storage.storageDirectory);setPresentationText(message,localizedCopy("ui.aviationReference.messageAviationFolderPathCopied"));}catch{setPresentationText(message,localizedCopy("ui.aviationReference.messageCopyTheDisplayedAviationFolderPathManually"));}});
   configured=c.onlineEnabled;
   if(canConfigure){
    const label=el('label',undefined,'checkbox-label'),enabled=el('input');enabled.type='checkbox';enabled.checked=c.onlineEnabled;setPresentationText(enabled,localizedCopy("ui.aviationReference.messageAllowFAAMetadataRetrieval"),"aria-label");label.append(enabled,presentationTextNode(localizedCopy("ui.aviationReference.messageAllowFAAMetadataRetrievalSeparateConsent")));access.append(label);
    const automaticLabel=el('label',undefined,'checkbox-label'),automatic=el('input');automatic.type='checkbox';automatic.checked=c.configured?tfr.tfrMonitoringEnabled:true;setPresentationText(automatic,localizedCopy("ui.aviationReference.messageAutomaticallyUpdateTFRNoticesAndBoundaries"),"aria-label");automaticLabel.append(automatic,presentationTextNode(localizedCopy("ui.aviationReference.messageAutomaticallyUpdateTFRNoticesAndBoundariesOn")));access.append(automaticLabel);
    access.append(el('p',localizedCopy("ui.aviationReference.messageWithFAAAccessEnabledAutomaticUpdatesRun")));
    const advanced=el('details'),summary=el('summary',localizedCopy("ui.aviationReference.messageAdvancedAviationStorageNetwork"));advanced.append(summary,el('p',storage.usingDefaultStorage?localizedCopy("ui.aviationReference.messageUsingTheDefaultManagedAviationFolder"):localizedCopy("ui.aviationReference.messageUsingYourExistingCustomAviationFolder")));
    const networkLabel=el('label',undefined,'checkbox-label'),unmetered=el('input');unmetered.type='checkbox';unmetered.checked=storage.unmeteredOnly;setPresentationText(unmetered,localizedCopy("ui.aviationReference.messageUseOnlyConfirmedUnmeteredNetworksForAviation"),"aria-label");networkLabel.append(unmetered,presentationTextNode(localizedCopy("ui.aviationReference.messageUseOnlyConfirmedUnmeteredNetworksForAviation")));advanced.append(networkLabel);
    if(!c.configured){const folderLabel=el('label',localizedCopy("ui.aviationReference.messageOptionalCustomAviationFolderOnTheCompanion")),folder=el('input');folder.type='text';setPresentationText(folder,localizedCopy("ui.aviationReference.messageNewAviationStorageFolder"),"aria-label");folderLabel.append(folder);advanced.append(folderLabel);button(advanced,localizedCopy("ui.aviationReference.messageSetAviationStorageFolder"),async()=>{try{await api('/api/aviation/downloads/configuration',{unmeteredOnly:unmetered.checked,storageDirectory:folder.value||null});await configuration();}catch(error){setPresentationText(message,error.message);}});}
    access.append(advanced);
    const save=button(access,localizedCopy("ui.aviationReference.messageSaveFAAAccess"),async()=>{save.disabled=true;try{
     await api('/api/aviation/downloads/configuration',{unmeteredOnly:unmetered.checked});
     await api('/api/aviation/configuration',{onlineEnabled:enabled.checked});
     // Disabling consent stops retrieval without discarding a saved monitoring choice.
     if(enabled.checked||!automatic.checked)await api('/api/aviation/tfr/configuration',{onlineEnabled:automatic.checked});
     await configuration();
    }catch(error){if(!closed)setPresentationText(message,error.message);}finally{save.disabled=false;}});
   }else access.append(el('p',c.onlineEnabled?localizedCopy("ui.aviationReference.messageFAAAccessIsEnabledOnTheCompanion",{value1:(String(tfr.tfrMonitoringEnabled?'On.':'Off.'))}):localizedCopy("ui.aviationReference.messageEnableFAAAccessOnTheCompanionPC")));
   const privacy=el('details');privacy.append(el('summary',localizedCopy("ui.aviationReference.messageFAAMetadataPrivacyLimits")),el('p',c.privacy));access.append(privacy);
   refresh.disabled=!configured;await load();
  }catch(error){if(!closed&&own===epoch)setPresentationText(message,error.message);}
 }
 async function load(update=false){
  const own=++epoch,selected=kind.value;loading=true;refresh.disabled=true;setPresentationText(message,update?localizedCopy("ui.aviationReference.messageRefreshingBoundedFAAMetadata"):localizedCopy("ui.aviationReference.messageReadingRetainedFAAMetadata"));
  try{
   if(update&&selected==='tfr'){await api('/api/aviation/tfr/refresh',{});document.dispatchEvent(new Event('aeroscience:tfr-updated'));}
   const page=await api(selected==='tfr'?'/api/aviation/tfr/notices?offset='+offset:'/api/aviation/catalog/'+selected+(update?'/refresh':'?offset='+offset),update&&selected!=='tfr'?{}:undefined);
   if(closed||own!==epoch||kind.value!==selected)return;
   if(update&&offset){offset=0;return await load();}
   total=page.total;setPresentationText(message,`${page.status} · ${total} ${selected==='tfr'?textForUI("ui.aviationReference.messagePublishedNotices"):textForUI("ui.aviationReference.messageCatalogRecords")}. ${page.cache?.error?textForUI("ui.aviationReference.messageLastRefreshPreviousSuccessfulRecordsAre",{value1:(page.cache.error)}):''}`);
   setPresentationText(metadata,localizedCopy("ui.aviationReference.messageLastSuccessfulRetrievalLastAttempt",{value1:(String(utc(page.cache?.lastSuccessUtc))),value2:(String(utc(page.cache?.lastAttemptUtc))),value3:(String(selected==='tfr'?'Updates refresh the host inventory and published geometry; exact activation and completeness remain unverified.':'Original chart transfer size: Unknown until published. Opening this catalog does not download originals.'))}));
   limitations.replaceChildren(...(page.limitations||(selected==='tfr'?[textForUI("ui.aviationReference.messagePublishedBoundariesAndUnplottedNoticesAreAvailable")]:[])).map(text=>el('li',text)));records.replaceChildren();
   for(const item of page.charts||[]){const card=el('article',undefined,'panel');card.append(el('h3',item.originalFileName.replace(/\.zip$/,'')),el('p',localizedCopy("ui.aviationReference.messageCatalogEdition",{value1:(String(productNames[item.product]||item.product)),value2:(String(item.edition))})),el('p',localizedCopy("ui.aviationReference.messageOriginalSizeEffectiveUTCExpiry",{value1:(String(bytes(item.originalBytes))),value2:(String(utc(item.effectiveUtc))),value3:(String(utc(item.expiresUtc)))})),el('p',localizedCopy("ui.aviationReference.messageGeographicCoverageCRSNeatline",{value1:(String(item.geographicVerification||'Not verified'))})),el('p',item.attribution));link(card,localizedCopy("ui.aviationReference.messageOfficialFAAOriginalZIP"),item.sourceUrl);button(card,localizedCopy("ui.aviationReference.messageDownloadRetainOriginal"),()=>openAviationDownloads(api,{kind:selected,item}));records.append(card);}
   for(const item of page.notices||[]){const card=el('article',undefined,'panel');card.append(el('h3',localizedCopy("ui.aviationReference.messageNOTAM",{value1:(String(item.notamId)),value2:(String(item.type))})),el('p',`${item.state} / ${item.facility}`),el('p',item.title),el('p',localizedCopy("ui.aviationReference.messagePublishedGeometryAndAvailableTimeAltitudeEvidenceCan")));const a=link(card,localizedCopy("ui.aviationReference.messageFindThisNoticeOnTheOfficialFAA"),officialTfrNoticeUrl(item.notamId));a.target='_blank';a.rel='noopener noreferrer';records.append(card);}
   if(!records.childElementCount)records.append(el('p',localizedCopy("ui.aviationReference.messageNoRetainedRecordsOnThisPageThis")));
   previous.disabled=offset===0;next.disabled=offset+50>=total;
  }catch(error){if(!closed&&own===epoch){setPresentationText(message,error.message);records.replaceChildren(el('p',localizedCopy("ui.aviationReference.messageCurrentAvailabilityIsUnknownExistingFlightRecording")));}}
  finally{if(!closed&&own===epoch){loading=false;refresh.disabled=!configured;}}
 }
 kind.onchange=()=>{offset=0;load();};await configuration();
}
