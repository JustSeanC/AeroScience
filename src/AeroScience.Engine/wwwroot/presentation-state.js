// Read-only projection of /api/mission-intelligence. Never a command controller.
export function missionPresentation(data, {now = Date.now(), pending = false} = {}) {
  const generated = Date.parse(data?.generatedUtc), age = now-generated;
  const flight = data?.flight?.activeMission, collection = data?.collection, state = collection?.state;
  const knownFlight = !!data?.flight && (flight === null || typeof flight?.id === 'string' && !!flight.id);
  const fresh = knownFlight && Number.isFinite(age) && age >= -2000 && age <= 6000;
  const bound = fresh && !!flight && state?.missionId === flight.id;
  const phases = ['armed','collecting','paused','betweenSessions','stopped','ended'];
  let science = !fresh ? 'unknown' : !flight ? 'notStarted' : !bound ? 'unknown'
    : collection.fault || data.flight.research?.scienceStorageError || data.flight.connection?.state !== 'connected'
      ? 'unavailable' : phases.includes(state.phase) ? state.phase : 'unknown';
  if (science === 'collecting' && !state.currentSessionId) science = 'unknown';
  return Object.freeze({fresh, flightId:fresh ? flight?.id || null : null,
    recording:!fresh ? 'unknown' : flight ? 'active' : 'inactive', science, pending:!!pending,
    // Availability comes only from the server, with stale/unbound/pending guarded.
    scienceActions:Object.freeze(bound && !pending && !['unknown','unavailable'].includes(science)
      ? (Array.isArray(collection.actions) ? collection.actions : []).filter(action => action?.available === true && typeof action.action === 'string').map(action => action.action) : [])});
}
