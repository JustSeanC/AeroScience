import {node} from './workstation-ui.js';

export function initializeWorkstationShell(){
  const sidebar=document.querySelector('.sidebar'),main=document.querySelector('main'),header=document.querySelector('.topbar');
  main.id='main-content';main.tabIndex=-1;sidebar.id='app-navigation';sidebar.setAttribute('aria-label','Application navigation');
  const skip=node('a','Skip to main content','skip-link');skip.href='#main-content';skip.onclick=event=>{event.preventDefault();main.focus();};document.body.prepend(skip);
  const toggle=node('button','☰ Menu','navigation-toggle secondary');toggle.type='button';toggle.setAttribute('aria-controls',sidebar.id);toggle.setAttribute('aria-expanded','false');header.prepend(toggle);
  const close=node('button','×','navigation-close popup-close secondary');close.type='button';close.setAttribute('aria-label','Close navigation');sidebar.prepend(close);
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
  const links=document.querySelectorAll('.nav-link');for(const link of links){link.title=link.textContent.trim();}
  return {afterNavigation(parent){if(open||document.activeElement===toggle||sidebar.contains(document.activeElement)){window.dispatchEvent(new Event('resize'));return;}setOpen(false,false);const heading=parent?.querySelector('h1');if(heading){heading.tabIndex=-1;heading.focus({preventScroll:true});}window.dispatchEvent(new Event('resize'));}};
}

export function streamlineMissionControl(){
  const control=document.getElementById('control'),map=document.getElementById('mission-map');
  const details=node('details',undefined,'control-secondary panel');details.id='flight-details';details.append(node('summary','Flight details — attitude, altitude profile and position'));
  for(const child of [control.querySelector('.flight-panel'),control.querySelector('.lower-grid')])if(child)details.append(child);
  control.append(details);
  const tools=node('details',undefined,'control-secondary panel');tools.id='flight-tools';tools.append(node('summary','Flight tools — guidance, environment and observation notes'));
  for(const id of ['flight-guidance','environment-readings','observation-notes']){const child=document.getElementById(id);if(child)tools.append(child);}control.append(tools);
  const telemetry=control.querySelector('.metrics'),label=telemetry?.previousElementSibling;
  if(map&&telemetry){if(label?.classList.contains('section-label'))map.before(label);map.before(telemetry);}
}
