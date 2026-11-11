import {node,link,button,status,errorMessage} from './workstation-ui.js';
import {t} from './i18n.js';
import {researchEntryActions,refreshResearchEntryActions,researchCardTargets} from './personal-research-start.js';
import {renderResearchContext,researchContext,scientificCategory} from './scientific-context.js';
import {surveyFields,surveySourceReview} from './survey-research.js';
import {preflightReadiness} from './preflight-readiness.js';
import {reviewPreflight} from './download-activity.js';
import {experimentPresentation,preparationPresentation} from './mission-discovery.js';

const label=(tag,key,cls)=>{const value=node(tag,t(key),cls);value.dataset.i18n=key;return value;};
function steps(root,selected){
  const nav=node('nav',undefined,'missions-steps');nav.setAttribute('aria-label',t('missions.workflow'));
  const list=node('ol');for(const key of ['explore','prepare','record']){const item=label('li','missions.step.'+key);if(key===selected)item.setAttribute('aria-current','step');list.append(item);}nav.append(list);root.append(nav);
}
function tools(root,draft){
  const nav=node('nav',undefined,'missions-tools');nav.setAttribute('aria-label',t('missions.advanced'));
  for(const [key,href]of [['missions.myResearch','#my-research'],['missions.savedPlans','#missions/plans'],['missions.customPlan','#planner'],['missions.sensors','#science'],['nav.organizations','#organizations']])link(nav,t(key),href);
  if(draft?.())link(nav,t('missions.resumeDraft'),'#planner');root.append(nav);
}
const prepareHref=plan=>'#missions/prepare?researchPlan='+encodeURIComponent(plan.id);

