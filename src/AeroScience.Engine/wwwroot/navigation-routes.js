// Navigation is presentation only. All aliases mount the original controllers.
export const viewPages = Object.freeze(['home', 'missions-explorer', 'mission-preparation', 'control', 'operations', 'planner', 'history', 'instruments',
  'settings', 'science-workspace', 'library', 'help', 'downloads', 'my-research', 'library-storage']);

export const workspaceDefinitions = Object.freeze([
  {id:'missions-workspace', titleKey:'nav.missions', pages:['my-research','planner','missions-explorer','mission-preparation'], mountBefore:'planner', key:'missions',
    links:[['missions.explore','#missions'],['nav.myResearch','#my-research'],['nav.planner','#missions/plans'],['nav.organizations','#organizations']]},
  {id:'flight-workspace', titleKey:'nav.flightOperations', pages:['control','operations','instruments'], key:'operations',
    links:[['nav.currentFlight','#operations'],['nav.instrumentSetup','#science'],['nav.flightDetails','#control'],['nav.planner','#planner']]},
  {id:'results-workspace', titleKey:'nav.results', pages:['history','science-workspace'], key:'results',
    links:[['nav.recordedFlights','#results'],['nav.scienceResults','#workspace/map']]},
  {id:'library-workspace', titleKey:'nav.library', pages:['library','downloads','library-storage'], key:'library',
    links:[['nav.dataSources','#library'],['nav.downloads','#downloads'],['nav.storage','#library/storage']]}
].map(group => Object.freeze({...group, pages:Object.freeze(group.pages),
  links:Object.freeze(group.links.map(link => Object.freeze(link)))})));

export function splitFragment(hash = '') {
  const value = hash.replace(/^#/, ''), question = value.indexOf('?');
  return {path:question < 0 ? value : value.slice(0,question),
    search:question < 0 ? '' : value.slice(question+1)};
}
const withSearch = (path, search) => '#'+path+(search ? '?'+search : '');
const aliases = Object.freeze({
  'missions/plans':'planner', 'missions/templates':'my-research',
  flight:'operations', 'flight/details':'control', 'flight/instruments':'instruments',
  'results/overview':'workspace/analysis', 'results/explore/map':'workspace/map',
  'results/explore/compare':'workspace/compare', 'results/explore/replay':'workspace/replay',
  'settings/support':'help'
});
export function workspaceKey(page) {
  return workspaceDefinitions.find(group => group.pages.includes(page))?.key || page;
}
export function resolveRoute(hash = '') {
  const {path,search} = splitFragment(hash), query = new URLSearchParams(search);
  let redirect = aliases[path] ? withSearch(aliases[path],search) : null;
  // Preserve legacy continuation workflows; the unqualified Missions entry now
  // has its own complete Explorer. Navigation never authorizes a command.
  if(path==='missions'&&['card','flight','submissionDraft','connectOrganization','permission','researchPlan','section'].some(key=>query.has(key)))redirect=withSearch('my-research',search);
  if(path==='organizations'){query.set('section','organizations');redirect=withSearch('my-research',query.toString());}
  if (path === 'workspace/timeline' || path === 'results/evidence') {
    // Retain every parameter, including the original multi-ID request. The existing
    // timeline displays one exact flight; no multi-flight evidence join is invented.
    const flight = path === 'workspace/timeline' ? query.get('missions')?.split(',')[0]
      : query.get('flight') || query.get('missions')?.split(',')[0];
    if (flight) query.set('flight',flight);
    redirect = withSearch(flight || path === 'workspace/timeline' ? 'operations' : 'history',query.toString());
  }
  const target = redirect ? splitFragment(redirect).path : path, route = target.split('/')[0];
  const page = target==='missions'?'missions-explorer':target==='missions/prepare'?'mission-preparation'
    : route === 'workspace' ? 'science-workspace' : route === 'results' ? 'history'
    : target === 'library/storage' ? 'library-storage' : route === 'science' ? 'instruments'
    : !target ? 'home' : viewPages.includes(route) ? route : 'my-research';
  return Object.freeze({page,path,search,query,redirect,workspaceKey:workspaceKey(page),
    archivedFlight:page === 'operations' ? query.get('flight') : null,
    known:!target || ['missions','missions/prepare'].includes(target) || viewPages.includes(route) || ['workspace','results','science'].includes(route) || !!redirect});
}
