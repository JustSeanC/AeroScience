import {message as localizedCopy,setPresentationText} from './i18n.js';
import {node} from './workstation-ui.js';
import {t as textForUI,t} from './i18n.js';

export function initializeWorkstationShell(){
  const sidebar=document.querySelector('.sidebar'),main=document.querySelector('main'),header=document.querySelector('.topbar');
  main.id='main-content';main.tabIndex=-1;sidebar.id='app-navigation';setPresentationText(sidebar,localizedCopy('shell.applicationNavigation'),"aria-label");sidebar.dataset.i18nAriaLabel='shell.applicationNavigation';
  const skip=node('a',localizedCopy('shell.skip'),'skip-link');skip.dataset.i18n='shell.skip';skip.href='#main-content';skip.onclick=event=>{event.preventDefault();main.focus();};document.body.prepend(skip);
  const toggle=node('button',localizedCopy('shell.menu'),'navigation-toggle secondary');toggle.dataset.i18n='shell.menu';toggle.type='button';toggle.setAttribute('aria-controls',sidebar.id);toggle.setAttribute('aria-expanded','false');header.prepend(toggle);
  const close=node('button','×','navigation-close popup-close secondary');close.type='button';setPresentationText(close,localizedCopy('shell.closeNavigation'),"aria-label");close.dataset.i18nAriaLabel='shell.closeNavigation';sidebar.prepend(close);
  const backdrop=node('div',undefined,'navigation-backdrop');backdrop.hidden=true;document.body.append(backdrop);
  const compact=matchMedia('(max-width:1100px)');let open=false;
  function setOpen(value,restore=true){
    open=compact.matches&&value;document.body.classList.toggle('navigation-open',open);toggle.setAttribute('aria-expanded',String(open));backdrop.hidden=!open;
    sidebar.inert=compact.matches&&!open;main.inert=open;
    if(open){sidebar.setAttribute('role','dialog');sidebar.setAttribute('aria-modal','true');close.focus();}
    else{sidebar.removeAttribute('role');sidebar.removeAttribute('aria-modal');if(restore&&compact.matches)toggle.focus();}
  }
  toggle.onclick=()=>setOpen(!open);close.onclick=()=>setOpen(false);backdrop.onclick=()=>setOpen(false);
  sidebar.addEventListener('click',event=>{if(event.target.closest('a')&&compact.matches)setOpen(false,false);});
  sidebar.addEventListener('keydown',event=>{
    if(!open)return;if(event.key==='Escape'){event.preventDefault();setOpen(false);}
    if(event.key==='Tab'){const elements=[...sidebar.querySelectorAll('a[href],button')].filter(element=>element.getClientRects().length&&!element.disabled);const first=elements[0],last=elements.at(-1);
      if(event.shiftKey&&document.activeElement===first){event.preventDefault();last.focus();}else if(!event.shiftKey&&document.activeElement===last){event.preventDefault();first.focus();}}
  });
  window.addEventListener('hashchange',()=>setOpen(false,false));
  compact.addEventListener('change',()=>setOpen(false,false));setOpen(false,false);
  const links=document.querySelectorAll('.nav-link');for(const link of links){setPresentationText(link,link.textContent.trim(),"title",true);}
  return {afterNavigation(parent){if(open||document.activeElement===toggle||parent?.contains(document.activeElement)){window.dispatchEvent(new Event('resize'));return;}setOpen(false,false);const heading=parent?.querySelector('h1,h2');if(heading){heading.tabIndex=-1;heading.focus({preventScroll:true});}window.dispatchEvent(new Event('resize'));}};
}

export function streamlineMissionControl(){
  const control=document.getElementById('control'),map=document.getElementById('mission-map');
  const details=node('details',undefined,'control-secondary panel');details.id='flight-details';details.append(node('summary',localizedCopy("ui.workstationShell.messageFlightDetailsAttitudeAltitudeProfileAnd")));
  for(const child of [control.querySelector('.flight-panel'),control.querySelector('.lower-grid')])if(child)details.append(child);
  control.append(details);
  const tools=node('details',undefined,'control-secondary panel');tools.id='flight-tools';tools.append(node('summary',localizedCopy("ui.workstationShell.messageFlightToolsPilotObservationsGuidanceAnd")));
  for(const id of ['flight-guidance','environment-readings','observation-notes']){const child=document.getElementById(id);if(child)tools.append(child);}control.append(tools);
  const telemetry=control.querySelector('.metrics'),label=telemetry?.previousElementSibling;
  if(map&&telemetry){if(label?.classList.contains('section-label'))map.before(label);map.before(telemetry);}
}
