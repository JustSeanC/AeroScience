import {node,link} from './workstation-ui.js';
import {workspaceDefinitions,workspaceKey,resolveRoute} from './navigation-routes.js';
import {t} from './i18n.js';

// Mount original nodes once. View-specific controllers retain all subscriptions,
// command callbacks and their existing close/dispose/restore lifecycle.
export function taskWorkspaces(){
  for(const group of workspaceDefinitions){
    if(document.getElementById(group.id))continue;
    const section=node('section',undefined,'task-workspace');section.id=group.id;section.hidden=true;
    const heading=node('header',undefined,'page-heading'),title=node('h1',t(group.titleKey));
    title.dataset.i18n=group.titleKey;heading.append(title);
    const nav=node('nav',undefined,'workspace-subnav');nav.setAttribute('aria-label',t('nav.sections',{name:t(group.titleKey)}));
    for(const [key,href] of group.links){const entry=link(nav,t(key),href);entry.dataset.i18n=key;}
    section.append(heading,nav);document.getElementById('workspace').insertBefore(section,document.getElementById(group.pages[0]));
    for(const page of group.pages){let view=document.getElementById(page);
      if(!view){view=node('section',undefined,'view');view.id=page;view.hidden=true;}
      section.append(view);
    }
  }
  return {show(page){
    let current=null;
    for(const group of workspaceDefinitions){
      const section=document.getElementById(group.id),selected=group.pages.includes(page);section.hidden=!selected;
      if(selected)current=section;
      for(const entry of section.querySelectorAll(':scope > nav > a')){
        const active=selected&&resolveRoute(entry.hash).page===page;
        if(active)entry.setAttribute('aria-current','page');else entry.removeAttribute('aria-current');
      }
    }
    return current;
  },key:workspaceKey};
}
