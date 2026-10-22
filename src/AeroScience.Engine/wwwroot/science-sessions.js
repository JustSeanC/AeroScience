import {missionIntelligence} from './mission-intelligence.js';
export function initializeScienceSessions({api},parent,options={}){return missionIntelligence(parent,api,{id:'science-sessions',after:true,...options});}
