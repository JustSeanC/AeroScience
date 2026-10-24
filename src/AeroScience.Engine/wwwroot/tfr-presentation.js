import {planningMeasure} from './science-display.js';

export class TfrInputError extends Error {}

// Presentation of the version-1 server findings, never interpretation of notice text.
export const freshDetail='Recently retrieved; cancellation/completeness unverified';
export const tfrGuidance='Published FAA boundaries are simulation references, not proof of an active restriction. Cancellation, aircraft applicability and complete coverage remain unverified. Missing shapes cannot clear a route.';
export const tfrStyles={
 published:{color:'#bd862d',dashArray:'7 5',label:'Published boundary · operational status unknown'},
 potential:{color:'#a76ee6',dashArray:'3 4',label:'Potential mission intersection · not confirmed legal applicability'},
 outside:{color:'#6e93b8',dashArray:'12 5 2 5',label:'Outside assessed time or altitude dimensions · no all-clear'},
 retained:{color:'#83909e',dashArray:'2 7',label:'Stale / offline / missing operational evidence'},
};
export function findingKind(finding){
 return finding?.outcome==='Outside assessed dimensions; lifecycle unverified'?'outside':finding?.outcome==='Potential intersection'?'potential':'published';
}
export function findingTitle(finding){return findingKind(finding)==='outside'?'Outside assessed dimensions':finding?.outcome==='Potential intersection'?'Potential intersection':'Unable to determine';}
export function missingDimensions(f){return !f.areaId||f.time==='Unknown'||f.altitude==='Unknown'||!f.operationalRetrievedUtc;}
export function utc(value){if(!value||!Number.isFinite(Date.parse(value)))return 'Unknown';return new Date(value).toISOString();}
export function age(value){const t=Date.parse(value);if(!Number.isFinite(t))return 'Age Unknown';const minutes=Math.floor((Date.now()-t)/60000);return minutes<0?'Timestamp ahead of this browser clock':minutes<1?'less than one minute ago':minutes<60?`${minutes} minutes ago`:`${Math.floor(minutes/60)} hours ago`;}
export function utcInput(value){
 if(!value)return null;
 const parts=/^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})(?::(\d{2}))?$/.exec(value);
 if(!parts)throw new TfrInputError('Enter a complete UTC date and time.');
 const [,year,month,day,hour,minute,second='00']=parts;const iso=`${year}-${month}-${day}T${hour}:${minute}:${second}.000Z`;
 const date=new Date(iso);
 if(!Number.isFinite(date.getTime())||date.toISOString()!==iso)throw new TfrInputError('Enter a valid UTC date and time.');
 return iso;
}
export function plannedWindow(basis,start,end){
 if(basis!=='Real-world planned UTC')return {plannedStartUtc:null,plannedEndUtc:null};
 const a=utcInput(start),b=utcInput(end);
 if(Boolean(a)!==Boolean(b))throw new TfrInputError('Enter both UTC times, or leave both blank to keep time unassessed.');
 if(a&&a>b)throw new TfrInputError('The UTC end must be at or after the UTC start.');
 return {plannedStartUtc:a,plannedEndUtc:b};
}
export function localDisplay(value){
 if(!value)return 'No UTC instant entered';
 return new Intl.DateTimeFormat(undefined,{dateStyle:'medium',timeStyle:'long'}).format(new Date(value))+` (${Intl.DateTimeFormat().resolvedOptions().timeZone})`;
}
const number=v=>Number.isFinite(v)?v.toLocaleString(undefined,{maximumFractionDigits:3}):'Unknown';
export function savedAltitude(plan){
 const display=v=>{if(!Number.isFinite(v))return 'Unknown';const d=planningMeasure('altitude',v,false);return `${number(d.value)} ${d.unit} (${number(v)} ft MSL saved)`;};
 return `Saved mission altitude envelope: ${display(plan?.minimumAltitudeFeet)} to ${display(plan?.maximumAltitudeFeet)}. Missing limits are not inferred from the route.`;
}
export function sourceLevel(level){
 if(!level)return 'Unknown';
 const original=`${number(level.value)} ${level.unit} ${level.reference}`;
 if(level.unit==='FT'&&level.reference==='MSL'&&Number.isFinite(level.value)){
  const d=planningMeasure('altitude',level.value,false);
  if(d.scale!==1)return `${original} (${number(d.value)} ${d.unit} display)`;
 }
 return original;
}
export function geometryFor(plan,choice){
 const route=choice==='route'?(plan.waypoints||[]).map(({latitude,longitude})=>({latitude,longitude})):[];
 let area=choice==='route'?null:plan.surveyAreas?.[Number(choice)]?.vertices?.map(({latitude,longitude})=>({latitude,longitude}));
 if(area?.length&&(area[0].latitude!==area.at(-1).latitude||area[0].longitude!==area.at(-1).longitude))area.push({...area[0]});
 const points=area||route;
 if(!points?.length)throw new TfrInputError('This saved geometry has no coordinates. Choose a saved route or research area.');
 if(points.some(c=>!Number.isFinite(c.latitude)||!Number.isFinite(c.longitude)||Math.abs(c.latitude)>85||Math.abs(c.longitude)>180))throw new TfrInputError('These coordinates are outside supported TFR screening bounds.');
 const west=Math.min(...points.map(p=>p.longitude)),east=Math.max(...points.map(p=>p.longitude)),south=Math.min(...points.map(p=>p.latitude)),north=Math.max(...points.map(p=>p.latitude));
 if(east-west>30||north-south>30)throw new TfrInputError('This route is too wide or crosses the dateline. Review smaller saved research areas; the whole route remains unassessed.');
 // A small viewport pad is for retrieval only, never a manufactured restriction boundary.
 const padX=Math.min(.02,(30-(east-west))/2),padY=Math.min(.02,(30-(north-south))/2);
 return {route,area,bounds:{west:Math.max(-180,west-padX),east:Math.min(180,east+padX),south:Math.max(-85,south-padY),north:Math.min(85,north+padY)}};
}
export function reviewCounts(result){
 const list=result.findings||[];
 return {findings:list.length,potential:new Set(result.potentialNotamIds||[]).size,insufficient:list.filter(missingDimensions).length,outside:list.filter(f=>findingKind(f)==='outside').length};
}
export function detailState(view){
 if(!view||view.status==='Not searched')return 'Not yet retrieved · operational evidence missing';
 if(view.status==='Notice revision changed; reassess')return 'Known notice revision changed · retrieve matching details';
 if(view.status===freshDetail)return 'Recently retrieved · matches known revision; cancellation unverified';
 if(view.status==='Stale or offline retained; current applicability Unknown')return 'Stale / offline / failed refresh · current applicability Unknown';
 return 'Unsupported or unassessed evidence · applicability Unknown';
}
export const errorText={RateLimited:'FAA requested a delay. Retained data remains available; retry later.',ProviderUnavailable:'FAA could not supply these details. Retained data is not current verification.',InvalidMetadata:'The supplied packets could not be safely interpreted. Review the official notice.',Timeout:'The request timed out. Retained information remains available.',NetworkUnavailable:'The provider could not be reached. Check network access and FAA consent.'};
export function requestProblem(error){
 const known={
  'FAA online reference consent is disabled.':'FAA online access is disabled. Enable it in FAA Catalog & Notices only if you want an official refresh.',
  'FAA detail refresh is throttled; inspect retained evidence.':'This notice was requested recently. Inspect retained details and wait at least five minutes before another request.',
  'Refresh the stale FAA notice inventory before retrieving operational details.':'Update the official boundary inventory first; then retrieve this notice’s matching details.',
  'FAA TFR refresh is throttled; retained data remains available.':'FAA inventory refresh is throttled. Retained data is still available; try later.',
 };
 return known[error?.message]||'This request could not be completed. Check the companion connection, FAA permission and retained-source status, then try again. No mission or scientific data was changed.';
}
