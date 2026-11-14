import './organization-appearance.js';
import {contextualHelp,helpGroups} from './contextual-help.js';
const root=document.getElementById('workspace');
for(const group of Object.keys(helpGroups)){const section=document.createElement('section');root.append(section);const help=contextualHelp(section,group,{guideHref:null});help.open=true;}
