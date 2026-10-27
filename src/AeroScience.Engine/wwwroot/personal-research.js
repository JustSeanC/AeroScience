import {organizationAssignmentSummary,organizationClaimReview} from './organization-assignment-ui.js';
import {researchOrganizations} from './research-organizations.js';
import {procedureExample,showProcedureResult} from './flight-test-procedure.js';
import {customFlightRule,showCustomFlightEvidence} from './custom-flight-test.js';
import {sensorSummary,showSensorResult} from './experimental-sensor.js';
import {observationSummary,showObservationResult} from './observation-research.js';
import {surveySummary,showSurveyResult} from './survey-research.js';
import {atmosphericSummary,showAtmosphericResult} from './atmospheric-research.js';
import {newIdentity,node} from './workstation-ui.js';
import {researchExperimentReview} from './personal-research-review.js';
import {flightRegimeFields,atmosphericFields,researchFields,researchDefinition,isAtmosphericResearch,isScienceResearch,researchCardTargets,researchStarterChoices,researchEntryActions} from './personal-research-start.js';
let epoch = 0, cardOffset = 0, resultOffset = 0;
let draft = null;
export function disposePersonalResearch() { epoch++; }

export async function personalResearch(root, api) {
  const run = ++epoch;
  const active = () => run === epoch && !root.hidden;
  root.replaceChildren(node('h1', 'My Research'), node('h2', 'Personal Research'),
    node('p', 'Your experiments stay on this computer. No organization, account or internet connection is needed.'));
  const status = node('p', 'Loading personal test cards…'); status.setAttribute('role', 'status'); root.append(status);
  researchOrganizations(root,api,active);
  const action = (label, handler, style = 'secondary') => {
    const b = node('button', label, style); b.type = 'button';
    b.onclick = async () => {
      if (b.disabled) return; b.disabled = true;
      try { await handler(); } catch (error) { if (active()) status.textContent = error.message; }
      finally { if (b.isConnected) b.disabled = false; }
    };
    return b;
  };
  const field = (parent, label, value, type = 'number') => {
    const wrap = node('label', label), input = node('input'); input.type = type; input.value = value ?? '';
    if (type === 'number') { input.step = 'any'; input.required = true; }
    wrap.append(input); parent.append(wrap); return input;
  };
  try {
    const [cards, attempts, starters] = await Promise.all([api(`/api/research/cards?offset=${cardOffset}`), api(`/api/research/attempts?offset=${resultOffset}`),api('/api/research/starters')]);
    if (!active()) return;
    status.textContent = 'Choose an experiment below. Atmospheric research uses retained scientific observations; Flight-Regime Validation uses aircraft telemetry. Flight recording and science sessions keep their normal controls.';
    researchStarterChoices(root,api,starters,active);
    const editor = node('section', undefined, 'panel research-panel');
    const renderEditor = () => {
      const custom=!!draft?.procedure||draft?.templateId==='custom-flight-test'||!!draft?.flightRegime?.testPoint;const atmospheric=draft?.experimentalSensor||draft?.observationFlight||draft?.survey||draft?.atmosphericProfile||draft?.atmosphericTransect;editor.replaceChildren(node('h2',custom?'Create or Edit Custom Test Card':draft?.experimentalSensor?'Edit Experimental Sensor Checkout':draft?.observationFlight?'Edit Scientific Observation Flight':draft?.survey?'Edit Terrain & Temperature Survey':draft?.atmosphericTransect?'Edit Atmospheric Transect':atmospheric?'Edit Vertical Atmospheric Profile':draft?.id?'Edit Flight-Regime Validation':'Create Flight-Regime Validation'));
      editor.append(node('p', atmospheric?'Choose achievable sampling goals. Collection coverage is not proof of a hypothesis.':'Choose targets appropriate for your aircraft. Results assess simulator evidence only; they are not aircraft certification.'));
      const form = node('form'); form.id = 'research-card-form';
      const fields = node('div', undefined, 'research-grid');
      const name = field(fields, 'Test card name', draft?.name || 'Flight-Regime Validation', 'text'); name.required = true; name.maxLength = 100;
      const rule=draft?.flightRegime||starters.find(s=>s.id==='flight-regime').example;
      const targets=researchFields(fields,draft||{flightRegime:rule},{api,revision:draft?.revision||0});
      const read = () => ({...draft,id:draft?.id, revision:draft?.revision, name:name.value,
        procedure:draft?.procedure?targets.read():null,flightRegime:atmospheric||draft?.procedure?null:targets.read(),atmosphericProfile:draft?.atmosphericProfile?targets.read():null,atmosphericTransect:draft?.atmosphericTransect?targets.read():null,survey:draft?.survey?targets.read():null,observationFlight:draft?.observationFlight?targets.read():null,experimentalSensor:draft?.experimentalSensor?targets.read():null});
      form.oninput = () => { draft = read(); };
      const save = node('button', draft?.id ? 'Save New Revision' : 'Save Test Card', 'primary'); save.type = 'submit';if(targets.isSupported?.()===false)save.disabled=true;
      form.onsubmit = async event => {
        event.preventDefault(); if (save.disabled) return; save.disabled = true;
        const value = read();
        const editing=Array.from(fields.querySelectorAll('input,textarea,select,button'),control=>[control,control.disabled]);for(const [control]of editing)control.disabled=true;
        try {
          await api('/api/research/cards', {name:value.name, procedure:value.procedure, flightRegime:value.flightRegime,atmosphericProfile:value.atmosphericProfile,atmosphericTransect:value.atmosphericTransect,survey:value.survey,observationFlight:value.observationFlight,experimentalSensor:value.experimentalSensor, id:value.id || null, expectedRevision:value.revision || null});
          if (JSON.stringify(draft) === JSON.stringify(value)) draft = null;
          if (active()) { draft = null; await personalResearch(root, api); }
        } catch (error) { if (active()) status.textContent = error.message; }
        finally { if (save.isConnected) { save.disabled = false; for(const [control,disabled]of editing)control.disabled=disabled; } }
      };
      form.append(fields, save, node('p', 'Saving a revision keeps earlier cards, mission snapshots and results unchanged. No data download is needed.'));
      editor.append(form);
    };
    const author=action('Create Custom Test Card',()=>{draft={name:'Flight Test: Stable Operating Conditions',flightRegime:customFlightRule()};renderEditor();editor.scrollIntoView({block:'start'});editor.querySelector('input').focus();},'primary');
    root.insertBefore(author,root.querySelector('#research-starters'));
    root.insertBefore(action('Create Multi-Point Test Card',()=>{draft={name:'Flight Test: Operating Envelope Demonstration',procedure:procedureExample()};renderEditor();editor.scrollIntoView({block:'start'});editor.querySelector('input').focus();},'primary'),root.querySelector('#research-starters'));
    renderEditor(); root.append(editor);
    const experiment=node('div');
    const review = node('section', undefined, 'panel research-panel'); review.id = 'personal-research-review';
    let reviewEpoch = 0;
    const showReview = async (card, selectedFlight = null, offset = 0) => {
      const request = ++reviewEpoch;
      review.replaceChildren(node('h2', `Review ${card.name}`), node('p', 'Loading completed flights…'));
      const flights = await api(`/api/research/cards/${card.id}/flights?offset=${offset}`);
      if (selectedFlight && !flights.some(f => f.id === selectedFlight)) {
        const [detail, research] = await Promise.all([api(`/api/missions/${selectedFlight}`), api(`/api/missions/${selectedFlight}/research`)]);
        if (!(research.planSnapshot?.researchCards??[research.planSnapshot?.researchCard]).some(ref=>ref?.id===card.id)) throw Error('This recorded flight belongs to a different test card. Reload the selected flight.');
        if (detail.mission.status === 'recording') throw Error('End this flight before evaluating its recorded attempt.');
        flights.unshift(detail.mission); // Exact history link can reference a flight beyond the current page.
      }
      if (!active() || request !== reviewEpoch) return;
      review.replaceChildren(node('h2', `Review ${card.name}`), node('p', 'Each flight uses the card revision frozen when recording started. A later card edit cannot change that result.'));
      if(card.organizationAssignment){const recorded=flights.find(f=>f.id===selectedFlight)||flights[0];review.append(organizationAssignmentSummary(card,recorded?{flightId:recorded.id,startUtc:recorded.startedUtc,endUtc:recorded.endedUtc}:{}));organizationClaimReview(review,api,card);}
      if (!flights.length) review.append(node('p', 'No completed flights on this page. Open this card in Mission Planner, select the plan for flight, record, and end your flight first.'));
      else {
        const label = node('label', 'Recorded flight'), select = node('select'); select.setAttribute('aria-label', 'Recorded flight'); label.append(select);
        for (const flight of flights) { const option = node('option', `${flight.name} · ${new Date(flight.startedUtc).toLocaleString()} · ${flight.status}`); option.value = flight.id; select.append(option); }
        if (flights.some(f => f.id === selectedFlight)) select.value = selectedFlight;
        review.append(label);
        const interval = node('div', undefined, 'research-grid');
        const start = field(interval, 'Evidence start (UTC)', '', 'datetime-local'); start.step = '0.001';
        const end = field(interval, 'Evidence end (UTC)', '', 'datetime-local'); end.step = '0.001';
        // HTML time controls support milliseconds; round inward rather than asking for bytes outside the recording.
        const setInterval = () => { const flight = flights.find(f => f.id === select.value); start.value = new Date(Date.parse(flight.startedUtc)+1).toISOString().slice(0,-1); end.value = new Date(flight.endedUtc).toISOString().slice(0,-1); };
        select.onchange = setInterval; setInterval();
        const result = node('div'); result.setAttribute('role', 'status');
        let lastEvaluation;
        if(isScienceResearch(card)){const advanced=node('details');advanced.append(node('summary','Optional — shorter collection interval (UTC)'),interval);review.append(advanced);}else review.append(interval);
        review.append(node('p', isScienceResearch(card)?'Review actual retained observations for the selected flight. Optional interval controls use UTC; raw flight context never fills missing or paused science.':'Times above are UTC, not local or simulator clock time. Choose the whole flight or a shorter test interval. Samples must be at most 1.5 seconds apart; pauses and gaps cannot be joined into a successful hold.'),
          action(card.procedure?'Evaluate Multi-Point Flight':card.experimentalSensor?'Evaluate Sensor Checkout':card.observationFlight?'Evaluate Scientific Observations':card.survey?'Evaluate Terrain & Temperature Survey':card.atmosphericTransect?'Evaluate Atmospheric Transect':card.atmosphericProfile?'Evaluate Atmospheric Profile':'Evaluate Recorded Attempt', async () => {
            if (!start.value || !end.value) throw Error('Choose both UTC times for this recorded attempt.');
            const input = {flightId:select.value, startUtc:new Date(start.value+'Z').toISOString(), endUtc:new Date(end.value+'Z').toISOString(),cardId:card.id};
            const key = JSON.stringify(input);
            if (lastEvaluation?.key !== key) lastEvaluation = {key, requestId:newIdentity()};
            const attempt = await api('/api/research/attempts', {requestId:lastEvaluation.requestId, ...input});
            if (active() && request === reviewEpoch) { if(card.procedure)await showProcedureResult(result,api,attempt,()=>active()&&request===reviewEpoch);else if(card.experimentalSensor)await showSensorResult(result,api,attempt,()=>active()&&request===reviewEpoch);else if(card.observationFlight)await showObservationResult(result,api,attempt,()=>active()&&request===reviewEpoch);else if(card.survey)await showSurveyResult(result,api,attempt,()=>active()&&request===reviewEpoch);else if(isAtmosphericResearch(card))await showAtmosphericResult(result,api,attempt,()=>active()&&request===reviewEpoch);else result.replaceChildren(attemptView(attempt,api)); }
          }, 'primary'), result);
        const timeline=node('div');review.append(timeline);
        const showTimeline=async()=>{timeline.replaceChildren();const {researchFlightTimeline}=await import('./personal-research-flight.js');if(active()&&request===reviewEpoch)await researchFlightTimeline(timeline,api,select.value);};
        select.addEventListener('change',()=>showTimeline().catch(error=>status.textContent=error.message));await showTimeline();
      }
      review.append(action('Newer Flights', () => showReview(card, null, Math.max(0, offset-20))), action('Older Flights', () => showReview(card, null, offset+20)));
      review.scrollIntoView({block:'nearest'});
    };
    root.append(node('h2', 'Your Test Cards'));
    if (!cards.length) root.append(node('p', 'No test cards on this page. Create your first experiment above.'));
    for (const card of cards) {
      const row = node('article', undefined, 'panel research-panel');
      row.append(node('h3', card.name), node('p', `Revision ${card.revision} · ${researchCardTargets(card)}`),
        action('Open in Mission Planner', async () => {
          const plan = await api(`/api/research/cards/${card.id}/plan`, {id:card.id, revision:card.revision});
          if (active()) location.hash = `#planner?researchPlan=${plan.id}`;
        }, 'primary'),
        action(card.procedure?'Review Multi-Point Flight':card.experimentalSensor?'Review Sensor Checkout':card.observationFlight?'Review Scientific Observations':card.survey?'Review Terrain & Temperature Survey':card.atmosphericTransect?'Review Atmospheric Transect':card.atmosphericProfile?'Review Atmospheric Profile':'Review Recorded Flight', () => showReview(card)),
        action('Overview & Compare',()=>researchExperimentReview(experiment,api,card,active)),
        action('Edit Card', () => { draft = structuredClone(card); renderEditor(); editor.scrollIntoView({block:'start'}); editor.querySelector('input').focus(); }));
      if(card.organizationAssignment){row.prepend(organizationAssignmentSummary(card));for(const b of row.querySelectorAll('button'))if(['Edit Card','Open in Mission Planner'].includes(b.textContent))b.remove();const open=node('a','Open Mission Planner','button secondary');open.href='#planner';row.append(open);organizationClaimReview(row,api,card);}
      root.append(row);
      if(!card.organizationAssignment)researchEntryActions(row,api,{card,starter:starters.find(s=>s.id===card.templateId)},active);
    }
    root.append(action('Newer Cards', async () => {cardOffset = Math.max(0,cardOffset-20); await personalResearch(root,api);}), action('Older Cards', async () => {cardOffset += 20; await personalResearch(root,api);}), experiment, review);
    const saved=node('details',undefined,'panel research-panel');saved.append(node('summary','Saved Local Results — All Evaluations'),node('p','Every saved evaluation is retained here. Overview & Compare groups repeated evaluations into their original attempts.'));
    if (!attempts.length) saved.append(node('p', 'No saved results on this page. Review a completed card flight to evaluate its recorded evidence.'));
    for (const attempt of attempts) saved.append(attemptView(attempt,api));
    saved.append(action('Newer Results', async () => {resultOffset = Math.max(0,resultOffset-20); await personalResearch(root,api);}), action('Older Results', async () => {resultOffset += 20; await personalResearch(root,api);}));root.append(saved);
    const query=new URLSearchParams(location.hash.split('?')[1]||'');const requestedCard=query.get('card');
    const selectedFlight = query.get('flight');
    if (selectedFlight) {
      const research = await api(`/api/missions/${encodeURIComponent(selectedFlight)}/research`);
      const refs=research.planSnapshot?.researchCards??(research.planSnapshot?.researchCard?[research.planSnapshot.researchCard]:[]);const ref=requestedCard?refs.find(r=>r.id===requestedCard):refs[0];
      if (active() && ref) await showReview(await api(`/api/research/cards/${ref.id}?revision=${ref.revision}`), selectedFlight);
    }else if(requestedCard&&active())await showReview(await api(`/api/research/cards/${encodeURIComponent(requestedCard)}`));
  } catch (error) { if (active()) status.textContent = `Personal Research could not load: ${error.message}`; }
}

