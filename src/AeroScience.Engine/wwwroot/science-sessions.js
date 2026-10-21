import {missionIntelligence} from './mission-intelligence.js';
export function initializeScienceSessions({api},parent){return missionIntelligence(parent,api,{id:'science-sessions',after:true});}
