import {localeManifest,supportedLocale,useLocale,t,localizeTree} from './i18n.js';
import {node,formField,status} from './workstation-ui.js';

// P02a is browser-scoped only. P02b will wire the existing profile preference
// store and native adapters; no mission/science/organization settings are read.
export const preferenceKey='AeroScience.presentation.v1';
let settings={theme:'auto',locale:'en-US'},started=false,persistent=true;
const subscribers=new Set();
let media;
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
  window.addEventListener('storage',event=>{if(event.key===preferenceKey){settings=read();apply();}});
}
export function presentationPreferences() {return {...settings,persistent};}
export function updatePresentation(change) {
  settings={theme:['auto','light','dark'].includes(change.theme)?change.theme:settings.theme,
    locale:change.locale===undefined?settings.locale:supportedLocale(change.locale)};
  try {localStorage.setItem(preferenceKey,JSON.stringify({version:1,...settings}));persistent=true;}catch {persistent=false;}
  apply();return presentationPreferences();
}
export function presentationControls(parent) {
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
  return {element:section,dispose:()=>subscribers.delete(refresh)};
}
