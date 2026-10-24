import {node as el,button,bytes,dateTime,numeric,table,disclosure,status} from './workstation-ui.js';
import {dialogHeader,requestDialogClose} from './dialog-ui.js';
import {ResearchMap} from './map.js';

const caveat='EMIT development validation: application workflows are tested with synthetic originals. Genuine NASA original numerical acceptance remains pending. Historical orbital measurements are not live aircraft observations.';
function modal(title){const d=el('dialog',undefined,'research-panel hyperspectral-wizard');d.setAttribute('aria-label',title);document.body.append(d);dialogHeader(d,{title});d.addEventListener('close',()=>d.remove());d.showModal();return d;}
export function openEmitImport(api){
 const existing=document.querySelector('#emit-import');if(existing){existing.focus();return;}
 const d=modal('Import NASA EMIT Originals');d.id='emit-import';d.append(el('p',caveat));
 const body=el('div'),message=status(d,'Reading current verification…');d.append(body);let stopped=false,timer,key;
 d.addEventListener('close',()=>{stopped=true;clearTimeout(timer);});
 async function refresh(){
  try{const s=await api('/api/science/emit/import');if(stopped)return;message.textContent=s.message;
   const next=JSON.stringify(s);if(key!==next){key=next;body.replaceChildren();
    if(s.review){const r=s.review;body.append(el('h3','Review exact originals'),el('p',`${r.definition.acquisition} · ${dateTime(r.definition.startUtc)} · ${bytes(r.originalBytes)} original bytes · RFL002 / uncertainty002 / MASK003 · ${r.definition.bands} channels · ${r.definition.rows} × ${r.definition.columns} native samples.`));const files=el('ul');for(const name of r.fileNames)files.append(el('li',name));body.append(files,el('p',r.message));}
    if(s.stage==='Review'){
     const label=el('label','I understand genuine-original compatibility remains under validation. '),ack=el('input');ack.type='checkbox';label.prepend(ack);body.append(label);
     const approve=button(body,'Import Verified EMIT Source',async()=>{await api('/api/science/emit/import',{id:s.review.id,selectionHash:s.review.selectionHash,validationAcknowledged:ack.checked});key=null;await refresh();});approve.disabled=true;ack.onchange=()=>approve.disabled=!ack.checked;
    }
    if(!['Review','Importing','Verifying'].includes(s.stage))button(body,'Choose RFL, Uncertainty & Mask Files on This PC',()=>api('/api/desktop/emit-originals',{}));
    if(s.stage==='Ready'){button(body,'Explore Spectrum / Map',()=>{requestDialogClose(d);openEmitSource(api,s.sourceId);});window.dispatchEvent(new CustomEvent('aeroscience-sources-changed'));}
    if(['Review','Importing','Verifying','Failed'].includes(s.stage))button(body,s.stage==='Importing'?'Cancel Import':'Clear Review',async()=>{await api('/api/science/emit/import/cancel',{});key=null;await refresh();});
   }
  }catch(e){message.textContent=e.message+' Choose local files on the companion PC. Paired browsers can explore already retained sources.';}
  if(!stopped){clearTimeout(timer);timer=setTimeout(refresh,1500);}
 }
 refresh();return d;
}
export async function openEmitSource(api,id){
 const d=modal('NASA EMIT Scientific Exploration'),intro=status(d,'Verifying retained originals and product definition…');d.append(el('p',caveat));
 let stopped=false,chart,map,epoch=0,layer;const controller=new AbortController();const read=(path,body)=>api(path,body,{signal:controller.signal});d.addEventListener('input',()=>epoch++);d.addEventListener('close',()=>{stopped=true;epoch++;controller.abort();chart?.destroy();map?.destroy();});
 let definition;try{definition=await read(`/api/science/sources/${id}/emit`);}catch(e){intro.textContent=e.message;return;}if(stopped)return;
 const def=definition;intro.textContent=`${def.acquisition} · ${dateTime(def.startUtc)} · NASA / LP DAAC · ${def.bands} channels · unitless reflectance. Native ${def.rows} × ${def.columns}; GLT display ${def.gridRows} × ${def.gridColumns}. ${def.acceptance}`;
 const details=disclosure(d,'Scientific Details');details.append(el('p',`${def.decoderPolicy} · ${def.spatialAlignment}`),el('p','GLT extent is a candidate envelope, never proof of QA-valid pixels. All reads recheck original hashes. No source-derived cache is retained.'));
 table(details,'Original source provenance',['Role','Product version','Original file','Bytes','SHA-256'],def.sources.map(s=>[s.role,s.version,s.originalFileName,bytes(s.bytes),s.sha256]));
 const controls=el('div',undefined,'workspace-controls');d.append(controls);
 function input(label,value,min,max){const wrap=el('label',label),n=el('input');n.type='number';n.value=value;n.min=min;n.max=max;n.step=1;n.setAttribute('aria-label',label);wrap.append(n);controls.append(wrap);return n;}
 const row=input('Native row (zero-based)',0,0,def.rows-1),column=input('Native column (zero-based)',0,0,def.columns-1),band=input('Map spectral channel (zero-based)',0,0,284);
 const qa=el('label','Exclude water '),water=el('input');water.type='checkbox';qa.prepend(water);controls.append(qa);
 const im=el('label','Exclude interpolated bands '),interpolated=el('input');interpolated.type='checkbox';interpolated.checked=true;im.prepend(interpolated);controls.append(im);
 const rawLabel=el('label','Show original decoded values (including excluded; not QA validated) '),raw=el('input');raw.type='checkbox';rawLabel.prepend(raw);controls.append(rawLabel);
 const report=status(d,'Select a native sample or inspect a mapped GLT cell. Unknown/excluded values remain visible in the accessible table.');
 const chartBox=el('div',undefined,'emit-chart'),canvas=el('canvas');canvas.setAttribute('role','img');canvas.setAttribute('aria-label','EMIT reflectance and uncertainty by wavelength');chartBox.append(canvas);d.append(chartBox);
 const samples=el('div');d.append(samples);
 const query=()=>({row:Number(row.value),column:Number(column.value),firstBand:0,bandCount:285,excludeWater:water.checked,excludeInterpolatedBands:interpolated.checked});
 async function sample(){const own=++epoch;report.textContent='Reading a bounded spectrum; source integrity is checked in the isolated worker…';const result=await read(`/api/science/sources/${id}/emit/spectrum`,query());if(stopped||own!==epoch)return;const p=result.pixels[0];
  report.textContent=`Native (${p.row}, ${p.column}) · ${p.latitude==null?'Unlocated':numeric(p.latitude,7)+', '+numeric(p.longitude,7)} · ${p.state}. Original values and usable values are separate. ${result.support}`;
  chart?.destroy();if(window.Chart)chart=new window.Chart(canvas,{type:'line',data:{labels:def.wavelengthNm,datasets:[...(raw.checked?[{label:'Original decoded, not QA validated',data:p.reflectance.map((y,i)=>({x:def.wavelengthNm[i],y})),borderColor:'#ecb566',borderDash:[5,5],pointRadius:0,spanGaps:false}]:[]),{label:'QA-usable reflectance',data:p.usableReflectance.map((y,i)=>({x:def.wavelengthNm[i],y})),borderColor:'#4fbbff',pointRadius:0,spanGaps:false},{label:'Usable + uncertainty',data:p.usableReflectance.map((v,i)=>({x:def.wavelengthNm[i],y:v==null||p.usableUncertainty[i]==null?null:v+p.usableUncertainty[i]})),borderColor:'#9ca7c4',pointRadius:0,spanGaps:false},{label:'Usable − uncertainty',data:p.usableReflectance.map((v,i)=>({x:def.wavelengthNm[i],y:v==null||p.usableUncertainty[i]==null?null:v-p.usableUncertainty[i]})),borderColor:'#9ca7c4',pointRadius:0,spanGaps:false}]},options:{animation:false,responsive:true,maintainAspectRatio:false,scales:{x:{type:'linear',title:{display:true,text:'Wavelength (nm)'},ticks:{maxTicksLimit:8}},y:{title:{display:true,text:'Estimated surface reflectance (unitless)'}}}}});
  samples.replaceChildren();table(samples,'EMIT spectral measurements',['Channel','Wavelength (nm)','FWHM (nm)','Original decoded reflectance','Uncertainty','QA-usable reflectance','State'],def.wavelengthNm.map((w,i)=>[i,numeric(w,4),numeric(def.fwhmNm[i],4),numeric(p.reflectance[i],7),numeric(p.uncertainty[i],7),numeric(p.usableReflectance[i],7),p.bandStates[i]]));
 }
 button(controls,'Explore Spectrum',sample);
 button(controls,'Export Data & Provenance',async()=>{const result=await read(`/api/science/sources/${id}/emit/spectrum`,query());const blob=new Blob([JSON.stringify({formatVersion:1,kind:'Decoded historical EMIT sample, not an original NASA file or flight observation',definition:def,result},null,2)],{type:'application/json'});const url=URL.createObjectURL(blob),a=el('a');a.href=url;a.download='AeroScience-EMIT-sample.json';a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);});
 const compare=disclosure(d,'Compare / Analyze — same-scene samples');
 const secondRow=input('Comparison native row',0,0,def.rows-1),secondColumn=input('Comparison native column',1,0,def.columns-1);compare.append(secondRow.parentElement,secondColumn.parentElement);
 const comparison=el('div');compare.append(comparison);button(compare,'Compare Measurements',async()=>{const first=query(),second={...first,row:Number(secondRow.value),column:Number(secondColumn.value)};const result=await read(`/api/science/sources/${id}/emit/compare`,{first,second});if(stopped)return;comparison.replaceChildren(el('p',result.method));table(comparison,'Same-scene reflectance comparison',['Wavelength (nm)','First − second (QA usable)'],result.wavelengthNm.map((w,i)=>[numeric(w,4),numeric(result.difference[i],7)]));});
 const mapSection=disclosure(d,'View on Map — bounded QA-aware scientific window');
 mapSection.append(el('p','Only requested decoded GLT cells are drawn. Other areas are not assessed. Green/blue cells have supported values; text status labels distinguish excluded, missing or unlocated cells. Selecting a cell inspects its native spectrum. No complete-scene coverage is claimed.'));
 const gridRow=input('GLT row (zero-based)',0,0,def.gridRows-1),gridColumn=input('GLT column (zero-based)',0,0,def.gridColumns-1),height=input('Map window rows',1,1,8),width=input('Map window columns',Math.min(4,def.gridColumns),1,8);mapSection.append(gridRow.parentElement,gridColumn.parentElement,height.parentElement,width.parentElement);
 const legend=status(mapSection,'Map not assessed.'),holder=el('div'),mapRows=el('div');mapSection.append(holder,mapRows);
 async function draw(){const own=++epoch;const q={row:Number(gridRow.value),column:Number(gridColumn.value),rows:Number(height.value),columns:Number(width.value),band:Number(band.value),excludeWater:water.checked,excludeInterpolatedBands:interpolated.checked};const r=await read(`/api/science/sources/${id}/emit/map`,q);if(stopped||own!==epoch)return;
  if(!map){map=new ResearchMap(holder,null,{coverageOnly:true});const b=def.derivedGltDomain;map.set({plan:{surveyAreas:[{name:'GLT candidate domain — not QA coverage',vertices:[{latitude:b.south,longitude:b.west},{latitude:b.south,longitude:b.east},{latitude:b.north,longitude:b.east},{latitude:b.north,longitude:b.west}]}]}});map.fit();layer=window.L.layerGroup().addTo(map.map);}layer.clearLayers();
  const values=r.cells.map(c=>c.spectrum?.usableReflectance[0]).filter(v=>Number.isFinite(v)),low=values.length?Math.min(...values):null,high=values.length?Math.max(...values):null;
  legend.textContent=`Channel ${q.band} · ${numeric(def.wavelengthNm[q.band],4)} nm · unitless reflectance · ${values.length}/${r.cells.length} requested cells usable · scale ${numeric(low,6)} to ${numeric(high,6)}. Grey = missing/unlocated; orange = QA excluded/unknown. This is a derived GLT display, not an affine original raster.`;
  const gt=def.gridTransform;
  for(const c of r.cells){const v=c.spectrum?.usableReflectance[0],valid=Number.isFinite(v),t=valid&&high>low?(v-low)/(high-low):.5,color=valid?`hsl(${220-t*120} 75% 45%)`:c.spectrum?'#c97c23':'#888';const lat=c.gridLatitude,lon=c.gridLongitude;window.L.rectangle([[lat+gt[5]/2,lon-gt[1]/2],[lat-gt[5]/2,lon+gt[1]/2]],{color,fillColor:color,fillOpacity:valid?.65:.15,weight:1}).bindTooltip(el('span',`${c.spectrum?.state||c.state} · ${valid?numeric(v,7):'Unavailable'} · native ${c.sourceRow??'—'},${c.sourceColumn??'—'}`)).on('click',()=>{if(c.sourceRow!=null&&c.sourceColumn!=null){row.value=c.sourceRow;column.value=c.sourceColumn;sample();}}).addTo(layer);}
  mapRows.replaceChildren();table(mapRows,'EMIT geographic samples',['GLT row','GLT column','Native row','Native column','Reflectance','State'],r.cells.map(c=>[c.gridRow,c.gridColumn,c.sourceRow??'—',c.sourceColumn??'—',numeric(c.spectrum?.usableReflectance[0],7),c.spectrum?.state||c.state]),{onSelect:i=>{const c=r.cells[i];if(c.sourceRow!=null){row.value=c.sourceRow;column.value=c.sourceColumn;sample();}}});
 }
 button(mapSection,'Render Scientific Window',draw);
 return d;
}
