import {localeManifest,supportedLocale,useLocale,t,localizeTree} from './i18n.js';
import {node,formField,status} from './workstation-ui.js';

// Browser overrides remain local. Host defaults use only the authenticated,
// revisioned presentation API; no migration/upload of browser preferences.
export const preferenceKey='AeroScience.presentation.v1';
let settings={theme:'auto',locale:'en-US'},started=false,persistent=true;
const subscribers=new Set();
let media;
let host=null,hostApi=null,hostError='',hostLoading=false,hostTimer,hostGeneration=0;
const hostSubscribers=new Set();
function hasOverride(){try{return JSON.parse(localStorage.getItem(preferenceKey)||'null')?.version===1;}catch{return false;}}
function publishHost(){for(const callback of hostSubscribers)callback();}
export async function refreshPresentationHost(){
  if(!hostApi||hostLoading)return;hostLoading=true;
  const ownGeneration=hostGeneration;
  try{
    const value=await hostApi('/api/settings/presentation');
    if(ownGeneration!==hostGeneration||host&&host.instanceId===value.instanceId&&value.preferences?.revision<host.preferences.revision)return;
    if(value.version!==1||value.preferences?.version!==1)throw Error(t('settings.hostUnavailable'));
    host=value;hostError=value.warning||(value.catalogVersion!==localeManifest.catalogVersion?t('toolbar.catalogFallback'):'');
    if(!hasOverride()){settings={theme:host.preferences.theme,locale:supportedLocale(host.preferences.locale)};apply();}
  }catch(error){if(ownGeneration===hostGeneration)hostError=error.message||t('settings.hostUnavailable');}
  finally{hostLoading=false;publishHost();}
}
export async function connectPresentationHost(api){
  hostApi=api;hostGeneration++;await refreshPresentationHost();
  if(!hostTimer){hostTimer=setInterval(()=>{if(!document.hidden)refreshPresentationHost();},10000);window.addEventListener('focus',refreshPresentationHost);}
}
function read() {
  let saved;
  try {saved=JSON.parse(localStorage.getItem(preferenceKey)||'null');} catch {persistent=false;}
  const candidate=saved?.version===1?saved:{};
  const preferred=(navigator.languages||[navigator.language]).find(value=>localeManifest.locales.some(item=>item.id.toLowerCase()===String(value).toLowerCase()));
  return {theme:['auto','light','dark'].includes(candidate.theme)?candidate.theme:'auto',locale:supportedLocale(candidate.locale||preferred)};
}
function apply() {
  const translated=useLocale(settings.locale),root=document.documentElement;
  root.dataset.theme=settings.theme;
  root.dataset.resolvedTheme=settings.theme==='auto'?(media?.matches?'dark':'light'):settings.theme;
  root.dataset.requestedLocale=settings.locale;root.lang=translated.effectiveLocale;
  root.dir=localeManifest.locales.find(item=>item.id===translated.effectiveLocale).direction;
  const color=document.querySelector('meta[name="theme-color"]');
  if(color)color.content=root.dataset.resolvedTheme==='dark'?'#0b1118':'#f1f5f9';
  localizeTree();for(const callback of subscribers)callback({...settings,persistent});
}
export function initializePresentation() {
  if(started)return;started=true;settings=read();media=window.matchMedia('(prefers-color-scheme: dark)');
  media.addEventListener('change',apply);apply();
  window.addEventListener('storage',event=>{if(event.key===preferenceKey){settings=!hasOverride()&&host?{theme:host.preferences.theme,locale:host.preferences.locale}:read();apply();}});
}
export function presentationPreferences() {return {...settings,persistent};}
export function updatePresentation(change) {
  settings={theme:['auto','light','dark'].includes(change.theme)?change.theme:settings.theme,
    locale:change.locale===undefined?settings.locale:supportedLocale(change.locale)};
  try {localStorage.setItem(preferenceKey,JSON.stringify({version:1,...settings}));persistent=true;}catch {persistent=false;}
  apply();return presentationPreferences();
}
export function presentationControls(parent,{includeHost=true}={}) {
  const section=node('section',undefined,'presentation-settings');section.setAttribute('aria-label',t('settings.presentation'));
  const title=node('h2',t('settings.presentation'));title.dataset.i18n='settings.presentation';
  const scope=node('p',t('settings.browserOnly'));scope.dataset.i18n='settings.browserOnly';
  const fields=node('div',undefined,'presentation-fields');section.append(title,scope,fields);parent.append(section);
  const theme=document.createElement('select');
  for(const [value,key] of [['auto','settings.themeAuto'],['light','settings.themeLight'],['dark','settings.themeDark']]){
    const option=node('option',t(key));option.value=value;option.dataset.i18n=key;theme.append(option);
  }
  const themeField=formField(fields,t('settings.theme'),theme);themeField.label.dataset.i18n='settings.theme';
  const language=document.createElement('select');
  for(const locale of localeManifest.locales){const option=node('option',locale.review==='preview'?t('settings.previewOption',{name:locale.name}):locale.name);option.value=locale.id;option.lang=locale.id;language.append(option);}
  const languageField=formField(fields,t('settings.language'),language);languageField.label.dataset.i18n='settings.language';
  const feedback=status(section,'');
  function refresh(value){
    theme.value=value.theme;language.value=value.locale;
    const locale=localeManifest.locales.find(item=>item.id===value.locale);
    feedback.textContent=!value.persistent?t('settings.notSaved'):locale.review==='preview'?t('settings.previewNotice',{name:locale.name}):t('settings.englishNotice');
  }
  theme.onchange=()=>updatePresentation({theme:theme.value});language.onchange=()=>updatePresentation({locale:language.value});
  subscribers.add(refresh);refresh(presentationPreferences());
  if(!includeHost)return {element:section,dispose:()=>subscribers.delete(refresh)};
  const hostSection=node('section',undefined,'host-presentation-settings');parent.append(hostSection);
  const hostTitle=node('h2',t('settings.hostTitle')),hostScope=node('p',t('settings.hostScope'));
  const hostFields=node('div',undefined,'presentation-fields');hostSection.append(hostTitle,hostScope,hostFields);
  const hostTheme=theme.cloneNode(true),hostLanguage=language.cloneNode(true);
  for(const control of [hostTheme,hostLanguage]){control.removeAttribute('id');control.removeAttribute('aria-describedby');}
  formField(hostFields,t('settings.hostTheme'),hostTheme);formField(hostFields,t('settings.hostLanguage'),hostLanguage);
  const save=node('button',t('settings.hostSave')),reload=node('button',t('settings.hostReload')),follow=node('button',t('settings.hostFollow'));
  save.type=reload.type=follow.type='button';hostSection.append(save,reload,follow);
  const hostFeedback=status(hostSection,''),nativeNotice=node('p',t('settings.hostNativeNotice'));hostSection.append(nativeNotice);
  let draft=false,saving=false,revision=null;
  hostTheme.onchange=hostLanguage.onchange=()=>{draft=true;};
  function refreshHost(){
    if(host&&!draft){hostTheme.value=host.preferences.theme;hostLanguage.value=host.preferences.locale;revision=host.preferences.revision;}
    hostTheme.disabled=hostLanguage.disabled=save.disabled=saving||!host?.canWrite||!!hostError;
    hostFeedback.textContent=hostError||(!host?t('settings.hostUnavailable'):!host.canWrite?t('settings.hostReadOnly'):t('settings.hostScope'));
  }
  save.onclick=async()=>{
    const ownGeneration=++hostGeneration;
    saving=true;refreshHost();
    try{const result=await hostApi('/api/settings/presentation',{expectedRevision:revision,theme:hostTheme.value,locale:hostLanguage.value});if(ownGeneration!==hostGeneration)return;
      hostGeneration++;host=result;draft=false;hostError='';
      if(!hasOverride()){settings={theme:host.preferences.theme,locale:host.preferences.locale};apply();}refreshHost();hostFeedback.textContent=t('settings.hostSaved');
    }catch(error){hostFeedback.textContent=error.message;hostFeedback.setAttribute('role','alert');}
    finally{saving=false;save.disabled=!host?.canWrite;hostTheme.disabled=hostLanguage.disabled=!host?.canWrite;}
  };
  reload.onclick=async()=>{draft=false;await refreshPresentationHost();};
  follow.onclick=()=>{if(!host)return;try{localStorage.removeItem(preferenceKey);persistent=true;}catch{persistent=false;}
    settings={theme:host.preferences.theme,locale:host.preferences.locale};apply();};
  hostSubscribers.add(refreshHost);refreshHost();
  return {element:section,dispose:()=>{subscribers.delete(refresh);hostSubscribers.delete(refreshHost);}};
}
