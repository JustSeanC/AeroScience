import { scientificAccessSettings } from "./scientific-access.js";
import { openHyperspectralImport } from "./hyperspectral-import.js";
import { ResearchMap } from "./map.js";
const el=(tag,text)=>{const e=document.createElement(tag);if(text!==undefined)e.textContent=text;return e;};
const kinds={terrain:["Terrain & Elevation","USGS 3DEP / Copernicus GLO-30","Numeric geographic heights; DEM/DSM distinctions and vertical datums are retained. These are not aircraft laser returns.","Single-band numeric elevation DEM GeoTIFF"],thermal:["Thermal Imaging","Landsat 8/9 Collection 2 Level-2 surface temperature","Historical satellite temperature with original quality masks.","Same-scene ST_B10 + QA_PIXEL GeoTIFFs; QA_RADSAT when supplied; original MTL JSON preferred"],multispectral:["Multispectral Imaging","Copernicus Sentinel-2 Level-2A","Historical reflectance supporting RGB and NDVI, with scene classification masks.","B02/B03/B04/B08 + SCL numeric GeoTIFFs, with original reflectance scaling metadata"],hyperspectral:["Hyperspectral Imaging","Your compatible ENVI dataset","User-supplied historical spectra. Automatic hyperspectral downloading is not required or supported.","Georeferenced ENVI .hdr + binary, 16–512 spectral channels"]};
function action(label,run){const b=el("button",label);b.type="button";b.className="secondary";b.onclick=async()=>{b.disabled=true;try{await run();}catch(e){alert(e.message);}finally{b.disabled=false;}};return b;}
function link(parent,label,url){const a=el("a",label);a.href=url;a.target="_blank";a.rel="noopener";parent.append(a,document.createTextNode(" · "));}
export async function scientificSourcesSettings(parent,api){
  const root=el("section");root.id="scientific-data-sources";parent.append(root);
  root.append(el("h2","Scientific Data Sources"),el("p","Automatic sources contact public providers only with your separate science-data permission. Imported datasets work offline within their actual coverage. Source selection does not enable or disable recording."));
  let query=new URLSearchParams(location.hash.split("?")[1]||"");
  const categories=el("div"),checked=el("p");root.append(categories,checked);let maps=[];
  async function refreshCategories(){
  for(const map of maps)map.destroy();maps=[];categories.replaceChildren();
  const [access,sources,state]=await Promise.all([api("/api/science/access"),api("/api/science/sources"),api("/api/state")]);
  const age=Date.now()-Date.parse(state.telemetry?.timestampUtc);const point=state.connection?.state==="connected"&&age>=-2000&&age<6000?state.telemetry:null;let local=sources;
  if(point)local=await api(`/api/science/sources?latitude=${point.latitude}&longitude=${point.longitude}`);
  for(const [kind,[title,provider,description,formats]] of Object.entries(kinds)){
    const section=el("article");section.id="source-"+kind;section.className="panel source-category";categories.append(section);section.append(el("h3",title),el("p",provider),el("p",description));
    const label=el("label", "Source mode "),select=el("select");select.setAttribute("aria-label",title+" source mode");const choices=kind==="hyperspectral"?[["local","My Datasets Only"]]:[["automatic","Automatic"],["local","My Datasets Only"],["prefer-local","Prefer My Datasets, Allow Automatic Fallback"]];
    for(const [value,text] of choices){const o=el("option",text);o.value=value;o.selected=value===(access.configuration.preferences.sourceModes?.[kind]||(kind==="hyperspectral"?"local":"automatic"));select.append(o);}select.disabled=!access.canConfigure;
    const save=el("span");select.onchange=async()=>{try{await api("/api/science/source-mode",{kind,mode:select.value});save.textContent="Saved · datasets retained";}catch(e){save.textContent=e.message;}};label.append(select);section.append(label,save);
    section.append(el("p",`Local input: ${formats}`),action(kind==="hyperspectral"?"Import Hyperspectral Dataset":"Import Dataset…",()=>openHyperspectralImport(api,kind)));
    link(section,"Open Beginner Guide",kind==="hyperspectral"?"/help/remote-sensing/using-your-own-hyperspectral-dataset.md":"/help/remote-sensing/scientific-data-sources.md#"+kind);
    const entries=local.filter(s=>s.source.manifest.kind===kind);
    section.append(el("p",`${entries.length} imported/prepared datasets · ${(entries.reduce((n,e)=>n+e.source.files.reduce((b,f)=>b+f.bytes,0),0)/1048576).toFixed(1)} MiB retained`));
    if(!entries.length)section.append(el("strong",kind==="hyperspectral"?"Dataset Required":"No local datasets"),el("p",kind==="hyperspectral"?"Import a compatible dataset covering your research area. No account or automatic download is required.":"Choose Automatic with scientific-data access enabled, or import compatible local data."));
    if(kind!=="hyperspectral"&&!access.configuration.preferences.onlineEnabled){const a=el("a","Enable Scientific Data Access");a.href="#settings/science?section=access";section.append(a);}
    const list=el("div");section.append(list);
    for(const entry of entries){const s=entry.source,m=s.manifest;const row=el("article");row.className="source-notice";list.append(row);row.append(el("h4",m.name),el("p",`${m.provider} · Observed: ${m.observationUtc||"Unknown / composite"} · ${entry.filesAvailable?"Available":"Dataset Invalid"} · ${point?(entry.coversPoint?"Covers current aircraft point; masks still apply":"Outside aircraft coverage"):"Mission-area suitability: select a position to check"}`));if(entry.error)row.append(el("p",entry.error));
      const details=el("details");details.append(el("summary","View Dataset Details"),el("p",`${m.license} · ${m.citation} · ${m.bands.length} bands · ${m.verticalDatum}`));for(const [id,g] of Object.entries(s.grids))details.append(el("p",`${id}: ${g.crs} · ${g.width}×${g.height} · ${g.pixelWidth}×${g.pixelHeight} native grid units`));details.append(el("p",m.notes||"Original file hashes and source metadata retained."));row.append(details);
      let map;
      const coverage=action("View Coverage",async()=>{if(map){map.fit();return;}const vertices=await api(`/api/science/sources/${s.id}/coverage`);const panel=el("div");row.append(panel);map=new ResearchMap(panel);maps.push(map);map.set({plan:{surveyAreas:[{name:m.name,vertices}]}});});row.append(coverage);
      const active=!(access.configuration.preferences.excludedSourceIds||[]).includes(s.id);row.append(action(active?"Exclude from active selection":"Use for active selection",async()=>{await api("/api/science/source-selection",{sourceId:s.id,enabled:!active});row.append(el("p",active?"Excluded from active selection. Original files and products retained.":"Included in source selection. Original files unchanged."));}));
      if(query.get("dataset")===s.id)coverage.click();
    }
    section.append(action("Manage Datasets",async()=>{list.hidden=!list.hidden;}));
  }
  checked.textContent="Dataset status checked: "+new Date().toLocaleTimeString()+". Coverage checks use the current aircraft point; View Coverage and Planner readiness assess your mission geography.";
  }
  await refreshCategories();
  root.append(action("Refresh dataset status",refreshCategories));
  const refresh=()=>{if(root.isConnected)refreshCategories().catch(error=>checked.textContent=error.message);else window.removeEventListener("aeroscience-sources-changed",refresh);};
  window.addEventListener("aeroscience-sources-changed",refresh);
  const accessPanel=el("details");accessPanel.id="scientific-access-controls";accessPanel.open=query.get("section")==="access";accessPanel.append(el("summary","Online access, predictive preparation & download budgets"));root.append(accessPanel);await scientificAccessSettings(accessPanel,api);
  const advanced=el("details");advanced.append(el("summary","Advanced JSON source-collection import"),el("p","For expert users supplying a reviewed manifest. Ordinary compatible imports use Import Dataset above."),action("Import scientific source collection…",()=>api("/api/desktop/science-import",{})));link(advanced,"Advanced source reference","/help/remote-sensing/sources.md");root.append(advanced);
  function applyLocation(){
    if(!root.isConnected){window.removeEventListener("hashchange",applyLocation);return;}
    if(!location.hash.startsWith("#settings/science"))return;
    query=new URLSearchParams(location.hash.split("?")[1]||"");
    if(query.get("section")==="access"){accessPanel.open=true;accessPanel.scrollIntoView({block:"start"});}
    if(query.get("source")){document.querySelector("#source-"+query.get("source"))?.scrollIntoView({block:"start"});if(query.get("import")==="1")openHyperspectralImport(api,query.get("source"));}
  }
  window.addEventListener("hashchange",applyLocation);applyLocation();
}
