import {initializePresentation,presentationControls} from './presentation-settings.js';
// Reuse browser-only preferences. No companion settings API or organization write.
initializePresentation();
const parent=document.getElementById('appearance');
if(parent)presentationControls(parent,{includeHost:false});
