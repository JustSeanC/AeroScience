import {node,link,button,status} from './workstation-ui.js';
import {homePresentation} from './home-state.js';
import {t} from './i18n.js';

// Owns only bounded GETs while visible. Recording/science/selection controllers
// remain in their original views. The application's single SSE supplies identity.
export function homeDashboard(root,api,{snapshot,draft}) {
  let disposed=false, busy=false, controller, planController, generation=0, selectionId, planGeneration=0;
  let data={archive:[],archiveStatus:'loading',planStatus:'loading'}, historyAt=0, historyKey='', pollAt=0;
  root.replaceChildren();
  const label=(tag,key,className)=>{const value=node(tag,t(key),className);value.dataset.i18n=key;return value;};
  const heading=node('header',undefined,'page-heading'), title=node('h1',t('nav.home'));
  title.dataset.i18n='nav.home';heading.append(title,label('p','home.intro','subtitle'));root.append(heading);
  const grid=node('div',undefined,'home-grid');root.append(grid);
  const hero=node('section',undefined,'panel home-next');hero.setAttribute('aria-labelledby','home-next-title');
  const step=label('p','home.next','eyebrow'), nextTitle=node('h2');nextTitle.id='home-next-title';
  const detail=node('p'),primary=link(hero,'','#missions','button primary');
  hero.prepend(step,nextTitle,detail);grid.append(hero);
  const health=node('section',undefined,'panel home-health');health.setAttribute('aria-labelledby','home-status-title');
  const statusTitle=label('h2','home.status');statusTitle.id='home-status-title';health.append(statusTitle);
  const connection=status(health),source=node('p',undefined,'home-source'),identity=node('p');health.append(source,identity);
  const facts=node('dl',undefined,'home-facts');health.append(facts);
  const fact=key=>{const row=node('div'),term=label('dt',key),value=node('dd');row.append(term,value);facts.append(row);return value;};
  const recording=fact('home.recording'),science=fact('home.science');
  const saved=node('p');health.append(saved);grid.append(health);
  const warning=status(root);warning.classList.add('home-warning');root.insertBefore(warning,grid);
  const preparation=node('section',undefined,'panel home-preparation');preparation.append(label('h2','home.preparation'));
  const prepared=node('p'),draftText=node('p');preparation.append(prepared,draftText);link(preparation,t('home.choose'),'#missions').dataset.i18n='home.choose';root.append(preparation);
  const results=node('section',undefined,'panel home-results');results.setAttribute('aria-labelledby','home-results-title');
  const recentTitle=label('h2','home.recent');recentTitle.id='home-results-title';results.append(recentTitle,label('p','home.resultsMeaning'));
  const archiveStatus=status(results),list=node('ul',undefined,'home-recent');results.append(list);link(results,t('home.allResults'),'#results').dataset.i18n='home.allResults';root.append(results);
  const tools=node('nav',undefined,'home-tools');tools.setAttribute('aria-label',t('home.tools'));
  tools.dataset.i18nAriaLabel='home.tools';
  link(tools,t('home.organizations'),'#organizations','button tertiary').dataset.i18n='home.organizations';link(tools,t('nav.library'),'#library','button tertiary').dataset.i18n='nav.library';
  const refreshButton=button(tools,t('home.refresh'),()=>refresh(true),'tertiary');refreshButton.dataset.i18n='home.refresh';root.append(tools);
  const active=()=>!disposed && !!root.offsetParent && !document.hidden;
  function paint(){
    if(!active())return;
    const model=homePresentation({...data,archiveStatus:data.archiveStatus==='available' && Date.now()-historyAt>12000 ? 'unavailable' : data.archiveStatus,snapshot:snapshot(),draft:draft()});
    root.dataset.homeState=model.next;
    nextTitle.textContent=t('home.nextTitle.'+model.next);detail.textContent=t('home.nextDetail.'+model.next);
    primary.textContent=t('home.action.'+model.next);
    primary.href={active:'#flight',draft:'#planner',plan:'#operations',idle:'#missions',connect:'#help?guide=v0.4.1-windows-testing.md',unavailable:'#help?guide=v0.4.1-windows-testing.md'}[model.next];
    connection.textContent=t('home.connection.'+model.connection);
    source.textContent=t(model.source==='simulated' ? 'home.synthetic' : model.source==='simulator' ? 'home.simulator' : 'home.sourceUnknown');
    identity.textContent=model.activeFlight ? t('home.activeName',{name:model.activeFlight.name}) : model.aircraft || '';
    recording.textContent=t('recording.'+model.recording);
    science.textContent=t('home.science.'+model.science);
    saved.textContent=model.counts ? t('home.retained',{original:model.counts.originalObservations,derived:model.counts.derivedObservations,products:model.counts.remoteProducts})
      : t(model.recording==='inactive' ? 'home.noActiveCounts' : 'home.countsUnknown');
    const warnings=[];
    if(model.recordingStop)warnings.push(t('home.recordingStopped',{code:model.recordingStop.code,message:model.recordingStop.message}));
    if(!model.fresh)warnings.push(t('home.stale'));
    else if(model.activeFlight && model.connection!=='connected')warnings.push(t('home.telemetryWarning'));
    if(model.collectionIssue)warnings.push(t('home.collectionWarning'));
    if(model.planUnavailable)warnings.push(t('home.planUnavailable'));
    warning.textContent=warnings.join(' ');warning.hidden=!warnings.length;
    prepared.textContent=model.selectedPlan ? t('home.selectedName',{name:model.selectedPlan.name})
      : model.selectionId ? t(model.planUnavailable ? 'home.planUnavailable' : 'home.planLoading') : t('home.noPlan');
    draftText.textContent=model.draft ? t('home.draftName',{name:model.draft.name || t('home.unnamedDraft')}) : '';
    draftText.hidden=!model.draft;
    archiveStatus.textContent=t(model.archiveStatus==='available' ? model.results.length ? 'home.archiveAvailable' : 'home.archiveEmpty'
      : model.archiveStatus==='loading' ? 'common.loadingSavedResults' : 'home.archiveUnavailable');
    const key=JSON.stringify(model.results);
    if(key!==historyKey){
      historyKey=key;list.replaceChildren();
      for(const row of model.results.slice(0,3)){
        const item=node('li'),info=node('div');info.append(node('h3',row.name),node('p',t('home.resultStatus.'+row.status)));
        const date=node('time');date.dateTime=row.startedUtc || '';date.textContent=Number.isFinite(Date.parse(row.startedUtc)) ? new Date(row.startedUtc).toLocaleString() : t('common.unknown');info.append(date);item.append(info);
        link(item,t('home.reviewNamed',{name:row.name}),'#operations?flight='+encodeURIComponent(row.id));list.append(item);
      }
    }
  }
  async function loadPlan(){
    const current=snapshot(),id=(current ? current.selectedPlanId : data.intelligence?.flight?.selectedPlanId) || null;
    if(selectionId===id)return;
    selectionId=id;const own=++planGeneration;planController?.abort();planController=new AbortController();data.plan=null;data.planStatus=id ? 'loading' : 'available';paint();
    if(!id)return;
    const readController=planController,timeout=setTimeout(()=>readController.abort(),6000),signal=readController.signal;
    try{const plan=await api('/api/plans/'+encodeURIComponent(id),undefined,{signal});
      if(disposed || own!==planGeneration)return;
      if(plan?.id!==id)throw Error('Mismatched plan.');data.plan=plan;data.planStatus='available';
    }catch{if(!disposed && own===planGeneration)data.planStatus='unavailable';}
    finally{clearTimeout(timeout);}
    paint();
  }
  async function refresh(force=false){
    if(!active() || busy || !force && Date.now()-pollAt<2000)return;
    if(force && data.planStatus==='unavailable'){selectionId=undefined;loadPlan();}
    busy=true;pollAt=Date.now();const own=++generation;controller=new AbortController();
    const readController=controller,timeout=setTimeout(()=>readController.abort(),6000);
    refreshButton.setAttribute('aria-busy','true');
    const valid=()=>!disposed && own===generation && active();
    const intelligence=api('/api/mission-intelligence',undefined,{signal:controller.signal}).then(value=>{
      if(valid()){data.intelligence=value;data.failed=false;loadPlan();paint();}
    }).catch(()=>{if(valid()){data.failed=true;paint();}});
    const history=force || Date.now()-historyAt>10000 ? api('/api/missions?limit=4&offset=0&sort=newest',undefined,{signal:controller.signal}).then(value=>{
      if(!Array.isArray(value))throw Error('Invalid archive response.');
      if(valid()){data.archive=value;data.archiveStatus='available';historyAt=Date.now();paint();}
    }).catch(()=>{if(valid()){data.archiveStatus='unavailable';data.archive=[];paint();}}) : Promise.resolve();
    await Promise.allSettled([intelligence,history]);
    clearTimeout(timeout);
    if(own===generation){busy=false;refreshButton.removeAttribute('aria-busy');}
  }
  function visibility(){
    if(document.hidden){generation++;controller?.abort();planController?.abort();busy=false;data.failed=true;data.archiveStatus='unavailable';data.archive=[];}
    else {paint();refresh(true);}
  }
  document.addEventListener('visibilitychange',visibility);
  paint();refresh(true);
  const timer=setInterval(()=>{paint();refresh();},1000);
  return {update(){if(active()){loadPlan();paint();}},dispose(){disposed=true;generation++;planGeneration++;controller?.abort();planController?.abort();clearInterval(timer);
    document.removeEventListener('visibilitychange',visibility);}};
}
