import {missionPresentation} from './presentation-state.js';

const recent = (utc, now) => Number.isFinite(Date.parse(utc)) && now-Date.parse(utc) >= -2000 && now-Date.parse(utc) <= 6000;
// A read-only projection. No readiness, grades or command availability are inferred.
export function homePresentation({intelligence, failed=false, snapshot, draft, plan, planStatus='loading', archiveStatus='loading', archive=[]}, now=Date.now()) {
  const data=intelligence, flight=data?.flight?.activeMission;
  const identityMatches=!snapshot || (snapshot.activeMission?.id || null)===(flight?.id || null);
  const projected=missionPresentation(data,{now});
  const fresh=!failed && data?.version===1 && identityMatches && projected.fresh;
  const telemetry=data?.flight?.telemetry;
  const connection=!fresh ? 'unknown' : data.flight.connection?.state!=='connected' ? 'disconnected'
    : !recent(telemetry?.timestampUtc,now) ? 'stale' : telemetry.isPaused ? 'paused' : 'connected';
  const bound=fresh && !!flight && data.collection?.state?.missionId===flight.id;
  const collectionIssue=bound && !!(data.collection.fault || data.flight.research?.scienceStorageError
    || data.collection.state.continuityHold || data.collection.state.holds?.length);
  const science=!fresh ? 'unknown' : !flight ? 'notStarted'
    : connection!=='connected' || collectionIssue ? 'unavailable' : projected.science;
  const selectionId=snapshot ? snapshot.selectedPlanId : data?.flight?.selectedPlanId;
  const selected=selectionId && plan?.id===selectionId ? plan : null;
  const active=fresh && !!flight;
  const next=!fresh ? 'unavailable' : active ? 'active' : draft ? 'draft' : selectionId ? 'plan'
    : connection==='connected' ? 'idle' : 'connect';
  const retained=data?.retainedScience;
  const counts=active && retained?.missionId===flight.id && recent(retained.checkedUtc,now)
    && ['originalObservations','derivedObservations','remoteProducts'].every(key=>Number.isSafeInteger(retained[key]) && retained[key]>=0) ? retained : null;
  // An unsuccessful archive read is never "no results". Live recordings are not saved results.
  const results=archiveStatus==='available' ? archive.filter(row=>row?.id && typeof row.name==='string' && ['completed','interrupted'].includes(row.status)) : [];
  return Object.freeze({fresh,next,connection,science,collectionIssue,recording:fresh ? projected.recording : 'unknown',
    activeFlight:active ? flight : null, selectedPlan:selected, selectionId,
    planUnavailable:!!selectionId && (!selected && planStatus!=='loading'), draft, counts,
    source:fresh ? data.flight.connection?.source : null, aircraft:connection==='connected' ? telemetry?.aircraft : null,
    recordingStop:fresh ? data.flight.recordingStop : null, results, archiveStatus});
}
