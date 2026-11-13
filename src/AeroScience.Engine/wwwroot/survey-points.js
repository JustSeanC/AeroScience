// Rendering only: choose actual records, never average or manufacture returns.
import {color} from './workstation-ui.js';
export const pointModes=['elevation','classification','intensity','return','rgb','heightAboveGround'];
export function modeAvailable(mode,source){const d=source?.dimensions||[];return mode==='elevation'?d.includes('Z'):mode==='classification'?d.includes('Classification'):mode==='intensity'?d.includes('Intensity'):mode==='return'?d.includes('ReturnNumber'):mode==='rgb'?['Red','Green','Blue'].every(v=>d.includes(v)):false;}
export function pointValue(v,mode){const p=v.point?.original;return mode==='classification'?p?.classification:mode==='intensity'?p?.intensity:mode==='return'?p?.returnNumber:p?.z;}
const classes=['#806aaf','#377f91','#8c7022','#4f834b','#22784e','#397b4d','#a64b53','#777777','#62778d'];
export function pointColor(v,mode,low,high){const p=v.point?.original;if(!p)return '#777777';if(mode==='rgb')return p.rgb?.length===3?'rgb('+p.rgb.map(v=>Math.round(v/65535*255)).join(',')+')':'#777777';if(mode==='classification')return classes[p.classification%classes.length];return color(pointValue(v,mode),low,high);}
export function filteredPoints(values,{ground=false,classification='',returns=''}={}){return values.filter(v=>v.point&&(!ground||v.point.original.classification===2)&&(classification===''||String(v.point.original.classification)===classification)&&(returns===''||String(v.point.original.returnNumber)===returns));}
export function pointRange(values,mode){let low=Infinity,high=-Infinity;for(const v of values){const n=pointValue(v,mode);if(Number.isFinite(n)){low=Math.min(low,n);high=Math.max(high,n);}}return [low,high];}
export function representatives(values,project,{width,height,cell=3,depth=false,limit=65536}={}){
 const occupied=new Set(),chosen=[];for(const v of values){const p=project(v);if(!p||p[0]<0||p[0]>width||p[1]<0||p[1]>height)continue;const key=Math.floor(p[0]/cell)+','+Math.floor(p[1]/cell)+(depth?','+Math.floor(p[2]*64):'');if(occupied.has(key))continue;occupied.add(key);chosen.push({value:v,screen:p});if(chosen.length>=limit)break;}return chosen;
}
