import {node,link} from './workstation-ui.js';

// Reparent existing views; their stores, event streams and controllers stay intact.
export function taskWorkspaces(){
 const groups=[
  ['flight-workspace','Flight Operations',['control','operations','instruments'],[['Current Flight','#operations'],['Instrument Setup','#science'],['Flight Details & Tools','#control']]],
  ['results-workspace','Results & History',['history','science-workspace'],[['Recorded Flights','#results'],['Science Results','#workspace/map']]],
  ['library-workspace','Scientific Library',['library','downloads','library-storage'],[['Data Sources','#library'],['Downloads','#downloads'],['Storage','#library/storage']]]
 ];
 for(const [id,title,views,links]of groups){const group=node('section',undefined,'task-workspace');group.id=id;group.hidden=true;const heading=node('header',undefined,'page-heading');heading.append(node('h1',title));const nav=node('nav',undefined,'workspace-subnav');nav.setAttribute('aria-label',title+' sections');for(const [label,href]of links)link(nav,label,href);group.append(heading,nav);document.getElementById('workspace').insertBefore(group,document.getElementById(views[0]));for(const view of views){let child=document.getElementById(view);if(!child){child=node('section',undefined,'view');child.id=view;child.hidden=true;}group.append(child);}}
 return {show(page){let current=null;for(const [id,title,views]of groups){const group=document.getElementById(id),selected=views.includes(page);group.hidden=!selected;if(selected)current=group;for(const a of group.querySelectorAll(':scope>nav>a')){const target=a.hash.split('?')[0],active=selected&&(target==='#results'?page==='history':target==='#library/storage'?page==='library-storage':target==='#science'?page==='instruments':target.startsWith('#workspace/')?page==='science-workspace':target==='#'+page);if(active)a.setAttribute('aria-current','page');else a.removeAttribute('aria-current');}}return current;},key(page){return ['control','operations','instruments'].includes(page)?'operations':['history','science-workspace'].includes(page)?'results':['library','downloads','library-storage'].includes(page)?'library':page;}};
}
