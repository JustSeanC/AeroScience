import {message as localizedCopy} from './i18n.js';
import {t as textForUI} from './i18n.js';
import {planningMeasure} from './science-display.js';

export class TfrInputError extends Error {}

// Presentation of the version-1 server findings, never interpretation of notice text.
// Original server-issued status identity. Translate its explanation, never this comparison token.
export const freshDetail='Recently retrieved; cancellation/completeness unverified';
export const tfrGuidance=localizedCopy("ui.tfrPresentation.messagePublishedFAABoundariesAreSimulationReferencesNot");
export const tfrStyles={
 published:{color:'#bd862d',dashArray:'7 5',get label(){return textForUI("ui.tfrPresentation.messagePublishedBoundaryOperationalStatusUnknown");}},
 potential:{color:'#a76ee6',dashArray:'3 4',get label(){return textForUI("ui.tfrPresentation.messagePotentialMissionIntersectionNotConfirmedLegal");}},
 outside:{color:'#6e93b8',dashArray:'12 5 2 5',get label(){return textForUI("ui.tfrPresentation.messageOutsideAssessedTimeOrAltitudeDimensions");}},
 retained:{color:'#83909e',dashArray:'2 7',get label(){return textForUI("ui.tfrPresentation.messageStaleOfflineMissingOperationalEvidence");}},
};
export function findingKind(finding){
 return finding?.outcome==='Outside assessed dimensions; lifecycle unverified'?'outside':finding?.outcome==='Potential intersection'?'potential':'published';
}
export function findingTitle(finding){return findingKind(finding)==='outside'?textForUI("ui.tfrPresentation.messageOutsideAssessedDimensions"):finding?.outcome==='Potential intersection'?textForUI("ui.tfrPresentation.messagePotentialIntersection"):textForUI("ui.tfrPresentation.messageUnableToDetermine");}
export function missingDimensions(f){return !f.areaId||f.time==='Unknown'||f.altitude==='Unknown'||!f.operationalRetrievedUtc;}
export function utc(value){if(!value||!Number.isFinite(Date.parse(value)))return textForUI("localization.unknownValue");return new Date(value).toISOString();}
export function age(value){const t=Date.parse(value);if(!Number.isFinite(t))return textForUI("ui.tfrPresentation.messageAgeUnknown");const minutes=Math.floor((Date.now()-t)/60000);return minutes<0?textForUI("ui.tfrPresentation.messageTimestampAheadOfThisBrowserClock"):minutes<1?'less than one minute ago':minutes<60?textForUI("ui.tfrPresentation.messageMinutesAgo",{value1:(String(minutes))}):textForUI("ui.tfrPresentation.messageHoursAgo",{value1:(String(Math.floor(minutes/60)))});}
export function utcInput(value){
 if(!value)return null;
 const parts=/^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})(?::(\d{2}))?$/.exec(value);
 if(!parts)throw new TfrInputError(textForUI("ui.tfrPresentation.messageEnterACompleteUTCDateAndTime"));
 const [,year,month,day,hour,minute,second='00']=parts;const iso=`${year}-${month}-${day}T${hour}:${minute}:${second}.000Z`;
 const date=new Date(iso);
 if(!Number.isFinite(date.getTime())||date.toISOString()!==iso)throw new TfrInputError(textForUI("ui.tfrPresentation.messageEnterAValidUTCDateAndTime"));
 return iso;
}
export function plannedWindow(basis,start,end){
 if(basis!=='Real-world planned UTC')return {plannedStartUtc:null,plannedEndUtc:null};
 const a=utcInput(start),b=utcInput(end);
 if(Boolean(a)!==Boolean(b))throw new TfrInputError(textForUI("ui.tfrPresentation.messageEnterBothUTCTimesOrLeaveBoth"));
 if(a&&a>b)throw new TfrInputError(textForUI("ui.tfrPresentation.messageTheUTCEndMustBeAtOr"));
 return {plannedStartUtc:a,plannedEndUtc:b};
}
export function localDisplay(value){
 if(!value)return textForUI("ui.tfrPresentation.messageNoUTCInstantEntered");
 return new Intl.DateTimeFormat(undefined,{dateStyle:'medium',timeStyle:'long'}).format(new Date(value))+` (${Intl.DateTimeFormat().resolvedOptions().timeZone})`;
}
const number=v=>Number.isFinite(v)?v.toLocaleString(undefined,{maximumFractionDigits:3}):textForUI("localization.unknownValue");
export function savedAltitude(plan){
 const display=v=>{if(!Number.isFinite(v))return textForUI("localization.unknownValue");const d=planningMeasure('altitude',v,false);return textForUI("ui.tfrPresentation.messageFtMSLSaved",{value1:(String(number(d.value))),value2:(String(d.unit)),value3:(String(number(v)))});};
 return textForUI("ui.tfrPresentation.messageSavedMissionAltitudeEnvelopeToMissing",{value1:(String(display(plan?.minimumAltitudeFeet))),value2:(String(display(plan?.maximumAltitudeFeet)))});
}
export function sourceLevel(level){
 if(!level)return textForUI("localization.unknownValue");
 const original=`${number(level.value)} ${level.unit} ${level.reference}`;
 if(level.unit==='FT'&&level.reference==='MSL'&&Number.isFinite(level.value)){
  const d=planningMeasure('altitude',level.value,false);
  if(d.scale!==1)return textForUI("ui.tfrPresentation.messageDisplay",{value1:(String(original)),value2:(String(number(d.value))),value3:(String(d.unit))});
 }
 return original;
}
export function geometryFor(plan,choice){
 const route=choice==='route'?(plan.waypoints||[]).map(({latitude,longitude})=>({latitude,longitude})):[];
 let area=choice==='route'?null:plan.surveyAreas?.[Number(choice)]?.vertices?.map(({latitude,longitude})=>({latitude,longitude}));
 if(area?.length&&(area[0].latitude!==area.at(-1).latitude||area[0].longitude!==area.at(-1).longitude))area.push({...area[0]});
 const points=area||route;
 if(!points?.length)throw new TfrInputError(textForUI("ui.tfrPresentation.messageThisSavedGeometryHasNoCoordinatesChoose"));
 if(points.some(c=>!Number.isFinite(c.latitude)||!Number.isFinite(c.longitude)||Math.abs(c.latitude)>85||Math.abs(c.longitude)>180))throw new TfrInputError(textForUI("ui.tfrPresentation.messageTheseCoordinatesAreOutsideSupportedTFRScreening"));
 const west=Math.min(...points.map(p=>p.longitude)),east=Math.max(...points.map(p=>p.longitude)),south=Math.min(...points.map(p=>p.latitude)),north=Math.max(...points.map(p=>p.latitude));
 if(east-west>30||north-south>30)throw new TfrInputError(textForUI("ui.tfrPresentation.messageThisRouteIsTooWideOrCrosses"));
 // A small viewport pad is for retrieval only, never a manufactured restriction boundary.
 const padX=Math.min(.02,(30-(east-west))/2),padY=Math.min(.02,(30-(north-south))/2);
 return {route,area,bounds:{west:Math.max(-180,west-padX),east:Math.min(180,east+padX),south:Math.max(-85,south-padY),north:Math.min(85,north+padY)}};
}
export function reviewCounts(result){
 const list=result.findings||[];
 return {findings:list.length,potential:new Set(result.potentialNotamIds||[]).size,insufficient:list.filter(missingDimensions).length,outside:list.filter(f=>findingKind(f)==='outside').length};
}
export function detailState(view){
 if(!view||view.status==='Not searched')return textForUI("ui.tfrPresentation.messageNotYetRetrievedOperationalEvidenceMissing");
 if(view.status==='Notice revision changed; reassess')return textForUI("ui.tfrPresentation.messageKnownNoticeRevisionChangedRetrieveMatching");
 if(view.status===freshDetail)return textForUI("ui.tfrPresentation.messageRecentlyRetrievedMatchesKnownRevisionCancellation");
 if(view.status==='Stale or offline retained; current applicability Unknown')return textForUI("ui.tfrPresentation.messageStaleOfflineFailedRefresh");
 return textForUI("ui.tfrPresentation.messageUnsupportedOrUnassessedEvidenceApplicabilityUnknown");
}
export const errorText={get RateLimited(){return textForUI("ui.tfrPresentation.messageFAARequestedADelayRetainedDataRemains");},get ProviderUnavailable(){return textForUI("ui.tfrPresentation.messageFAACouldNotSupplyTheseDetailsRetained");},get InvalidMetadata(){return textForUI("ui.tfrPresentation.messageTheSuppliedPacketsCouldNotBeSafely");},get Timeout(){return textForUI("ui.tfrPresentation.messageTheRequestTimedOutRetainedInformationRemains");},get NetworkUnavailable(){return textForUI("ui.tfrPresentation.messageTheProviderCouldNotBeReachedCheck");}};
export function requestProblem(error){
 const known={
  'FAA online reference consent is disabled.':textForUI("ui.tfrPresentation.messageFAAOnlineAccessIsDisabledEnableIt"),
  'FAA detail refresh is throttled; inspect retained evidence.':textForUI("ui.tfrPresentation.messageThisNoticeWasRequestedRecentlyInspectRetained"),
  'Refresh the stale FAA notice inventory before retrieving operational details.':textForUI("ui.tfrPresentation.messageUpdateTheOfficialBoundaryInventoryFirstThen"),
  'FAA TFR refresh is throttled; retained data remains available.':textForUI("ui.tfrPresentation.messageFAAInventoryRefreshIsThrottledRetainedData"),
 };
 return known[error?.message]||textForUI("ui.tfrPresentation.messageThisRequestCouldNotBeCompletedCheck");
}
