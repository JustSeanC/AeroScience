// One dismissal pattern. Closing a view never owns/cancels its background operation.
import {t} from './i18n.js';
const states=new WeakMap();
export function popupHeader(name,onClose,heading=null,close=null){
 const header=document.createElement('header');header.className='popup-header';
 heading ||= document.createElement('h2');if(!heading.textContent)heading.textContent=name;
 close ||= document.createElement('button');close.type='button';close.className='popup-close secondary';close.textContent='×';if(!close.hasAttribute('aria-label'))close.setAttribute('aria-label',t('common.closeNamed',{name}));close.onclick=onClose;
 header.append(heading,close);return {header,close};
}
export function layerPopup(control){
 const container=control.getContainer(),section=container.querySelector('.leaflet-control-layers-list'),toggle=container.querySelector('.leaflet-control-layers-toggle');
 const dismiss=()=>{control.collapse();toggle.focus({preventScroll:true});};const {header}=popupHeader('Map overlays',dismiss);section.prepend(header);section.setAttribute('aria-label','Map overlay choices');toggle.setAttribute('aria-label','Show map overlays');
 container.addEventListener('keydown',event=>{if(event.key==='Escape'){event.preventDefault();event.stopPropagation();dismiss();}});
 return control;
}
export function dialogHeader(dialog,{title,opener=document.activeElement,protectEdits=false,onDismiss,reuseHeading=true}={}){
 if(states.has(dialog))return states.get(dialog);
 const name=title||dialog.getAttribute('aria-label')||dialog.querySelector('h1,h2,h3')?.textContent||'dialog';
 if(!dialog.hasAttribute('aria-label')&&!dialog.hasAttribute('aria-labelledby'))dialog.setAttribute('aria-label',name);
 const {header,close}=popupHeader(name,()=>requestClose(),reuseHeading?dialog.querySelector('h1,h2,h3'):null,dialog.querySelector('#close-detail'));dialog.prepend(header);dialog.classList.add('managed-popup');
 let dirty=false,confirmation=null,returnTo=opener;
 dialog.addEventListener('beforetoggle',event=>{if(event.newState==='open'&&document.activeElement!==document.body&&!dialog.contains(document.activeElement))returnTo=document.activeElement;});
 const requestClose=()=>{
  // close() clears open before its queued close event. A new dismissal in that
  // gap still needs a warning; an older close event must not clear the new one.
  if(dirty&&protectEdits){if(confirmation?.open)return;const warning=document.createElement('dialog');confirmation=warning;warning.setAttribute('aria-label',t('dialog.unsaved'));
   const text=document.createElement('p');text.textContent=t('dialog.unsavedWarning');warning.append(text);
   const keep=document.createElement('button');keep.type='button';keep.textContent=t('dialog.keepEditing');keep.autofocus=true;keep.onclick=()=>warning.close();
   const discard=document.createElement('button');discard.type='button';discard.textContent=t('dialog.discardEdits');discard.className='danger';discard.onclick=()=>{dirty=false;warning.close();finish();};warning.append(keep,discard);
   warning.addEventListener('close',()=>{if(confirmation===warning)confirmation=null;warning.remove();});document.body.append(warning);dialogHeader(warning,{title:t('dialog.unsaved'),opener:close});warning.showModal();return;
  }finish();
 };
 function finish(){if(onDismiss)onDismiss();else dialog.close();}
 close.onclick=requestClose;
 dialog.addEventListener('cancel',event=>{event.preventDefault();requestClose();});
 dialog.addEventListener('input',event=>{if(protectEdits&&!event.target.matches('[data-transient]'))dirty=true;});
 dialog.addEventListener('close',()=>queueMicrotask(()=>{const top=[...document.querySelectorAll('dialog[open]')].at(-1);if(returnTo?.isConnected&&(!top||top.contains(returnTo)))returnTo.focus({preventScroll:true});}));
 const result={requestClose,isDirty:()=>dirty,markSaved:()=>{dirty=false;},markDirty:()=>{dirty=true;}};states.set(dialog,result);return result;
}
export function requestDialogClose(dialog){const state=states.get(dialog);if(state)state.requestClose();else dialog.close();}
export function markDialogSaved(dialog){states.get(dialog)?.markSaved();}
