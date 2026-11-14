import {node} from './workstation-ui.js';
import {t} from './i18n.js';

// Navigation only. Every destination retains its existing server authorization.
export function organizationPortalNavigation(parent,session,actions){
 if(!session?.user||session.membershipState!=='active'||session.accountSecurityRequired)return;
 const roles=session.user.roles||[],nav=node('nav');nav.className='organization-task-navigation';nav.setAttribute('aria-label',t('collaboration.portalNavigation'));
 const add=(key,action)=>{const a=node('a',t(key));a.href='#workspace';a.onclick=async e=>{e.preventDefault();try{await action();}catch(error){if(error.name==='AbortError')return;let p=parent.querySelector('.portal-navigation-error');if(!p){p=node('p');p.className='portal-navigation-error';p.setAttribute('role','alert');parent.prepend(p);}p.textContent=error.message||t('error.notConfirmed');}};nav.append(a);};
 add('collaboration.researchOverview',actions.research);
 if(roles.some(r=>['Administrator','Manager','Reviewer'].includes(r)))add('collaboration.reviewEvidence',actions.reviews);
 if(roles.some(r=>['Administrator','Manager'].includes(r)))add('collaboration.managePrograms',actions.programs);
 add('collaboration.liveMonitoring',actions.live);add('collaboration.events',actions.events);
 if(roles.includes('Administrator'))add('collaboration.notificationSettings',actions.notifications);
 parent.append(nav);return nav;
}
