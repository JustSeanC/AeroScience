import {message as localizedCopy,setPresentationText} from './i18n.js';
import {node,link,disclosure} from './workstation-ui.js';
import {researchContext,renderTemplateCriteria} from './scientific-context.js';
import {t} from './i18n.js';

export const organizationSections=Object.freeze(['mine','available','assignments','campaigns','live','evidence','reviews']);
export function organizationRoute(view='mine',connection=null,hash=location.hash){
 const query=new URLSearchParams(hash.split('?')[1]||'');query.set('section','organizations');query.set('orgView',organizationSections.includes(view)?view:'mine');
 if(connection)query.set('orgConnection',connection);else query.delete('orgConnection');
 return '#my-research?'+query;
}
export function organizationTaskNavigation(parent,view,connection){
 const nav=node('nav',undefined,'workspace-tabs organization-task-navigation');setPresentationText(nav,localizedCopy('organizations.navigation'),"aria-label");
 for(const id of organizationSections){const a=link(nav,localizedCopy('organizations.section.'+id),organizationRoute(id,connection),'');if(id===view)a.setAttribute('aria-current','page');}
 parent.append(nav);return nav;
}
// Read the frozen published/issued definition. This is a requirements explanation,
// never a readiness assessment, aircraft approval or a second card evaluator.
export function assignmentRequirements(parent,card){
 const box=node('section',undefined,'assignment-requirements');setPresentationText(box,localizedCopy('organizations.requirements'),"aria-label");
 const d=card.definition;if(!d||d.version!==1){box.append(node('p',localizedCopy('organizations.unsupportedDefinition')));parent.append(box);return;}
 const context=researchContext({...d,templateId:d.template?.id,name:card.name});
 const list=node('dl');
 for(const [key,text]of [['aircraft',t('organizations.aircraftUnspecified')],['measurements',context.variables],['method',context.method],['evidence',context.output],['quality',context.quality]]){
  if(!text)continue;list.append(node('dt',localizedCopy('organizations.requirement.'+key)),node('dd',text));
 }
 box.append(list);renderTemplateCriteria(box,d);
 if(card.instructions)box.append(node('p',card.instructions));
 if(context.limitation)box.append(node('p',context.limitation,'metadata'));
 disclosure(box,localizedCopy('organizations.reviewPolicy'),card.policy.reviewPolicy);
 parent.append(box);
}