export function missionsExplorer(root,api,{draft}={}){
  let disposed=false,epoch=0,controller,valid=false,entries=[];
  const active=()=>!disposed && !!root.offsetParent;
  root.replaceChildren(label('h2','missions.exploreTitle'),label('p','missions.exploreIntro','subtitle'));steps(root,'explore');
  const selected=node('aside',undefined,'panel missions-selection');root.append(selected);
  const searchLabel=label('label','missions.search'),search=node('input');search.type='search';search.id='missions-search';searchLabel.htmlFor=search.id;
  search.value=new URLSearchParams(location.hash.split('?')[1]||'').get('search')||'';root.append(searchLabel,search);
  const notice=status(root),cards=node('section',undefined,'missions-grid');cards.id='missions-catalog';cards.setAttribute('aria-label',t('missions.catalog'));
  root.append(cards);const saved=node('section',undefined,'panel missions-saved');saved.append(label('h3','missions.savedExperiments'));root.append(saved);tools(root,draft);button(root,t('missions.refresh'),load,'secondary');
  function filter(){
    const query=search.value.toLocaleLowerCase().trim();let count=0;
    for(const entry of entries){entry.element.hidden=!entry.text.includes(query);if(!entry.element.hidden)count++;}
    if(valid)notice.textContent=count?t('missions.catalogCount',{count}):t('missions.noMatches');
  }
  search.oninput=()=>{filter();const query=new URLSearchParams(location.hash.split('?')[1]||'');if(search.value)query.set('search',search.value);else query.delete('search');history.replaceState(null,'','#missions'+(query.size?'?'+query:''));};
  async function load(){
    valid=false;for(const entry of entries)refreshResearchEntryActions(entry.actions);
    const own=++epoch;controller?.abort();controller=new AbortController();const readController=controller,timeout=setTimeout(()=>readController.abort(),6000);
    const read=path=>api(path,undefined,{signal:readController.signal});notice.textContent=t('common.loading');
    try{
      const [starters,templates,definitions,state,personal]=await Promise.all([read('/api/research/starters'),read('/api/templates'),read('/api/instruments'),read('/api/state'),read('/api/research/cards?offset=0').catch(()=>null)]);
      if(!active()||own!==epoch)return;
      if(![starters,templates,definitions].every(Array.isArray))throw Error(t('missions.catalogUnavailable'));
      valid=true;cards.replaceChildren();entries=[];selected.replaceChildren();saved.replaceChildren(label('h3','missions.savedExperiments'));
      if(!Array.isArray(personal))saved.append(label('p','missions.savedUnavailable'));
      else if(!personal.length)saved.append(label('p','missions.noSavedExperiments'));
      else for(const card of personal.slice(0,3)){
        const row=node('article',undefined,'mission-saved-card');row.append(node('h4',card.name),node('p',t('missions.cardRevision',{name:card.name,revision:card.revision})));
        try{row.append(node('p',researchContext(card).question));}catch{row.append(label('p','missions.objectiveUnknown'));}
        link(row,t('missions.reviewExperiment'),'#my-research?card='+encodeURIComponent(card.id));saved.append(row);
      }
      link(saved,t('missions.allExperiments'),'#my-research');
      if(state.activeMission){selected.append(node('p',t('missions.activeFlight',{name:state.activeMission.name})));link(selected,t('nav.currentFlight'),'#flight');}
      let selectionRead=Promise.resolve();
      if(state.selectedPlanId){
        const loading=node('p',t('missions.selectionLoading'));selected.append(loading);
        selectionRead=read('/api/plans/'+encodeURIComponent(state.selectedPlanId)).then(plan=>{
          if(!active()||own!==epoch)return;if(plan?.id!==state.selectedPlanId)throw Error(t('missions.selectionUnavailable'));
          selected.append(node('p',t('missions.selectedPlan',{name:plan.name})));link(selected,t('missions.reviewPreparation'),prepareHref(plan));
          loading.remove();
        }).catch(()=>{if(active()&&own===epoch){loading.textContent=t('missions.selectionUnavailable');}});
      }else if(!state.activeMission)selected.append(label('p','missions.noSelection'));
      for(const starter of starters){
        const article=node('article',undefined,'panel mission-experiment-card');article.setAttribute('aria-label',(starter.name||t('missions.unknown'))+' starter');
        const heading=node('h3',starter.name||t('missions.unknown'));article.append(heading);const summary=node('div'),requirements=node('div');article.append(summary,requirements);
        const options=node('details');options.append(label('summary','missions.requirements'));
        const survey=starter.surveyExample?surveyFields(options,starter.surveyExample,{measurementsOnly:true}):null;const criteria=node('div');options.append(criteria);
        let model;function paint(){
          model=experimentPresentation(starter,templates,definitions,survey?.read()||starter.surveyExample);summary.replaceChildren();requirements.replaceChildren();criteria.replaceChildren();
          if(!model.supported){summary.append(label('p','missions.unsupported'));return;}
          heading.textContent=t('missions.title.'+model.input+(model.input==='atmosphere'?(starter.transectExample?'Transect':'Profile'):''));
          criteria.append(node('p',t('missions.objective',{objective:researchCardTargets(model.card)})));renderResearchContext(criteria,model.card);
          summary.append(node('p',model.context.question,'mission-question'),node('p',t('missions.goal.'+model.input)));
          const facts=node('dl',undefined,'mission-requirements');
          const fact=(key,value)=>facts.append(label('dt',key),node('dd',value));
          fact('missions.experience',t('missions.experience.'+model.experience));fact('missions.aircraftLabel',t('missions.aircraft'));
          fact('missions.inputs',t('missions.input.'+model.input));
          fact('missions.instruments',model.instrumentsKnown?model.instruments.map(d=>d.name).join(', ')||t('missions.noInstruments'):t('missions.instrumentsUnknown'));
          fact('missions.sources',model.external?t('missions.externalSources'):t('missions.localSources'));
          requirements.append(facts,node('p',model.context.categories.map(id=>scientificCategory(id).label).join(' · '),'mission-source'));
          if(!model.instrumentsKnown)requirements.append(label('p','missions.metadataUnavailable'));
          if(model.input==='model')requirements.append(label('p','missions.modelWarning'));
          if(model.input==='notes')requirements.append(label('p','missions.notesWarning'));
        }
        paint();options.append(node('p',t('missions.originalTemplate',{name:starter.name})),node('p',starter.description));
        const boundary=label('p','missions.prepareOnly','mission-boundary');article.append(options,boundary);
        const actions=researchEntryActions(article,api,{starter,survey},()=>active()&&article.isConnected&&valid,
          {prepareHref,canPrepare:()=>valid&&model.supported&&model.instrumentsKnown,navigate:href=>{location.hash=href;window.scrollTo(0,0);}});
        const feedback=actions.nextElementSibling;article.insertBefore(actions,requirements);article.insertBefore(boundary,requirements);if(feedback)article.insertBefore(feedback,requirements);
        options.addEventListener('change',()=>{paint();refreshResearchEntryActions(actions);});
        cards.append(article);entries.push({element:article,actions,text:[starter.name,starter.description,model.context?.question,...(starter.measurements||[])].join(' ').toLocaleLowerCase()});
      }
      filter();if(!starters.length)notice.textContent=t('missions.emptyCatalog');await selectionRead;
    }catch(error){if(active()&&own===epoch){valid=false;notice.textContent=t('missions.catalogUnavailable');errorMessage(root,error,load);}}
    finally{clearTimeout(timeout);}
  }
  load();return {dispose(){disposed=true;epoch++;controller?.abort();}};
}

