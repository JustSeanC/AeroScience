import {message as localizedCopy,setPresentationText} from './i18n.js';
import {t as textForUI} from './i18n.js';
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
 const dialog=el('dialog');dialog.className='download-activity online-access-dialog';setPresentationText(dialog,localizedCopy("ui.onlineAccess.messageOnlineAccessPrivacy"),"aria-label");
 const content=el('div',undefined,'online-access-content');
 dialog.append(el('h2',localizedCopy("ui.onlineAccess.messageOnlineAccessPrivacy")),content);content.append(el('p',localizedCopy("ui.onlineAccess.messageAllowAeroScienceToUseOnlineMapsDiscover")));
 const disclosure=el('ul');
 for(const text of [
  textForUI("ui.onlineAccess.messageMapProvidersReceiveYourIPAddressAnd"),
  textForUI("ui.onlineAccess.messageScientificProvidersIncludingNASAReceiveRequestedAreas"),
  textForUI("ui.onlineAccess.messageYourExistingDownloadLimitsStorageProtectionsBackground"),
  textForUI("ui.onlineAccess.messageOnlineAccessDoesNotGuaranteeDataAvailability")
 ])disclosure.append(el('li',text));content.append(disclosure);
 const status=el('p',localizedCopy("ui.onlineAccess.messageReadingCurrentOnlinePermissions"));status.setAttribute('role','status');status.setAttribute('aria-live','polite');
 const details=el('details');details.append(el('summary',localizedCopy("ui.onlineAccess.messageCurrentPermissionsAndFullProviderDisclosures")));const states=el('div');details.append(states);content.append(status,details);
 const actions=el('div',undefined,'action-row');dialog.append(actions);document.body.append(dialog);
 let closed=false,busy=false;
 const header=dialogHeader(dialog,{opener});
 function show(s){
  states.replaceChildren();
  for(const [title,enabled,text] of [
   [textForUI("ui.onlineAccess.messageOnlineMaps"),s.maps.configuration.preferences.onlineEnabled&&!s.maps.configuration.requiresPrivacyChoice,s.maps.privacy],
   [textForUI("ui.onlineAccess.messageScientificDiscoveryAndDownloads"),s.science.configuration.preferences.onlineEnabled&&s.science.configuration.preferences.privacyAccepted,s.science.configuration.privacy],
   [textForUI("ui.onlineAccess.messageFAAReferences"),s.aviation.configuration.onlineEnabled,s.aviation.configuration.privacy],
   [textForUI("ui.onlineAccess.messageAutomaticTFRNoticesAndBoundaries"),s.aviation.configuration.onlineEnabled&&s.tfr.tfrMonitoringEnabled,s.tfr.privacy]
  ])states.append(el('h3',`${title}: ${enabled?textForUI("ui.onlineAccess.messageAllowed"):textForUI("ui.onlineAccess.messageNotAllowed")}`),el('p',text));
  states.append(el('p',localizedCopy("ui.onlineAccess.messageYouCanChangeThesePermissionsIndividuallyLater")));
 }
 const allow=button(actions,localizedCopy("ui.onlineAccess.messageAllowOnlineAccess"),async()=>{
  if(busy)return;busy=true;later.disabled=true;setPresentationText(status,localizedCopy("ui.onlineAccess.messageSavingYourOnlineAccessChoices"));
  try{
   let s=await read(api);
   if(!s.maps.canConfigure||!s.science.canConfigure||!s.aviation.canConfigure)throw Error(textForUI("ui.onlineAccess.messageEnableHostOnlinePermissionsOnTheCompanion"));
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
   if(!closed){setPresentationText(status,localizedCopy("ui.onlineAccess.messageOnlineAccessWasNotFullyConfirmedSome",{value1:(String(error.message))}));status.setAttribute('role','alert');
    try{const current=await read(api);if(!closed)show(current);}catch{/* Retain the actual failure; no successful state is invented. */}}
  }finally{busy=false;if(!closed)later.disabled=false;}
 },'primary');allow.disabled=true;
 const later=button(actions,onOffline?localizedCopy("ui.onlineAccess.messageUseOfflineMaps"):localizedCopy("ui.onlineAccess.messageNotNow"),async()=>{if(onOffline)await onOffline();header.requestClose();});
 dialog.addEventListener('close',()=>{closed=true;dialog.remove();onClosed();});dialog.showModal();
 read(api).then(s=>{
  if(closed)return;show(s);
  const canConfigure=s.maps.canConfigure&&s.science.canConfigure&&s.aviation.canConfigure;
  allow.disabled=!canConfigure;
  if(canConfigure){setPresentationText(status,localizedCopy("ui.onlineAccess.messageChooseAllowOnlineAccessToGrantAll"));allow.focus();}
  else{setPresentationText(status,localizedCopy("ui.onlineAccess.messageEnableTheseChoicesOnTheCompanionPCS"));status.tabIndex=-1;status.focus();}
 }).catch(error=>{if(!closed){status.setAttribute('role','alert');setPresentationText(status,localizedCopy("ui.onlineAccess.messageCurrentOnlinePermissionsCouldNotBeRead",{value1:(String(error.message))}));}});
 return dialog;
}
