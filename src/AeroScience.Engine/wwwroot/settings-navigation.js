import {message as localizedCopy,setPresentationText} from './i18n.js';
import {t as textForUI} from './i18n.js';
import {splitFragment} from './navigation-routes.js';
export const settingsCategories = [
  ["general",localizedCopy("ui.settingsNavigation.messageGeneral")],["maps",localizedCopy("ui.basemaps.messageMapsBasemaps")],["science",localizedCopy("ui.scientificSources.messageScientificDataSources")],
  ["units",localizedCopy("ui.settingsNavigation.messageUnitsDisplay")],["storage",localizedCopy("ui.settingsNavigation.messageStorageData")],["toolbar",localizedCopy("ui.settingsNavigation.messageMSFSToolbar")],
  ["updates",localizedCopy("ui.applicationUpdates.messageUpdates")],["advanced",localizedCopy("ui.settingsNavigation.messageAdvancedDiagnostics")],["support",localizedCopy("nav.help")]
];
export function settingsLocation(hash=location.hash){
  const {path,search:query}=splitFragment(hash);
  return {page:path.split("/")[0],category:settingsCategories.some(([id])=>id===path.split("/")[1])?path.split("/")[1]:"general",query:new URLSearchParams(query)};
}
export function settingsNavigation(parent){
  const nav=document.createElement("nav");nav.className="settings-navigation";setPresentationText(nav,localizedCopy("ui.settingsNavigation.messageSettingsCategories"),"aria-label");
  const panels=new Map();
  for(const [id,title] of settingsCategories){const a=document.createElement("a");a.href="#settings/"+id;setPresentationText(a,title);nav.append(a);const section=document.createElement("section");section.id="settings-"+id;setPresentationText(section,title,"aria-label");panels.set(id,section);}
  parent.append(nav,...panels.values());
  const show=()=>{const current=settingsLocation().category;for(const [id,panel] of panels)panel.hidden=id!==current;for(const a of nav.children){if(a.hash==="#settings/"+current)a.setAttribute("aria-current","page");else a.removeAttribute("aria-current");}};
  return {panels,show};
}
