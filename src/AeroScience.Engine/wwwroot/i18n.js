import {localeData} from './i18n/catalog.generated.js';

import {createTranslator,supportedLocale as resolveSupportedLocale} from './i18n-format.js';
export {createTranslator} from './i18n-format.js';
export const supportedLocale = (value,manifest=localeData.manifest) => resolveSupportedLocale(value,manifest);

export const localeManifest = localeData.manifest;
let current = createTranslator(localeData);
export const t = (key, values, fallback) => current.text(key,values&&Object.fromEntries(Object.entries(values).map(([name,value])=>[name,copies.has(value)?String(value):value])),fallback);
export const effectiveLocale = () => current.effectiveLocale;
const copies=new WeakMap(),bindings=new WeakMap(),ownedListeners=new WeakMap(),listeners=new Set();
let observer;
function walk(root,visit){if(!root)return;const pending=[root];while(pending.length){const node=pending.pop();visit(node);if(node.childNodes)for(let i=node.childNodes.length-1;i>=0;i--)pending.push(node.childNodes[i]);}}
function refreshBindings(root){walk(root,node=>{for(const render of bindings.get(node)?.values()||[])render();for(const callback of ownedListeners.get(node)||[])callback(current);});}
function observeBindings(){
  if(observer||typeof document==='undefined'||typeof MutationObserver==='undefined')return;
  observer=new MutationObserver(records=>{for(const record of records)for(const node of record.addedNodes)if(node.isConnected)refreshBindings(node);});
  observer.observe(document,{childList:true,subtree:true});
}
// Explicit presentation values only. Raw strings (including source names,
// notes, original evidence, hashes and UTC) never acquire translation metadata.
export function message(key,values,fallback){
  const copy={toString:()=>t(key,values,fallback)};
  const render=()=>t(key,values,fallback);render.key=key;copies.set(copy,render);return Object.freeze(copy);
}
export function presentationTextNode(value){const node=document.createTextNode(String(value));if(copies.has(value))setPresentationText(node,value);return node;}
export function setPresentationText(element,value,attribute='textContent',property=false,staticBinding=false){
  observeBindings();
  // A controller replacing a placeholder owns the field from now on. A later
  // language/theme refresh must never restore the initial aircraft/status text.
  if(!staticBinding)element.removeAttribute?.(attribute==='textContent'?'data-i18n':'data-i18n-'+attribute);
  let textNode,initial=true;
  const render=()=>{const text=copies.has(value)?copies.get(value)():value??'';
    if(attribute==='textContent'&&copies.has(value)&&element.nodeType===1){element.lang=current.messageLocale(copies.get(value).key);element.dataset.localeFallback=String(element.lang!==current.locale);}
    if(attribute==='textContent'){
      if(element.nodeType===3)element.nodeValue=String(text);
      else if(initial){element.textContent=text;textNode=element.firstChild;}
      else if(textNode?.parentNode===element)textNode.nodeValue=String(text);
      else if(!element.ownerDocument)element.textContent=text; // non-DOM test adapter
      // Labels may acquire inputs/selects after construction. Update only our
      // original text node; never replace controls, dirty drafts or keyboard focus.
    }else if(property)element[attribute]=text;
    else element.setAttribute(attribute,text);
    initial=false;};
  render.key=copies.get(value)?.key;
  let fields=bindings.get(element);
  if(copies.has(value)){if(!fields){fields=new Map();bindings.set(element,fields);}fields.set(attribute,render);}
  else if(fields){fields.delete(attribute);if(!fields.size)bindings.delete(element);}
  render();return value;
}
export function onLocaleChange(callback,{owner}={}){
  if(!owner){listeners.add(callback);return()=>listeners.delete(callback);}
  observeBindings();let callbacks=ownedListeners.get(owner);if(!callbacks){callbacks=new Set();ownedListeners.set(owner,callbacks);}callbacks.add(callback);return()=>callbacks.delete(callback);
}
// Read-only QA evidence: message identities only, never original values or notes.
export function presentationCoverage(root=document){const keys=[];walk(root,node=>{for(const render of bindings.get(node)?.values()||[])if(render.key)keys.push(render.key);});return keys;}
export function useLocale(locale,{root=typeof document==='undefined'?null:document}={}) {
  current = createTranslator(localeData,locale);
  refreshBindings(root);
  for(const callback of listeners)callback(current);
  return current;
}

// Only explicit bindings change. Original values are never matched as prose.
// Weak ownership also prevents removed live views from becoming location history.
export function localizeTree(root = document, translate = t) {
  const attributes=['aria-label','aria-description','placeholder','title','alt'];
  const selector = '[data-i18n],'+attributes.map(name=>'[data-i18n-'+name+']').join(',');
  const elements = Array.from(root.querySelectorAll(selector));
  if (root.matches?.(selector)) elements.unshift(root);
  for (const element of elements) {
    if (element.dataset.i18n&&!bindings.get(element)?.has('textContent'))setPresentationText(element,translate===t?message(element.dataset.i18n):translate(element.dataset.i18n),'textContent',false,true);
    for(const attribute of attributes){const key=element.getAttribute('data-i18n-'+attribute);if(key&&!bindings.get(element)?.has(attribute))setPresentationText(element,translate===t?message(key):translate(key),attribute,false,true);}
  }
}

// Optional classic moving-map presentation bridge; original values stay parameters.
if(typeof window!=="undefined")window.AeroScienceMapText=(key,fallback,values)=>t(key,values,fallback);
