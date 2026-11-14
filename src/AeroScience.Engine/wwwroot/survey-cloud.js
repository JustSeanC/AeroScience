import {setPresentationText} from './i18n.js';
import {node as el,button,select,status,numeric} from './workstation-ui.js';
import {t as textForUI,t} from './i18n.js';
import {pointColor,pointRange,representatives,filteredPoints} from './survey-points.js';
const text=key=>t('results.'+key);
// WebGL 1 point primitives; bounded CPU projection enables exact-record picking
// and view-dependent LOD. No browser scientific reprojection or output product.
export function cloudView(parent,onInspect){
 const root=el('section',undefined,'survey-cloud');setPresentationText(root,text('cloudTitle'),"aria-label");parent.append(root);
 const controls=el('div',undefined,'action-row'),canvas=el('canvas'),feedback=status(root,text('gpuChecking')),description=el('p');root.prepend(controls,canvas,description);canvas.tabIndex=0;canvas.setAttribute('role','img');setPresentationText(canvas,text('cloudKeys'),"aria-label");
 let values=[],source,options={},origin,positions=[],selected=[],gl,program,buffer,frame,disposed=false,lost=false,yaw=.55,pitch=.65,zoom=1,pan=[0,0],drag=null,moved=false,width=1,height=1,zScale=1;
 const scale=select(controls,text('exaggeration'),[['1','1×'],['2','2×'],['5','5×']],'1');scale.onchange=()=>{zScale=Number(scale.value);redraw();};
 button(controls,text('orbitLeft'),()=>{yaw-=.2;redraw();});button(controls,text('orbitRight'),()=>{yaw+=.2;redraw();});button(controls,text('zoomIn'),()=>{zoom=Math.min(20,zoom*1.3);redraw();});button(controls,text('zoomOut'),()=>{zoom=Math.max(.1,zoom/1.3);redraw();});button(controls,text('resetCloud'),()=>{yaw=.55;pitch=.65;zoom=1;pan=[0,0];redraw();});
 function unavailable(){lost=true;canvas.hidden=true;controls.hidden=true;setPresentationText(feedback,text('gpuFallback'));root.dataset.renderer='unavailable';}
 try{gl=canvas.getContext('webgl',{alpha:false,antialias:false,preserveDrawingBuffer:false,powerPreference:'low-power'});if(!gl)throw Error('WebGL unavailable');
  const shader=(type,source)=>{const s=gl.createShader(type);gl.shaderSource(s,source);gl.compileShader(s);if(!gl.getShaderParameter(s,gl.COMPILE_STATUS)){gl.deleteShader(s);throw Error(textForUI("ui.surveyCloud.messagePointShaderCompilationFailed"));}return s;};
  const vs=shader(gl.VERTEX_SHADER,'attribute vec3 position;attribute vec3 color;varying vec3 tint;void main(){gl_Position=vec4(position,1.0);gl_PointSize=3.0;tint=color;}');
  const fs=shader(gl.FRAGMENT_SHADER,'precision mediump float;varying vec3 tint;void main(){if(distance(gl_PointCoord,vec2(0.5))>0.5)discard;gl_FragColor=vec4(tint,1.0);}');
  program=gl.createProgram();gl.attachShader(program,vs);gl.attachShader(program,fs);gl.linkProgram(program);gl.deleteShader(vs);gl.deleteShader(fs);if(!gl.getProgramParameter(program,gl.LINK_STATUS))throw Error(textForUI("ui.surveyCloud.messagePointProgramLinkFailed"));buffer=gl.createBuffer();if(!buffer)throw Error(textForUI("ui.surveyCloud.messagePointBufferUnavailable"));root.dataset.renderer='webgl1';
 }catch{unavailable();}
 function prepare(){positions=[];if(!values.length||!source){origin=null;setPresentationText(description,'');delete root.dataset.localOrigin;return;}const p=values[0].point.original;origin={x:p.x,y:p.y,z:p.z,geographic:values[0].position,crs:source.horizontalCrs,zUnit:source.unit,verticalReference:source.verticalReference};
  let xy=0,z=0;const geographic=source.horizontalCrs==='EPSG:4326';
  for(const v of values){const p=v.point.original,dx=geographic?(p.x-origin.x)*Math.PI/180*6378137*Math.cos(origin.y*Math.PI/180):p.x-origin.x,dy=geographic?(p.y-origin.y)*Math.PI/180*6378137:p.y-origin.y,dz=(p.z-origin.z)*(source.unit==='ft'?.3048:1);positions.push([dx,dy,dz]);xy=Math.max(xy,Math.abs(dx),Math.abs(dy));z=Math.max(z,Math.abs(dz));}
  const low=[Infinity,Infinity,Infinity],high=[-Infinity,-Infinity,-Infinity];for(const p of positions)for(let i=0;i<3;i++){low[i]=Math.min(low[i],p[i]);high[i]=Math.max(high[i],p[i]);}
  const horizontal=Math.max(1,high[0]-low[0],high[1]-low[1]),zFactor=['m','ft'].includes(source.unit)?1:horizontal*.25/Math.max(1,high[2]-low[2]),target=low.map((v,i)=>(v+high[i])/2),radius=Math.max(1,Math.hypot((high[0]-low[0])/2,(high[1]-low[1])/2,(high[2]-low[2])*zFactor/2));
  positions=positions.map(p=>[(p[0]-target[0])*.8/radius,(p[1]-target[1])*.8/radius,(p[2]-target[2])*zFactor*.8/radius]);
  setPresentationText(description,`${text('localOrigin')} X=${origin.x}, Y=${origin.y}, Z=${origin.z} (${source.unit}). ${source.horizontalCrs}. ${source.verticalReference}. ${['m','ft'].includes(source.unit)?text('metricDisplay'):text('unknownAspect')} ${geographic?text('geographicDisplay'):text('nativeDisplay')}`);
  root.dataset.localOrigin=JSON.stringify(origin);
 }
 function project(p){const a=p[0]*Math.cos(yaw)-p[1]*Math.sin(yaw),b=p[0]*Math.sin(yaw)+p[1]*Math.cos(yaw),y=b*Math.sin(pitch)-p[2]*zScale*Math.cos(pitch),z=b*Math.cos(pitch)+p[2]*zScale*Math.sin(pitch),fit=Math.min(1,width/height);return [width/2+a*zoom*fit*height/2+pan[0]*width/2,height/2+y*zoom*fit*height/2+pan[1]*height/2,Math.max(-.99,Math.min(.99,z*.2))];}
 function redraw(){if(disposed||lost)return;cancelAnimationFrame(frame);frame=requestAnimationFrame(draw);}
 function draw(){if(disposed||lost||!gl)return;const bounds=canvas.getBoundingClientRect();width=Math.max(1,bounds.width);height=Math.max(1,bounds.height);const dpr=Math.min(2,devicePixelRatio||1,Math.sqrt(2097152/(width*height)));canvas.width=Math.floor(width*dpr);canvas.height=Math.floor(height*dpr);gl.viewport(0,0,canvas.width,canvas.height);gl.clearColor(.035,.065,.09,1);gl.clear(gl.COLOR_BUFFER_BIT|gl.DEPTH_BUFFER_BIT);gl.enable(gl.DEPTH_TEST);gl.useProgram(program);
  const all=options.historical===false?[]:filteredPoints(values,options),positionById=new Map(values.map((v,i)=>[v.id,positions[i]]));selected=representatives(all,v=>project(positionById.get(v.id)),{width,height,cell:3,depth:true,limit:65536});const [low,high]=pointRange(all,options.mode),data=new Float32Array(selected.length*6);
  selected.forEach((v,i)=>{const p=v.screen,c=pointColor(v.value,options.mode,low,high);const rgb=c.startsWith('rgb')?c.match(/[\d.]+/g).map(Number):[0,2,4].map(i=>parseInt(c.slice(1+i,3+i),16));data.set([p[0]/width*2-1,1-p[1]/height*2,p[2],...rgb.map(v=>v/255)],i*6);});
  gl.bindBuffer(gl.ARRAY_BUFFER,buffer);gl.bufferData(gl.ARRAY_BUFFER,data,gl.DYNAMIC_DRAW);for(const [name,offset]of [['position',0],['color',12]]){const a=gl.getAttribLocation(program,name);gl.enableVertexAttribArray(a);gl.vertexAttribPointer(a,3,gl.FLOAT,false,24,offset);}gl.drawArrays(gl.POINTS,0,selected.length);
  setPresentationText(feedback,`${selected.length} ${text('renderedPoints')} · ${all.length} ${text('filteredPoints')} · ${values.length} ${text('loaded')}. ${text('lod')}`);root.dataset.renderedPoints=selected.length;root.dataset.gpuBytes=data.byteLength;root.dataset.canvasPixels=canvas.width*canvas.height;
 }
 canvas.addEventListener('webglcontextlost',event=>{event.preventDefault();cancelAnimationFrame(frame);unavailable();});
 canvas.onpointerdown=e=>{drag=[e.clientX,e.clientY,yaw,pitch,...pan];moved=false;canvas.setPointerCapture(e.pointerId);};
 canvas.onpointermove=e=>{if(!drag)return;const x=e.clientX-drag[0],y=e.clientY-drag[1];if(Math.hypot(x,y)>4)moved=true;if(e.shiftKey){pan=[drag[4]+x/Math.max(width,1)*2,drag[5]+y/Math.max(height,1)*2];}else{yaw=drag[2]+x*.01;pitch=Math.max(.05,Math.min(Math.PI-.05,drag[3]+y*.01));}redraw();};
 canvas.onpointerup=e=>{if(!moved){const r=canvas.getBoundingClientRect(),x=e.clientX-r.left,y=e.clientY-r.top;let best,dist=12;for(const p of selected){const d=Math.hypot(x-p.screen[0],y-p.screen[1]);if(d<dist){dist=d;best=p.value;}}if(best)onInspect(best);}drag=null;};canvas.onpointercancel=()=>{drag=null;};
 canvas.onwheel=e=>{e.preventDefault();zoom=Math.max(.1,Math.min(20,zoom*(e.deltaY<0?1.15:1/1.15)));redraw();};
 canvas.onkeydown=e=>{if(['ArrowLeft','ArrowRight','ArrowUp','ArrowDown','+','=','-','Home','Enter'].includes(e.key))e.preventDefault();if(e.key==='ArrowLeft')yaw-=.15;if(e.key==='ArrowRight')yaw+=.15;if(e.key==='ArrowUp')pitch=Math.max(.05,pitch-.1);if(e.key==='ArrowDown')pitch=Math.min(Math.PI-.05,pitch+.1);if(['+','='].includes(e.key))zoom=Math.min(20,zoom*1.2);if(e.key==='-')zoom=Math.max(.1,zoom/1.2);if(e.key==='Home'){zoom=1;pan=[0,0];}if(e.key==='Enter'&&selected.length)onInspect(selected[0].value);redraw();};
 const observer=new ResizeObserver(redraw);observer.observe(canvas);
 return {update(next,originalSource,preferences){values=next.filter(v=>v.point);source=originalSource;options=preferences;prepare();redraw();},destroy(){disposed=true;values=[];positions=[];selected=[];cancelAnimationFrame(frame);observer.disconnect();if(gl){if(buffer)gl.deleteBuffer(buffer);if(program)gl.deleteProgram(program);gl.getExtension('WEBGL_lose_context')?.loseContext();}root.remove();}};
}
