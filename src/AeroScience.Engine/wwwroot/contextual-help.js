import {node,informationDetails} from './workstation-ui.js';
import {t} from './i18n.js';

// Bundled explanations only: no network, storage, consent or command side effects.
export const helpGroups=Object.freeze({
 flight:['recording','quality'],
 assignments:['assignments','evaluation','roles'],
 evidence:['originals','corridor','terrain','lidar','dates','datum','coverage','provenance','submission'],
 sources:['coverage','provenance','downloads','datum'],
 live:['privacy','roles']
});
export function contextualHelp(parent,group,{guideHref='#help?guide=v0.8.0-research-collaboration.md'}={}){
 const topics=helpGroups[group];if(!topics)return null;
 const box=informationDetails(parent,t('contextHelp.group.'+group));box.classList.add('contextual-help');box.dataset.helpGroup=group;
 const dl=node('dl');for(const key of topics)dl.append(node('dt',t('contextHelp.'+key+'.title')),node('dd',t('contextHelp.'+key+'.body')));box.append(dl);
 if(guideHref){const a=node('a',t('contextHelp.guide'));a.href=guideHref;a.target='_blank';a.rel='noopener noreferrer';box.append(a);}
 return box;
}
