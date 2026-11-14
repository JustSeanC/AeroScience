import {message as localizedCopy,setPresentationText} from './i18n.js';
import {t as textForUI} from './i18n.js';
import {node as el,button} from './workstation-ui.js';
import {dialogHeader} from './dialog-ui.js';
export function acknowledgeEmit(){return new Promise(resolve=>{const d=el('dialog',undefined,'research-panel');setPresentationText(d,localizedCopy("ui.emitReview.messageEMITDevelopmentValidation"),"aria-label");let approved=false;d.append(el('p',localizedCopy("ui.emitReview.messageOnlyExactRFL002Uncertainty002AndCompatibleSevenLayer")));button(d,localizedCopy("ui.emitReview.messageContinueToExactFilePreview"),()=>{approved=true;d.close();});button(d,localizedCopy("lidar.cancel"),()=>d.close());dialogHeader(d,{title:localizedCopy("ui.emitReview.messageEMITDevelopmentValidation")});d.addEventListener('close',()=>{d.remove();resolve(approved);});document.body.append(d);d.showModal();});}
