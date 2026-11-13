// Bounded display of actual retained masks. No resampling, source retrieval or science writes.
import {ResearchMap} from './map.js';
import {node as el,button,color,numeric} from './workstation-ui.js';
export function contains(position,geometry){
 const ringContains=ring=>{let inside=false;for(let i=0,j=ring.length-1;i<ring.length;j=i++){const a=ring[i],b=ring[j];if((a[1]>position.latitude)!==(b[1]>position.latitude)&&position.longitude<(b[0]-a[0])*(position.latitude-a[1])/(b[1]-a[1])+a[0])inside=!inside;}return inside;};
 return geometry?.coordinates.some(p=>ringContains(p[0])&&!p.slice(1).some(ringContains))||false;
}
export function elevation(value,unit,reading){return ['m','ft'].includes(unit)?reading('elevation',value==null?null:value*(unit==='ft'?.3048:1)):{value,unit:unit||'unknown',decimals:3};}
export function surveyMap(parent,onInspect){
 const L=window.L,base=new ResearchMap(parent,()=>{},{coverageOnly:true}),map=base.map;
 base.container.setAttribute('aria-label','Retained terrain survey map. Arrow keys pan; plus and minus zoom. Exact values are available in the evidence table.');
 const layers={corridor:L.layerGroup().addTo(map),track:L.layerGroup().addTo(map),originals:L.layerGroup().addTo(map),selection:L.layerGroup().addTo(map)};
 let values=[],options={},surface,frame,disposed=false,selected=null,intervals=[];
 const Layer=L.Layer.extend({onAdd(m){this.canvas=L.DomUtil.create('canvas','survey-canvas');this.canvas.setAttribute('aria-hidden','true');this.canvas.style.pointerEvents='none';m.getPane('overlayPane').append(this.canvas);m.on('moveend zoomend resize',redraw);redraw();},onRemove(m){m.off('moveend zoomend resize',redraw);cancelAnimationFrame(frame);this.canvas.remove();}});
 surface=new Layer().addTo(map);
 function redraw(){cancelAnimationFrame(frame);if(disposed)return;frame=requestAnimationFrame(draw);}
 function draw(){
  if(disposed||!surface.canvas)return;const c=surface.canvas,size=map.getSize(),dpr=Math.min(2,devicePixelRatio||1,Math.sqrt(2097152/Math.max(1,size.x*size.y))),top=map.containerPointToLayerPoint([0,0]);L.DomUtil.setPosition(c,top);c.style.width=size.x+'px';c.style.height=size.y+'px';c.width=Math.max(1,Math.floor(size.x*dpr));c.height=Math.max(1,Math.floor(size.y*dpr));const ctx=c.getContext('2d');ctx.scale(dpr,dpr);ctx.globalAlpha=options.opacity??.85;
  const valid=values.filter(v=>v.value!=null&&v.quality==='valid'),min=valid.length?Math.min(...valid.map(v=>v.value)):0,max=valid.length?Math.max(...valid.map(v=>v.value)):1;
  let overview=0;const occupied=new Set();
  for(const v of values){if(!v.geometry||!options.terrain)continue;const missing=v.value==null||v.quality!=='valid';if(missing&&!options.gaps)continue;let left=Infinity,right=-Infinity,top=Infinity,bottom=-Infinity;ctx.beginPath();for(const polygon of v.geometry.coordinates)for(const ring of polygon){ring.forEach((p,i)=>{const q=map.latLngToContainerPoint([p[1],p[0]]);left=Math.min(left,q.x);right=Math.max(right,q.x);top=Math.min(top,q.y);bottom=Math.max(bottom,q.y);if(i)ctx.lineTo(q.x,q.y);else ctx.moveTo(q.x,q.y);});ctx.closePath();}ctx.fillStyle=missing?'#d96841':color(v.value,min,max);if(!missing&&right-left<2&&bottom-top<2){const p=map.latLngToContainerPoint([v.position.latitude,v.position.longitude]),key=Math.floor(p.x/4)+','+Math.floor(p.y/4);if(!occupied.has(key)){occupied.add(key);ctx.beginPath();ctx.arc(p.x,p.y,2,0,Math.PI*2);ctx.strokeStyle=ctx.fillStyle;ctx.lineWidth=1;ctx.stroke();overview++;}}else if(missing){ctx.strokeStyle='#d96841';ctx.lineWidth=1;ctx.stroke();}else ctx.fill('evenodd');}
  // Point rendering is installed by the real LiDAR viewer checkpoint.
  parent.dataset.displayedCells=values.filter(v=>v.geometry&&v.value!=null).length;parent.dataset.canvasPixels=c.width*c.height;parent.dataset.representation=overview?'native-masks-and-location-overview':'native-cell-masks';
 }
 const click=e=>{const p={latitude:e.latlng.lat,longitude:e.latlng.lng};const hit=values.find(v=>v.geometry&&contains(p,v.geometry));if(hit)onInspect(hit);};map.on('click',click);
 const tools=el('div',undefined,'action-row');parent.prepend(tools);button(tools,'Fit retained survey',()=>fit());
 const bounds=[];
 function fit(){const b=bounds.length?bounds:values.map(v=>[v.position.latitude,v.position.longitude]);if(b.length)map.fitBounds(L.latLngBounds(b),{padding:[24,24],maxZoom:18,animate:false});}
 return {base,map,layers,
  update(next,preferences){values=next;options=preferences;redraw();},
  geometry(records,session){layers.corridor.clearLayers();bounds.length=0;intervals=records.filter(r=>r.chunk).map(r=>r.chunk);for(const r of records){const chunk=r.chunk;if(!chunk||session&&chunk.sessionId!==session)continue;const g=L.geoJSON({type:'Feature',geometry:chunk.geometry,properties:{}},{style:{color:'#9a73d8',weight:2,fill:false,dashArray:'6 4'}});g.eachLayer(l=>{for(const p of chunk.geometry.coordinates.flatMap(p=>p[0]))bounds.push([p[1],p[0]]);});g.addTo(layers.corridor);}fit();},
  track(points){layers.track.clearLayers();let segment=[],previousSession;const flush=()=>{if(segment.length>1)L.polyline(segment,{color:'#297aaf',weight:2,dashArray:'3 5'}).addTo(layers.track);segment=[];};for(const p of points){const session=intervals.find(c=>p.utc>=c.fromUtc&&p.utc<=c.toUtc)?.sessionId;if(p.breakBefore||p.paused||session!==previousSession)flush();if(!p.paused)segment.push([p.position.latitude,p.position.longitude]);previousSession=session;}flush();},
  originals(products){layers.originals.clearLayers();for(const p of products){const ring=p.footprint?.boundary;if(ring?.length)L.polygon(ring.map(v=>[v.latitude,v.longitude]),{color:'#b07300',fill:false,weight:1}).addTo(layers.originals).bindTooltip(el('span',`${p.instrumentId} · ${p.flightPacketUtc} · modeled footprint of original acquisition ${p.id}`));}},
  show(name,show){if(layers[name]){if(show)layers[name].addTo(map);else map.removeLayer(layers[name]);}redraw();},
  select(value){selected=value;layers.selection.clearLayers();if(value){L.circleMarker([value.position.latitude,value.position.longitude],{radius:7,color:'#d95825',fill:false,weight:3}).addTo(layers.selection);}},
  fit,redraw,destroy(){disposed=true;values=[];selected=null;cancelAnimationFrame(frame);map.off('click',click);base.destroy();}
 };
}
