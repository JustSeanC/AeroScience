import {node as el} from './workstation-ui.js';
import {dialogHeader} from './dialog-ui.js';

const scopeName = scope => ({entire:'Entire Mission','first-half':'First Half of Mission',areas:'Custom Areas'}[scope] || 'Unknown');
const sameLaunch = (a,b) => ['planId','name','mode','armScience'].every(key => a[key] === b[key]);
const sameRevision = (a,b) => a.planId === b.planId && a.planRevision === b.planRevision;

// All checks are read-only. The normal mission command remains the sole recording authority.
export async function startMissionWithReadiness(api, launch, {readLaunch, opener}={}) {
  async function context() {
    if (readLaunch && !sameLaunch(launch,readLaunch())) throw new Error('Mission launch information changed. Continue setup and review the current mission.');
    const state=await api('/api/state');
    if (state.activeMission) throw new Error('A mission is already recording. This advisory cannot start another flight.');
    if ((state.selectedPlanId || null)!==launch.planId) throw new Error('The selected mission plan changed. Continue setup and review the current selection.');
    if (!launch.planId) return null;
    try {
      const result=await api(`/api/plans/${launch.planId}/preparation/start-readiness`);
      if (result.planId!==launch.planId || !result.planRevision) throw new Error('Preparation response does not identify the selected saved plan.');
      return result;
    } catch {
      // A preparation outage is not a recording blocker. Still bind the saved plan version.
      const plan=await api(`/api/plans/${launch.planId}/revision`);
      if (plan.planId!==launch.planId || !plan.planRevision) throw new Error('The saved mission plan could not be verified. Refresh missions before starting.');
      return {...plan,status:'Unavailable',checkedUtc:new Date().toISOString(),
        message:'Scientific preparation readiness is unavailable. All readiness counts are Unknown; no full readiness is assumed.'};
    }
  }
  const initial=await context();
  async function submit(value) {
    if(readLaunch&&!sameLaunch(launch,readLaunch()))throw new Error('Mission launch information changed. Continue setup and review the current mission.');
    return api('/api/missions',{...launch,checkSelectedPlan:true,
      ...(value?.planRevision?{expectedPlanRevision:value.planRevision}:{})});
  }
  if (!initial || ['Ready','NotRequired'].includes(initial.status)) return submit(initial);

  const dialog=el('dialog');dialog.className='preparation-review mission-start-advisory';
  dialog.setAttribute('aria-label','Some Scientific Data Is Not Ready');
  const body=el('div',undefined,'preparation-review-body');body.tabIndex=0;body.setAttribute('role','region');body.setAttribute('aria-label','Scientific data advisory details');body.append(el('h2','Some Scientific Data Is Not Ready'));
  body.append(el('p','You can start your flight now. Flight recording will continue normally, and instruments can collect supported science where suitable data is available.'),
    el('p','Some planned research regions may not have all the required external data. Scientific measurements in those areas may be unavailable or incomplete.'));
  const summary=el('section');summary.setAttribute('aria-label','Current external scientific data readiness');body.append(summary);
  body.append(el('h3','Recording, collection and external data are separate'),el('p','Start Mission Anyway starts flight recording subject to the normal telemetry, storage and startup checks. Your Arm Science choice is preserved. Starting a science session does not guarantee valid measurements everywhere.'),
    el('p','AeroScience will attempt to prepare data for upcoming research areas during flight when scientific-data consent, network policy, storage, download allowances and flight performance permit. Resource contention can pause or throttle retrieval. User-paused or cancelled work stays under your control; unrelated Library downloads may yield. Downloads are not guaranteed to finish before arrival.'),
    el('p','Data arriving after the aircraft has passed an area cannot automatically create historical live scientific observations. After End Flight, unfinished mission preparation waits for explicit continuation for future use.'));
  const feedback=el('p');feedback.setAttribute('role','status');body.append(feedback);dialog.append(body);
  const footer=el('footer',undefined,'dialog-actions');dialog.append(footer);
  const button=(title,run)=>{const b=el('button',title);b.type='button';b.onclick=run;footer.append(b);return b;};
  let closed=false,busy=false,checking=false,invalid=false,timer,generation=0,submitting=false;
  function show(value) {
    summary.replaceChildren(el('p',value.message),el('p','Readiness checked '+new Date(value.checkedUtc).toLocaleTimeString()+'. Preparation can change; confirmation checks it again.'));
    const p=value.preparation;
    if (!p) {summary.append(el('p',`External data: ${value.status==='Stale'?'Stale assessment':value.status==='NotAssessed'?'Not Yet Assessed':'Unavailable'}. Total, ready, awaiting and unavailable instrument-region counts: Unknown.`));return;}
    const total=p.ready+p.awaiting+p.unavailable;
    summary.append(el('p',`Entire mission: ${p.ready} ready / ${total} planned instrument-region units · ${p.awaiting} awaiting preparation · ${p.unavailable} unavailable or limited. Entire mission ${value.status==='Ready'?'complete':'incomplete'}.`));
    summary.append(el('p',`Preflight scope: ${scopeName(p.scope)} · ${p.selectedReady}/${p.selectedUnits} selected units ready · ${p.selectedAwaiting} awaiting · ${p.selectedUnavailable} unavailable or limited. Selected scope ${p.selectedUnits>0&&p.selectedAwaiting===0&&p.selectedUnavailable===0?'complete':'incomplete'}.`));
    if (p.scope!=='entire') summary.append(el('p','Preparation outside your preflight selection may still be outstanding. This selection does not reduce the complete mission requirements or grading obligations.'));
    const list=el('ul');for (const i of value.instruments||[]) list.append(el('li',`${i.name}: ${i.ready}/${i.total} ready · ${i.awaiting} awaiting · ${i.unavailable} unavailable or limited${i.awaiting||i.unavailable?' — affected instrument':''}.`));summary.append(list);
    if (value.status==='Ready') summary.append(el('p','Preparation completed while this advisory was open. All planned external source units are now ready; valid measurements still depend on actual source coverage and quality.'));
  }
  show(initial);
  return new Promise((resolve,reject)=>{
    function invalidate(error) {invalid=true;start.disabled=true;view.disabled=true;summary.replaceChildren(el('p','Previous readiness counts are no longer current. Review the current mission before starting.'));feedback.textContent=error.message;}
    async function refresh() {
      if (closed || busy || checking || invalid) return;
      checking=true;const request=++generation;
      try {const value=await context();if (closed||busy||request!==generation) return;if (!sameRevision(initial,value)) {invalidate(new Error('The saved plan revision changed. Continue setup and review the updated plan.'));return;}show(value);}
      catch(error) {if (!closed&&!busy&&request===generation) {summary.replaceChildren(el('p','Current external data counts are Unknown. The previous summary is no longer current.'));invalidate(error);}}
      finally {checking=false;if(!closed&&!invalid&&!busy)timer=setTimeout(refresh,2500);}
    }
    const start=button('Start Mission Anyway',async()=>{
      if (busy || invalid || closed) return;busy=true;++generation;clearTimeout(timer);start.disabled=true;view.disabled=true;
      try {
        const value=await context();if(closed)return;
        if(!sameRevision(initial,value)){invalidate(new Error('The saved plan revision changed. Continue setup and review the updated plan.'));return;}
        show(value);submitting=true;feedback.textContent='Starting mission using the normal recording checks. Closing this window does not cancel an already confirmed start.';
        const mission=await submit(value);dialog.close();resolve(mission);
      } catch(error) {if(!closed){feedback.textContent=error.message;submitting=false;}else reject(error);}
      finally {busy=false;if(!closed&&!invalid){start.disabled=false;view.disabled=false;clearTimeout(timer);timer=setTimeout(refresh,2500);}}
    });
    const view=button('View Data Preparation',async()=>{if(busy||invalid)return;busy=true;++generation;clearTimeout(timer);try{const value=await context();if(closed)return;if(!sameRevision(initial,value)){invalidate(new Error('The saved plan revision changed. Continue setup and review the updated plan.'));return;}dialog.close();location.hash='#downloads?plan='+encodeURIComponent(launch.planId);}catch(error){if(!closed)invalidate(error);}finally{busy=false;}});
    button('Continue Setup',()=>dialog.close());
    dialog.addEventListener('close',()=>{closed=true;clearTimeout(timer);dialog.remove();if(!submitting)resolve(null);});
    document.body.append(dialog);dialogHeader(dialog,{title:'Some Scientific Data Is Not Ready',opener});dialog.showModal();timer=setTimeout(refresh,2500);
  });
}
