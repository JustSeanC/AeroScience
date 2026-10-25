import {dialogHeader} from './dialog-ui.js';
import {node as el,button} from './workstation-ui.js';

// One explicit disclosure using existing host-owned settings APIs. Provider activity
// still follows their policies; no credentials, transfer approval or budget changes.
async function read(api){
 const [maps,science,aviation,tfr]=await Promise.all([api('/api/maps/settings'),api('/api/science/access'),api('/api/aviation/configuration'),api('/api/aviation/tfr/configuration')]);
 return {maps,science,aviation,tfr};
}
const ready=s=>s.maps.configuration.preferences.onlineEnabled&&!s.maps.configuration.requiresPrivacyChoice&&s.science.configuration.preferences.onlineEnabled&&s.science.configuration.preferences.privacyAccepted&&s.aviation.configuration.onlineEnabled&&s.tfr.tfrMonitoringEnabled;
export function openOnlineAccess(api,{opener=document.activeElement,onAllowed=()=>{},onClosed=()=>{},onOffline=null}={}){
 const dialog=el('dialog');dialog.className='download-activity online-access-dialog';dialog.setAttribute('aria-label','Online Access & Privacy');
 const content=el('div',undefined,'online-access-content');
 dialog.append(el('h2','Online Access & Privacy'),content);content.append(el('p','Allow AeroScience to use online maps, discover and acquire scientific data, and automatically update FAA/TFR notices and published boundaries.'));
 const disclosure=el('ul');
 for(const text of [
  'Map providers receive your IP address and viewed map areas. Optional Esri imagery uses your own public key and may incur provider charges.',
  'Scientific providers, including NASA, receive requested areas, dates and products. FAA services receive reference and TFR requests. AeroScience does not upload your mission recordings, observations or private notes to these providers.',
  'Your existing download limits, storage protections, background settings and network preferences stay in place. Individual Library downloads still require review and approval. NASA Earthdata authorization remains a separate connection.',
  'Online access does not guarantee data availability, current chart editions, valid scientific coverage or live restriction activation. Offline data remains available.'
 ])disclosure.append(el('li',text));content.append(disclosure);
 const status=el('p','Reading current online permissions…');status.setAttribute('role','status');status.setAttribute('aria-live','polite');
 const details=el('details');details.append(el('summary','Current permissions and full provider disclosures'));const states=el('div');details.append(states);content.append(status,details);
 const actions=el('div',undefined,'action-row');dialog.append(actions);document.body.append(dialog);
 let closed=false,busy=false;
 const header=dialogHeader(dialog,{opener});
 function show(s){
  states.replaceChildren();
  for(const [title,enabled,text] of [
   ['Online maps',s.maps.configuration.preferences.onlineEnabled&&!s.maps.configuration.requiresPrivacyChoice,s.maps.privacy],
   ['Scientific discovery and downloads',s.science.configuration.preferences.onlineEnabled&&s.science.configuration.preferences.privacyAccepted,s.science.configuration.privacy],
   ['FAA references',s.aviation.configuration.onlineEnabled,s.aviation.configuration.privacy],
   ['Automatic TFR notices and boundaries',s.aviation.configuration.onlineEnabled&&s.tfr.tfrMonitoringEnabled,s.tfr.privacy]
  ])states.append(el('h3',`${title}: ${enabled?'Allowed':'Not allowed'}`),el('p',text));
  states.append(el('p','You can change these permissions individually later in Settings.'));
 }
 const allow=button(actions,'Allow Online Access',async()=>{
  if(busy)return;busy=true;later.disabled=true;status.textContent='Saving your online access choices…';
  try{
   let s=await read(api);
   if(!s.maps.canConfigure||!s.science.canConfigure||!s.aviation.canConfigure)throw Error('Enable host online permissions on the companion PC’s local dashboard.');
   // Fetch each complete current preference immediately before its narrow mutation.
   // Already-allowed settings are not resaved, avoiding needless preparation resets.
   const maps=await api('/api/maps/settings');
   if(!maps.configuration.preferences.onlineEnabled||maps.configuration.requiresPrivacyChoice)await api('/api/maps/settings',{...maps.configuration.preferences,onlineEnabled:true,privacyAccepted:true});
   const science=await api('/api/science/access');
   if(!science.configuration.preferences.onlineEnabled||!science.configuration.preferences.privacyAccepted)await api('/api/science/access',{...science.configuration.preferences,onlineEnabled:true,privacyAccepted:true});
   const aviation=await api('/api/aviation/configuration');
   if(!aviation.configuration.onlineEnabled)await api('/api/aviation/configuration',{onlineEnabled:true});
   const tfr=await api('/api/aviation/tfr/configuration');
   if(!tfr.tfrMonitoringEnabled)await api('/api/aviation/tfr/configuration',{onlineEnabled:true});
   s=await read(api);if(!ready(s))throw Error('A permission changed during saving. Review current settings and try again.');
   await onAllowed(s);if(!closed)dialog.close();
  }catch(error){
   if(!closed){status.textContent='Online access was not fully confirmed. Some settings may have saved. '+error.message;status.setAttribute('role','alert');
    try{const current=await read(api);if(!closed)show(current);}catch{/* Retain the actual failure; no successful state is invented. */}}
  }finally{busy=false;if(!closed)later.disabled=false;}
 },'primary');allow.disabled=true;
 const later=button(actions,onOffline?'Use Offline Maps':'Not Now',async()=>{if(onOffline)await onOffline();header.requestClose();});
 dialog.addEventListener('close',()=>{closed=true;dialog.remove();onClosed();});dialog.showModal();
 read(api).then(s=>{
  if(closed)return;show(s);
  const canConfigure=s.maps.canConfigure&&s.science.canConfigure&&s.aviation.canConfigure;
  allow.disabled=!canConfigure;
  if(canConfigure){status.textContent='Choose Allow Online Access to grant all listed permissions. Nothing is enabled by opening or dismissing this window.';allow.focus();}
  else{status.textContent='Enable these choices on the companion PC’s local dashboard. Paired devices can review this disclosure but cannot grant host consent.';status.tabIndex=-1;status.focus();}
 }).catch(error=>{if(!closed){status.setAttribute('role','alert');status.textContent='Current online permissions could not be read. '+error.message;}});
 return dialog;
}