export function attemptView(attempt,api) {
  if(attempt.card.procedure){const panel=node('section');if(api)showProcedureResult(panel,api,attempt);return panel;}
  if(attempt.experimentalSensor){const panel=sensorSummary(attempt);if(api){const control=node('button','Open Sensor Checkout','secondary');control.type='button';const detail=node('div');control.onclick=()=>showSensorResult(detail,api,attempt);panel.append(control,detail);}return panel;}
  if(attempt.observationFlight){const panel=observationSummary(attempt);if(api){const control=node('button','Open Scientific Observations','secondary');control.type='button';const detail=node('div');control.onclick=()=>showObservationResult(detail,api,attempt);panel.append(control,detail);}return panel;}
  if(attempt.survey){const panel=surveySummary(attempt);if(api){const control=node('button','Open Survey Results','secondary');control.type='button';const detail=node('div');control.onclick=()=>showSurveyResult(detail,api,attempt);panel.append(control,detail);}return panel;}
  if(attempt.atmosphericProfile){const panel=atmosphericSummary(attempt);if(api){const button=node('button',attempt.card.atmosphericTransect?'Open Atmospheric Transect':'Open Atmospheric Profile','secondary');button.type='button';const detail=node('div');button.onclick=()=>showAtmosphericResult(detail,api,attempt);panel.append(button,detail);}return panel;}
  const panel = node('article', undefined, 'panel research-panel');
  const outcome = {pass:'Pass', fail:'Fail', insufficientEvidence:'Insufficient Evidence'}[attempt.outcome] || 'Unknown';
  if(attempt.automatic)panel.append(node('p',`Automatic attempt · ${attempt.automatic.policyVersion} · original sequences ${attempt.automatic.firstSequence}–${attempt.automatic.lastSequence}. Recognition is not a deliberate-pilot-intent or certification claim.`));
  panel.append(node('h3', `${attempt.card.name} — ${outcome}`), node('p', attempt.explanation),
    node('p', attempt.card.flightRegime.testPoint?`${researchCardTargets(attempt.card)} · Card revision ${attempt.card.revision}`:researchCardTargets(attempt.card)),
    node('p', `Longest supported hold: ${attempt.longestSupportedHoldSeconds.toFixed(1)} seconds · Required: ${attempt.card.flightRegime.holdSeconds} seconds · Card revision ${attempt.card.revision}`),
    node('p', `Evidence: ${attempt.startUtc} to ${attempt.endUtc} · ${attempt.evidence.sampleCount} recorded samples · ${attempt.evidence.source === 'simulator' ? 'Recorded simulator telemetry' : attempt.evidence.source === 'simulated' ? 'Synthetic/demo telemetry; not an installed MSFS test' : 'Source unavailable; no telemetry source type inferred'}`));
  const details = node('details'); details.append(node('summary', 'Evidence References'), node('p', `Flight ${attempt.flightId} · Original telemetry sequences ${attempt.evidence.firstSequence}–${attempt.evidence.lastSequence}`),
    node('p', `Evaluator ${attempt.evaluatorVersion} · Evidence SHA-256 ${attempt.evidence.sha256} · Plan SHA-256 ${attempt.evidence.planSha256}`));
  panel.append(organizationAssignmentSummary(attempt.card,attempt),details);
  if(attempt.card.flightRegime.testPoint&&api){const button=node('button','Inspect Custom Test Evidence','secondary');button.type='button';const detail=node('div');button.onclick=()=>showCustomFlightEvidence(detail,api,attempt);panel.append(button,detail);}
  return panel;
}