export function missionPreparation(root,api,{snapshot,draft,continueToFlight}){
  const id=new URLSearchParams(location.hash.split('?')[1]||'').get('researchPlan');
  let disposed=false,busy=false,controller,plan=null,planKey='',state=snapshot(),checkedAt=0,readiness,lastStateKey='',first=true,failure='',verified=false,instrumentDefinitions=null;
  const active=()=>!disposed && !!root.offsetParent && !document.hidden;
  root.replaceChildren(label('h2','missions.prepareTitle'),label('p','missions.prepareIntro','subtitle'));steps(root,'prepare');
  const notice=status(root),title=node('h3',t('missions.prepareTitle')),identity=node('p',undefined,'mission-identity'),info=node('section',undefined,'panel mission-preparation-summary');
  info.append(title,identity);const facts=node('div');info.append(facts);root.append(info);
  const readinessRoot=node('section',undefined,'panel mission-readiness');root.append(readinessRoot);
  const actions=node('div',undefined,'action-row'),next=button(actions,t('missions.openControls'),()=>{
    const model=preparationPresentation(state,checkedAt,plan);
    continueToFlight({planId:plan.id,armScience:model.fresh&&model.matched&&!model.active});
  },'primary');next.disabled=true;root.append(label('p','missions.separateScience'),actions);
  const advanced=link(actions,t('missions.customizePlan'),'#planner');link(actions,t('missions.exploreAgain'),'#missions');tools(root,draft);
  function paint(){
    if(!active())return;if(!id){notice.textContent=t('missions.choosePlan');root.dataset.preparationState='unavailable';return;}const model=preparationPresentation(state,checkedAt,plan);
    const key=[model.fresh,model.connection,state?.selectedPlanId,model.active?.id].join('/');
    if(lastStateKey&&key!==lastStateKey)readiness?.invalidate();lastStateKey=key;
    notice.textContent=failure||(!plan?t(first?'common.loading':'missions.planUnavailable'):!model.fresh?t('missions.stateUnavailable')
      :model.active?t('missions.activeFlight',{name:model.active.name}):!model.matched?t('missions.selectionDifferent')
        :t('missions.connection.'+model.connection)+(model.synthetic?' '+t('home.synthetic'):''));
    root.dataset.preparationState=failure?'unavailable':!plan?'unavailable':!model.fresh?'stale':model.active?'active':!model.matched?'unselected':model.connection;
    next.disabled=!plan||!verified;next.textContent=t(model.active?'nav.currentFlight':'missions.openControls');
  }
  async function refresh(){
    if(!active()||busy)return;
    if(!id){first=false;notice.textContent=t('missions.choosePlan');return;}
    busy=true;const startedAt=Date.now();controller=new AbortController();const readController=controller,timeout=setTimeout(()=>readController.abort(),6000);
    const read=path=>api(path,undefined,{signal:readController.signal});
    try{
      const [selected,current,definitions]=await Promise.all([read('/api/state'),read('/api/plans/'+encodeURIComponent(id)),instrumentDefinitions?Promise.resolve(instrumentDefinitions):read('/api/instruments')]);
      if(!active())return;if(!Array.isArray(definitions))throw Error(t('missions.instrumentsUnknown'));instrumentDefinitions=definitions;if(current?.id!==id)throw Error(t('missions.planUnavailable'));
      if(checkedAt<=startedAt){state=selected;checkedAt=Date.now();}
      const key=JSON.stringify(current);
      if(key!==planKey){
        readiness?.invalidate();verified=false;
        const references=current.researchCards||(current.researchCard?[current.researchCard]:[]);
        const cards=await Promise.all(references.map(ref=>read('/api/research/cards/'+encodeURIComponent(ref.id)+'?revision='+encodeURIComponent(ref.revision))));
        if(!active())return;
        if(cards.some((card,index)=>card?.id!==references[index].id||card.revision!==references[index].revision))throw Error(t('missions.cardMismatch'));
        plan=current;planKey=key;title.textContent=plan.name;identity.textContent=t('missions.planIdentity',{id:plan.id});
        advanced.href='#planner?researchPlan='+encodeURIComponent(plan.id);facts.replaceChildren();
        facts.append(node('p',plan.objective||t('missions.objectiveUnknown')),node('p',t('missions.geometry',{waypoints:plan.waypoints?.length||0,areas:plan.surveyAreas?.length||0})),
          node('p',t('missions.savedInstruments',{instruments:(plan.instruments||[]).filter(i=>i.enabled).map(i=>instrumentDefinitions.find(d=>d.id===i.instrumentId)?.name||i.instrumentId).join(', ')||t('missions.noInstruments')})));
        for(const card of cards){
          const section=node('details');section.append(node('summary',t('missions.cardRevision',{name:card.name,revision:card.revision})));renderResearchContext(section,card,{plan});facts.append(section);
          if(card.survey)surveySourceReview(section,api,plan);
        }
        if(!readiness)readiness=preflightReadiness(readinessRoot,()=>plan,api,{id:'mission-preparation-readiness',active,buttonClass:'secondary',saved:true});
        button(facts,t('missions.dataPreparation'),()=>reviewPreflight(api,plan.id),'secondary');
      }
      verified=true;failure='';
    }catch(error){if(active()){checkedAt=0;verified=false;failure=error.message;readiness?.invalidate();}}
    finally{clearTimeout(timeout);busy=false;first=false;paint();}
  }
  const timer=setInterval(()=>{paint();},1000),poll=setInterval(refresh,5000);
  function visibility(){if(document.hidden){checkedAt=0;controller?.abort();readiness?.invalidate();}else refresh();}
  document.addEventListener('visibilitychange',visibility);
  button(root,t('missions.refreshPreparation'),refresh,'secondary');refresh();
  return {update(value){if(disposed)return;state=value;checkedAt=Date.now();paint();},
    stale(){checkedAt=0;readiness?.invalidate();paint();},dispose(){disposed=true;controller?.abort();readiness?.dispose();clearInterval(timer);clearInterval(poll);document.removeEventListener('visibilitychange',visibility);}};
}
