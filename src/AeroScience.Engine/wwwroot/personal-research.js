import {newIdentity,node} from './workstation-ui.js';
import {researchExperimentReview} from './personal-research-review.js';
import {flightRegimeFields,researchStarterChoices,researchEntryActions} from './personal-research-start.js';
let epoch = 0, cardOffset = 0, resultOffset = 0;
let draft = null;
export function disposePersonalResearch() { epoch++; }

export async function personalResearch(root, api) {
  const run = ++epoch;
  const active = () => run === epoch && !root.hidden;
  root.replaceChildren(node('h1', 'My Research'), node('h2', 'Personal Research'),
    node('p', 'Your experiments stay on this computer. No organization, account or internet connection is needed.'));
  const status = node('p', 'Loading personal test cards…'); status.setAttribute('role', 'status'); root.append(status);
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
    status.textContent = 'Flight-Regime Validation uses recorded indicated airspeed and altitude above mean sea level. Flight recording and science sessions keep their normal controls.';
    researchStarterChoices(root,api,starters,active);
    const editor = node('section', undefined, 'panel research-panel');
    const renderEditor = () => {
      editor.replaceChildren(node('h2', draft?.id ? 'Edit Flight-Regime Validation' : 'Create Flight-Regime Validation'));
      editor.append(node('p', 'Choose targets appropriate for your aircraft. Results assess simulator evidence only; they are not aircraft certification.'));
      const form = node('form'); form.id = 'research-card-form';
      const fields = node('div', undefined, 'research-grid');
      const name = field(fields, 'Test card name', draft?.name || 'Flight-Regime Validation', 'text'); name.required = true; name.maxLength = 100;
      const rule=draft?.flightRegime||starters.find(s=>s.id==='flight-regime').example;
      const targets=flightRegimeFields(fields,rule);
      const read = () => ({id:draft?.id, revision:draft?.revision, name:name.value,
        flightRegime:targets.read()});
      form.oninput = () => { draft = read(); };
      const save = node('button', draft?.id ? 'Save New Revision' : 'Save Test Card', 'primary'); save.type = 'submit';
      form.onsubmit = async event => {
        event.preventDefault(); if (save.disabled) return; save.disabled = true;
        const value = read();
        for (const input of fields.querySelectorAll('input')) input.disabled = true;
        try {
          await api('/api/research/cards', {name:value.name, flightRegime:value.flightRegime, id:value.id || null, expectedRevision:value.revision || null});
          if (JSON.stringify(draft) === JSON.stringify(value)) draft = null;
          if (active()) { draft = null; await personalResearch(root, api); }
        } catch (error) { if (active()) status.textContent = error.message; }
        finally { if (save.isConnected) { save.disabled = false; for (const input of fields.querySelectorAll('input')) input.disabled = false; } }
      };
      form.append(fields, save, node('p', 'Saving a revision keeps earlier cards, mission snapshots and results unchanged. No data download is needed.'));
      editor.append(form);
    };
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
        review.append(interval, node('p', 'Times above are UTC, not local or simulator clock time. Choose the whole flight or a shorter test interval. Samples must be at most 1.5 seconds apart; pauses and gaps cannot be joined into a successful hold.'),
          action('Evaluate Recorded Attempt', async () => {
            if (!start.value || !end.value) throw Error('Choose both UTC times for this recorded attempt.');
            const input = {flightId:select.value, startUtc:new Date(start.value+'Z').toISOString(), endUtc:new Date(end.value+'Z').toISOString(),cardId:card.id};
            const key = JSON.stringify(input);
            if (lastEvaluation?.key !== key) lastEvaluation = {key, requestId:newIdentity()};
            const attempt = await api('/api/research/attempts', {requestId:lastEvaluation.requestId, ...input});
            if (active() && request === reviewEpoch) { result.replaceChildren(attemptView(attempt)); }
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
      row.append(node('h3', card.name), node('p', `Revision ${card.revision} · ${card.flightRegime.airspeedKnots} ± ${card.flightRegime.airspeedToleranceKnots} knots IAS · ${card.flightRegime.altitudeMslFeet} ± ${card.flightRegime.altitudeToleranceFeet} feet MSL · ${card.flightRegime.holdSeconds} second hold`),
        action('Open in Mission Planner', async () => {
          const plan = await api(`/api/research/cards/${card.id}/plan`, {id:card.id, revision:card.revision});
          if (active()) location.hash = `#planner?researchPlan=${plan.id}`;
        }, 'primary'),
        action('Review Recorded Flight', () => showReview(card)),
        action('Overview & Compare',()=>researchExperimentReview(experiment,api,card,active)),
        action('Edit Card', () => { draft = structuredClone(card); renderEditor(); editor.scrollIntoView({block:'start'}); editor.querySelector('input').focus(); }));
      root.append(row);
      researchEntryActions(row,api,{card,starter:starters.find(s=>s.id===card.templateId)},active);
    }
    root.append(action('Newer Cards', async () => {cardOffset = Math.max(0,cardOffset-20); await personalResearch(root,api);}), action('Older Cards', async () => {cardOffset += 20; await personalResearch(root,api);}), experiment, review);
    const saved=node('details',undefined,'panel research-panel');saved.append(node('summary','Saved Local Results — All Evaluations'),node('p','Every saved evaluation is retained here. Overview & Compare groups repeated evaluations into their original attempts.'));
    if (!attempts.length) saved.append(node('p', 'No saved results on this page. Review a completed card flight to evaluate its recorded evidence.'));
    for (const attempt of attempts) saved.append(attemptView(attempt));
    saved.append(action('Newer Results', async () => {resultOffset = Math.max(0,resultOffset-20); await personalResearch(root,api);}), action('Older Results', async () => {resultOffset += 20; await personalResearch(root,api);}));root.append(saved);
    const selectedFlight = new URLSearchParams(location.hash.split('?')[1] || '').get('flight');
    if (selectedFlight) {
      const research = await api(`/api/missions/${encodeURIComponent(selectedFlight)}/research`);
      const ref = research.planSnapshot?.researchCards?.[0]??research.planSnapshot?.researchCard;
      if (active() && ref) await showReview(await api(`/api/research/cards/${ref.id}?revision=${ref.revision}`), selectedFlight);
    }
  } catch (error) { if (active()) status.textContent = `Personal Research could not load: ${error.message}`; }
}

export function attemptView(attempt) {
  const panel = node('article', undefined, 'panel research-panel');
  const outcome = {pass:'Pass', fail:'Fail', insufficientEvidence:'Insufficient Evidence'}[attempt.outcome] || 'Unknown';
  panel.append(node('h3', `${attempt.card.name} — ${outcome}`), node('p', attempt.explanation),
    node('p', `Longest supported hold: ${attempt.longestSupportedHoldSeconds.toFixed(1)} seconds · Required: ${attempt.card.flightRegime.holdSeconds} seconds · Card revision ${attempt.card.revision}`),
    node('p', `Evidence: ${attempt.startUtc} to ${attempt.endUtc} · ${attempt.evidence.sampleCount} recorded samples · ${attempt.evidence.source === 'simulator' ? 'Recorded simulator telemetry' : attempt.evidence.source === 'simulated' ? 'Synthetic/demo telemetry; not an installed MSFS test' : 'Source unavailable; no telemetry source type inferred'}`));
  const details = node('details'); details.append(node('summary', 'Evidence References'), node('p', `Flight ${attempt.flightId} · Original telemetry sequences ${attempt.evidence.firstSequence}–${attempt.evidence.lastSequence}`),
    node('p', `Evaluator ${attempt.evaluatorVersion} · Evidence SHA-256 ${attempt.evidence.sha256} · Plan SHA-256 ${attempt.evidence.planSha256}`));
  panel.append(details); return panel;
}
