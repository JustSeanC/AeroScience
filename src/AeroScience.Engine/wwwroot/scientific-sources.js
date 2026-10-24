import { scientificAccessSettings } from "./scientific-access.js";
import { openHyperspectralImport } from "./hyperspectral-import.js";
import {openDownloadActivity} from "./download-activity.js";
import { ResearchMap } from "./map.js";
const el=(tag,text)=>{const e=document.createElement(tag);if(text!==undefined)e.textContent=text;return e;};
const kinds={terrain:["Terrain & Elevation","USGS 3DEP / Copernicus GLO-30","Numeric geographic heights; DEM/DSM distinctions and vertical datums are retained. These are not aircraft laser returns.","Single-band numeric elevation DEM GeoTIFF"],thermal:["Thermal Imaging","Landsat 8/9 Collection 2 Level-2 surface temperature","Historical satellite temperature with original quality masks.","Same-scene ST_B10 + QA_PIXEL GeoTIFFs; QA_RADSAT when supplied; original MTL JSON preferred"],multispectral:["Multispectral Imaging","Copernicus Sentinel-2 Level-2A","Historical reflectance supporting RGB and NDVI, with scene classification masks.","B02/B03/B04/B08 + SCL numeric GeoTIFFs, with original reflectance scaling metadata"],hyperspectral:["Hyperspectral Imaging","NASA AVIRIS-Classic / AVIRIS-NG or compatible ENVI files","Discover historical flightlines and use authorized Earthdata Download & Import for supported originals. Manual ENVI import remains available; EMIT native import is deferred.","Georeferenced ENVI .hdr + binary, 16–512 spectral channels"]};
function action(label,run){const b=el("button",label);b.type="button";b.className="secondary";b.onclick=async()=>{b.disabled=true;try{await run();}catch(e){alert(e.message);}finally{b.disabled=false;}};return b;}
function link(parent,label,url){const a=el("a",label);a.href=url.startsWith("/help/")?"#help?guide="+encodeURIComponent(url.slice(6).split("#")[0])+(url.includes("#")?"&anchor="+encodeURIComponent(url.split("#")[1]):""):url;if(!url.startsWith("/help/")){a.target="_blank";a.rel="noopener";}parent.append(a,document.createTextNode(" · "));}
export async function scientificSourcesSettings(parent,api){
  const root=el("section");root.id="scientific-data-sources";parent.append(root);
  root.append(el("h2","Scientific Data Sources"),el("p","Automatic sources contact public providers only with your separate science-data permission. Imported datasets work offline within their actual coverage. Source selection does not enable or disable recording."));
  const online=el("p"),configure=action("Enable Online Scientific Data Access",async()=>{document.getElementById("scientific-access-controls").open=true;document.getElementById("scientific-access-controls").scrollIntoView({block:"start"});});root.append(online,configure,action("View Download Activity",()=>openDownloadActivity(api)));
  let query=new URLSearchParams(location.hash.split("?")[1]||"");
  const categories=el("div"),checked=el("p");root.append(categories,checked);let maps=[],generation=0;
  async function refreshCategories(){
  const own=++generation;for(const map of maps)map.destroy();maps=[];categories.replaceChildren();
  const access=await api("/api/science/access");if(own!==generation||!root.isConnected)return;
  online.textContent="Online scientific data access: "+(access.configuration.preferences.onlineEnabled?"Enabled":"Disabled (local datasets remain available)");configure.textContent=access.configuration.preferences.onlineEnabled?"Configure Online Scientific Data Access":"Enable Online Scientific Data Access";
  for(const [kind,[title,provider,description,formats]] of Object.entries(kinds)){
    const section=el("article");section.id="source-"+kind;section.className="panel source-category";categories.append(section);section.append(el("h3",title),el("p",provider),el("p",description));
    const label=el("label", "Source mode "),select=el("select");select.setAttribute("aria-label",title+" source mode");const choices=kind==="hyperspectral"?[["local","My Datasets Only"]]:[["automatic","Automatic"],["local","My Datasets Only"],["prefer-local","Prefer My Datasets, Allow Automatic Fallback"]];
    for(const [value,text] of choices){const o=el("option",text);o.value=value;o.selected=value===(access.configuration.preferences.sourceModes?.[kind]||(kind==="hyperspectral"?"local":"automatic"));select.append(o);}select.disabled=!access.canConfigure;
    const save=el("span");select.onchange=async()=>{try{await api("/api/science/source-mode",{kind,mode:select.value});save.textContent="Saved · datasets retained";}catch(e){save.textContent=e.message;}};label.append(select);section.append(label,save);
    section.append(el("p",`Local input: ${formats}`),action(kind==="hyperspectral"?"Import Hyperspectral Dataset":"Find & Download Data",()=>{location.hash="#library/download?science="+kind;}),action("Import Files Already on My Computer",()=>openHyperspectralImport(api,kind)));
    link(section,"Open Beginner Guide",kind==="hyperspectral"?"/help/remote-sensing/using-your-own-hyperspectral-dataset.md":"/help/remote-sensing/scientific-data-sources.md#"+kind);
    const inventory=el("p","Reading complete Library counts…"),list=el("div");section.append(inventory,list);
    const collections=await api('/api/science/source-collections?kind='+kind);if(own!==generation||!section.isConnected)return;
    inventory.textContent=`${collections.regions} imported/prepared datasets in the full inventory · ${(collections.bytes/1048576).toFixed(1)} MiB scientific originals. Source footprints are not verified pixel coverage.`;
    if(!collections.regions)section.append(el("strong",kind==="hyperspectral"?"Dataset Required":"No local datasets"),el("p","Import compatible data or use permitted automatic preparation. No suitable source pixels or future coverage are assumed."));
    if(kind!=="hyperspectral"&&!access.configuration.preferences.onlineEnabled){const a=el("a","Enable Scientific Data Access");a.href="#settings/science?section=access";section.append(a);}
    function renderRegion(entry,holder){
const s=entry.source,m=s.manifest;const row=el("article");row.className="source-notice";holder.append(row);row.append(el("h4",m.name),el("p",`${m.provider} · Observed: ${m.observationUtc||"Unknown / composite"} · ${entry.filesAvailable?"Available":"Dataset Invalid"} · ${"Mission-area suitability: use View Coverage or current Planner readiness"}`));if(entry.error)row.append(el("p",entry.error));
      const details=el("details");details.append(el("summary","View Dataset Details"),el("p",`${m.license} · ${m.citation} · ${m.bands.length} bands · ${m.verticalDatum}`));for(const [id,g] of Object.entries(s.grids))details.append(el("p",`${id}: ${g.crs} · ${g.width}×${g.height} · ${g.pixelWidth}×${g.pixelHeight} native grid units`));details.append(el("p",m.notes||"Original file hashes and source metadata retained."));row.append(details);
      let map;

      const coverage=action("View Coverage",async()=>{if(map){map.fit();return;}const vertices=await api(`/api/science/sources/${s.id}/coverage`);const panel=el("div");row.append(panel);map=new ResearchMap(panel);maps.push(map);map.set({plan:{surveyAreas:[{name:m.name,vertices}]}});});row.append(coverage);
      const active=!(access.configuration.preferences.excludedSourceIds||[]).includes(s.id);row.append(action(active?"Exclude from active selection":"Use for active selection",async()=>{await api("/api/science/source-selection",{sourceId:s.id,enabled:!active});row.append(el("p",active?"Excluded from active selection. Original files and products retained.":"Included in source selection. Original files unchanged."));}));
      if(query.get("dataset")===s.id)coverage.click();

    }
    async function collectionPage(page){
      for(const group of page.items){
        const box=el("details");box.className="source-collection";box.append(el("summary",`${group.provider} — ${group.regions} prepared regions — ${(group.bytes/1048576).toFixed(1)} MiB`),el("p",`${group.product} · Individual original identities, dates, calibration and masks are preserved. Source footprints do not guarantee valid pixels.`));list.append(box);
        const regions=el("details"),holder=el("div"),range=el("p");regions.append(el("summary",`View Individual Regions (${group.regions}) / Manage Selection`),range,holder);let cursor=0,through=null,nextCursor=null,history=[],serial=0;
        const previous=action("Previous Regions",async()=>{cursor=history.pop()??0;await render();}),next=action("More Regions",async()=>{if(nextCursor==null)return;history.push(cursor);cursor=nextCursor;await render();});regions.append(previous,next);
        async function render(){const request=++serial;range.textContent='Reading a bounded region page…';const params=new URLSearchParams({collection:group.id,limit:20,after:cursor});if(through!=null)params.set('through',through);const result=await api('/api/science/sources/page?'+params);if(own!==generation||request!==serial||!box.isConnected)return;through=result.through;nextCursor=result.next;holder.replaceChildren();range.textContent=`${result.items.length} regions displayed · ${result.total} in the complete collection`;previous.disabled=history.length===0;next.disabled=nextCursor==null;for(const entry of result.items)renderRegion(entry,holder);}
        let collectionMap;box.append(action("View Collection Coverage",async()=>{if(collectionMap){collectionMap.fit();return;}const page=await api('/api/science/sources/page?'+new URLSearchParams({collection:group.id,limit:20}));const holder=el('div');box.append(el('p',`${page.items.length} of ${page.total} retained footprints shown; inspect any region using the paginated list. No pixel-quality coverage is implied.`),holder);collectionMap=new ResearchMap(holder,null,{coverageOnly:true});maps.push(collectionMap);const areas=[];for(const entry of page.items){const vertices=await api(`/api/science/sources/${entry.source.id}/coverage`);if(own!==generation||!holder.isConnected)return;areas.push({name:entry.source.manifest.name,vertices});}collectionMap.set({plan:{surveyAreas:areas}});collectionMap.fit();}));
        regions.addEventListener('toggle',()=>{if(regions.open&&!holder.children.length)render().catch(error=>range.textContent=error.message);});box.append(regions);
      }
      if(page.next!=null)list.append(action('More Collections',async()=>{const next=await api('/api/science/source-collections?'+new URLSearchParams({kind,after:page.next}));if(own===generation&&list.isConnected)await collectionPage(next);}));
    }
    await collectionPage(collections);
    section.append(action("Manage Datasets",async()=>{list.hidden=!list.hidden;}));
  }
  checked.textContent="Dataset status checked: "+new Date().toLocaleTimeString()+". Counts describe the complete retained inventory; displayed regions are paged. View Coverage and Planner readiness assess mission geography.";
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
    if(query.get("source")){document.querySelector("#source-"+query.get("source"))?.scrollIntoView({block:"start"});if(query.get("import")==="1")location.hash="#library/download?science="+query.get("source");}
  }
  window.addEventListener("hashchange",applyLocation);applyLocation();
}
