import { ResearchMap } from "./map.js";
const el=(tag,text)=>{const n=document.createElement(tag);if(text!==undefined)n.textContent=text;return n;};
const help="#help?guide=remote-sensing/using-your-own-hyperspectral-dataset.md";
function link(parent,label,url){const a=el("a",label);a.href=url;a.target="_blank";a.rel="noopener";parent.append(a,document.createTextNode(" · "));}
function button(label,run){const b=el("button",label);b.type="button";b.className="secondary";b.onclick=async()=>{b.disabled=true;try{await run();}catch(error){alert(error.message);}finally{b.disabled=false;}};return b;}
const mib=n=>`${(n/1048576).toFixed(1)} MiB`;
export function openHyperspectralImport(api,kind="hyperspectral"){
  const existing=document.querySelector("#hyperspectral-import");if(existing){existing.focus();return;}
  const dialog=el("dialog");dialog.id="hyperspectral-import";dialog.className="research-panel hyperspectral-wizard";dialog.setAttribute("aria-label","Import Hyperspectral Dataset");
  const heading=el("h2",kind==="hyperspectral"?"Import Hyperspectral Dataset":`Import ${kind} Dataset`),steps=el("p","1. Choose files → 2. Verify dataset → 3. Import → 4. Use in a mission"),body=el("div"),message=el("p");
  dialog.append(heading,steps);link(dialog,"How to Find and Prepare a Dataset",help);dialog.append(body,message);
  let closed=false,timer,map,lastKey,confirming=false,announced=null;
  function cleanup(){closed=true;clearTimeout(timer);map?.destroy();dialog.remove();}
  const closeWizard=button("Close wizard",async()=>{const s=await api("/api/science/hyperspectral/import");if(s.busy&&!confirm("Cancel this import/inspection and close? Original files remain untouched."))return;if(s.busy)await api("/api/science/hyperspectral/cancel",{});cleanup();});dialog.append(closeWizard);
  dialog.addEventListener("cancel",event=>{event.preventDefault();closeWizard.click();});
  document.body.append(dialog);dialog.showModal();
  function choose(){if(kind!=="hyperspectral"){body.append(el("h3","Step 1 — Choose Your Files"),el("p",kind==="terrain"?"Select a single-band numeric elevation GeoTIFF.":"Select one original scene’s compatible numeric GeoTIFF bands and original metadata. Rendered images and JP2 are unsupported."),button(kind==="terrain"?"Choose elevation GeoTIFF…":"Choose collection folder…",()=>api(`/api/desktop/${kind}-import`,{})));return;}body.append(el("h3","Step 1 — Choose Your Files"),el("p","Keep the original ENVI .hdr and its binary together in one folder. AeroScience reads the metadata; you do not need JSON or a list of wavelengths."),button("Choose ENVI header (.hdr)…",()=>api("/api/desktop/hyperspectral-header",{})),button("Choose dataset folder…",()=>api("/api/desktop/hyperspectral-folder",{})));}
  async function draw(s){
    if(s.inspection&&s.inspection.kind!==kind&&!s.busy){await api("/api/science/hyperspectral/cancel",{});return;}
    if(s.stage==="Ready"&&s.source?.id!==announced){announced=s.source.id;window.dispatchEvent(new CustomEvent("aeroscience-sources-changed"));}
    const key=JSON.stringify([s.stage,s.inspection,s.source?.id,confirming,s.nasaSelection?.scene]);message.textContent=s.message;
    if(key===lastKey)return;lastKey=key;map?.destroy();map=null;body.replaceChildren();
    steps.textContent=`${s.stage==="Choose"||s.stage==="Invalid"?"●":"✓"} 1. Choose files → ${s.stage==="Verify"?"●":""} 2. Verify dataset → ${confirming||s.stage==="Importing"?"●":""} 3. Import → ${s.stage==="Ready"?"●":""} 4. Use in a mission`;
    if(s.nasaSelection){const n=s.nasaSelection.nasa;body.append(el("p",`NASA ${n.family} · ${n.flightline} · ${n.granuleId}. Official browser download is external; this wizard verifies local originals. Only matching reflectance ENVI files are accepted. EMIT import is unavailable.`));}
    if(s.stage==="Choose"||s.stage==="Invalid"||s.stage==="Cancelled"){choose();return;}
    if(s.stage==="Verifying"){body.append(el("p","Reading the header and bounded file metadata. The entire cube is not loaded into memory."));return;}
    const i=s.inspection;if(!i)return;kind=i.kind||kind;
    body.append(el("h3",s.stage==="Ready"?"Step 4 — Dataset Ready":confirming?"Step 3 — Confirm Import":"Step 2 — Verify the Dataset"),el("p",`Header: ${i.header}`),el("p",`Binary: ${i.binary||"Select the matching binary below"}`));
    if(!i.binary){const options=el("select");options.setAttribute("aria-label","Associated ENVI binary");for(const name of i.candidates){const o=el("option",name);o.value=name;options.append(o);}if(i.candidates.length)body.append(options,button("Use selected binary",()=>api("/api/science/hyperspectral/associate",{fileName:options.value})));else body.append(button("Choose another header",async()=>{await api("/api/science/hyperspectral/cancel",{});}));return;}
    body.append(el("p",`${i.bandCount} bands${i.minimumWavelengthNm!=null?` · ${i.minimumWavelengthNm.toFixed(2)}–${i.maximumWavelengthNm.toFixed(2)} nm`:""} · ${i.unit||"Original scientific units needed"} · ${i.badBands} bad bands excluded`),el("p",`Source acquisition: ${i.observationUtc||"Unknown; not guessed"} · ${i.grid?.crs} · ${i.grid?.width} × ${i.grid?.height} pixels · Native resolution: ${i.grid?Math.abs(i.grid.pixelWidth):"?"} × ${i.grid?Math.abs(i.grid.pixelHeight):"?"} ${i.grid?.crs==="EPSG:4326"?"degrees":"m"}`),el("p",`Original files: ${mib(i.fileBytes)} · Estimated import: ${mib(i.estimatedImportBytes)} · Destination: ${s.destination} · Free: ${s.freeBytes==null?"Not reported":mib(s.freeBytes)}`));
    for(const warning of i.warnings)body.append(el("p",warning));
    let coverageDetails;
    function showCoverage(){
      if(!i.coverage?.length)return;
      if(!coverageDetails){coverageDetails=el("details");coverageDetails.append(el("summary","View geographic coverage on map"));const panel=el("div");panel.className="hyperspectral-coverage";coverageDetails.append(panel,el("p","Geographic envelope only; original quality and nodata limits remain. Historical external data, not a live aircraft camera."));body.append(coverageDetails);coverageDetails.addEventListener("toggle",()=>{if(coverageDetails.open&&!map){map=new ResearchMap(panel,null,{coverageOnly:true});map.set({plan:{surveyAreas:[{name:"Dataset coverage — per-pixel quality applies",vertices:i.coverage}]}});map.fit();}});}
      return coverageDetails;
    }
    if(s.stage==="Verify"&&!confirming)showCoverage();

    if(s.stage==="Ready"&&kind!=="hyperspectral"){body.append(el("p","Use this retained dataset offline within its geographic coverage. Manage source modes in Scientific Data Sources."),button("Import Another Dataset",()=>api("/api/science/hyperspectral/cancel",{})));return;}
    if(s.stage==="Ready"){
      body.append(el("p","Available wavelengths and retained provenance are listed in Settings → Scientific Data Sources. During a recording, View Data displays genuine sampled spectra and JSON/CSV export."),button("View Coverage on Map",()=>{const details=showCoverage();if(details){details.open=true;map?.fit();details.scrollIntoView({block:"nearest"});}}),button("Enable Hyperspectral Imager",async()=>{const configs=await api("/api/instruments/configuration");const enabled=configs.map(c=>({...c,enabled:c.instrumentId==="hyperspectral-imager"?true:c.enabled}));if(!enabled.some(c=>c.instrumentId==="hyperspectral-imager"))enabled.push({instrumentId:"hyperspectral-imager",enabled:true,intervalMs:1000});await api("/api/instruments",enabled);message.textContent="Hyperspectral Imager enabled. Fly inside this dataset's real coverage and start a mission to acquire spectra.";}),button("View Dataset Details",()=>{const pre=el("pre",JSON.stringify(s.source.manifest,null,2));body.append(pre);}),button("Import Another Dataset",()=>api("/api/science/hyperspectral/cancel",{})));return;
    }
    if(s.busy){body.append(button("Cancel import",()=>api("/api/science/hyperspectral/cancel",{})));return;}
    if(confirming){body.append(el("p",`Import ${i.manifest.name}? Provider: ${i.manifest.provider}. Terms: ${i.manifest.license}. Originals remain untouched. The verified files and source metadata will be copied atomically; no account or network download is required.`),button("Import Dataset",async()=>{confirming=false;await api("/api/science/hyperspectral/import",{});}),button("Back to verification",async()=>{confirming=false;lastKey=null;}),button("Cancel",()=>api("/api/science/hyperspectral/cancel",{})));return;}
    let user={};try{user=JSON.parse(i.manifest?.notes||"{}").userEntered||{};}catch{}
    if(s.nasaSelection){if(i.manifest)body.append(button("Continue to Import",async()=>{confirming=true;lastKey=null;}));body.append(button("Cancel",()=>api("/api/science/hyperspectral/cancel",{})));return;}
    const details=el("details");details.append(el("summary","Optional source information / original units"));const form=el("form");const inputs={};
    for(const [key,label,type] of [["name","Friendly dataset name","text"],["provider","Source organization (if known)","text"],["source","Original source link or identifier","text"],["observationUtc","Original acquisition date (if known)","date"],["license","License / usage terms (if known)","text"],["notes","Research notes","text"],["sceneId","Original scene identifier","text"],["verticalDatum","Elevation vertical datum (or explicitly Unknown)","text"],["scale","Original numeric scale factor","number"],["offset","Original numeric additive offset","number"]]){if(kind==="hyperspectral"&&["sceneId","verticalDatum","scale","offset"].includes(key))continue;const l=el("label",label+" "),n=el("input");n.type=type;n.value=user[key]??"";if(type==="date")n.value=n.value.slice(0,10);n.setAttribute("aria-label",label);l.append(n);form.append(l,el("br"));inputs[key]=n;}
    const units=el("select");units.setAttribute("aria-label","Original scientific units");for(const value of ["","m","reflectance","radiance W/(m2 sr um)","radiance W/(m2 sr nm)","radiance mW/(cm2 sr um)","radiance uW/(cm2 sr nm)"]){const o=el("option",value||"Choose only from original documentation");o.value=value;o.selected=value===i.unit;units.append(o);}form.append(el("label","Original scientific units "),units,button("Apply source information",async()=>{const value=Object.fromEntries(Object.entries(inputs).map(([key,n])=>[key,n.value||null]));if(value.observationUtc){value.observationUtc+="T00:00:00Z";value.notes=(value.notes?value.notes+"; ":"")+"User-entered acquisition date has day precision; exact UTC observation time was not supplied.";}for(const key of ["scale","offset"])if(value[key]!=null)value[key]=Number(value[key]);value.unit=units.value||null;await api("/api/science/hyperspectral/metadata",value);}));details.append(form,el("p","User-entered metadata is retained separately from fields read from the original header. Do not guess units or dates."));details.open=!i.manifest;body.append(details);
    if(i.manifest)body.append(button("Continue to Import",async()=>{confirming=true;lastKey=null;}));body.append(button("Cancel",()=>api("/api/science/hyperspectral/cancel",{})));
  }
  async function refresh(){if(closed)return;try{const s=await api("/api/science/hyperspectral/import");if(!closed)await draw(s);}catch(error){message.textContent=error.message+" Open this wizard on the companion PC's dashboard.";if(!body.children.length)choose();}if(!closed)timer=setTimeout(refresh,1000);}
  refresh();
  return dialog;
}
export async function hyperspectralCard(parent,api,point=null){
  const card=el("article");card.className="panel hyperspectral-status";card.append(el("h3","Hyperspectral Imager · User-supplied external data"));parent.append(card);
  const query=point?`?latitude=${point.latitude}&longitude=${point.longitude}`:"";
  const sources=(await api("/api/science/sources"+query)).filter(s=>s.source.manifest.kind==="hyperspectral");
  if(!card.isConnected)return card;
  const active=(await api("/api/state")).research?.instruments?.find(i=>i.instrumentId==="hyperspectral-imager");
  const ready=sources.find(s=>s.filesAvailable&&(!point||s.coversPoint));
  const status=ready?(active?.enabled&&["unavailable","acquisitionGap","invalid"].includes(active.quality)?"Acquisition Unavailable":"Dataset Ready"):sources.length===0?"Dataset Required":sources.every(s=>!s.filesAvailable)?"Dataset Invalid":"Dataset Outside Coverage";
  card.append(el("h4",status),el("p",ready?`Historical source: ${ready.source.manifest.name}. Ready only inside its actual geographic coverage, subject to pixels, quality masks and current acquisition conditions.`:"To use this instrument, import a compatible hyperspectral dataset covering your research area. No account or automatic download is required."),button("Import Hyperspectral Dataset",()=>openHyperspectralImport(api)));
  link(card,"How to Find and Prepare a Dataset",help);link(card,"View supported formats",help+"#supported-files");link(card,"Manage imported datasets","/#settings");
  if(ready)card.append(button("View geographic coverage",async()=>{const coverage=await api(`/api/science/sources/${ready.source.id}/coverage`);const panel=el("div");card.append(panel);const map=new ResearchMap(panel);map.set({plan:{surveyAreas:[{name:ready.source.manifest.name,vertices:coverage}]}});map.fit();}));return card;
}
