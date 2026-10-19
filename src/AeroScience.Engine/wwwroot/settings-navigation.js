export const settingsCategories = [
  ["general","General"],["maps","Maps & Basemaps"],["science","Scientific Data Sources"],
  ["units","Units & Display"],["storage","Storage & Data"],["toolbar","MSFS Toolbar"],
  ["updates","Updates"],["advanced","Advanced & Diagnostics"],["support","Help & Support"]
];
export function settingsLocation(hash=location.hash){
  const [path,query=""]=hash.replace(/^#/,"").split("?");
  return {page:path.split("/")[0],category:settingsCategories.some(([id])=>id===path.split("/")[1])?path.split("/")[1]:"general",query:new URLSearchParams(query)};
}
export function settingsNavigation(parent){
  const nav=document.createElement("nav");nav.className="settings-navigation";nav.setAttribute("aria-label","Settings categories");
  const panels=new Map();
  for(const [id,title] of settingsCategories){const a=document.createElement("a");a.href="#settings/"+id;a.textContent=title;nav.append(a);const section=document.createElement("section");section.id="settings-"+id;section.setAttribute("aria-label",title);panels.set(id,section);}
  parent.append(nav,...panels.values());
  const show=()=>{const current=settingsLocation().category;for(const [id,panel] of panels)panel.hidden=id!==current;for(const a of nav.children){if(a.hash==="#settings/"+current)a.setAttribute("aria-current","page");else a.removeAttribute("aria-current");}};
  return {panels,show};
}
