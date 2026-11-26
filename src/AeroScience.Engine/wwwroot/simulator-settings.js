import {message as copy,setPresentationText,t} from './i18n.js';
import {node,formField,status} from './workstation-ui.js';

const subscribers=new Set();
let api,host,error='',timer,pending,generation=0;
export const simulatorName=id=>t(id==='xplane12'?'simulator.xplane12':id==='msfs2024'?'simulator.msfs2024':'common.unavailable');
export function recordingAllowed(snapshot){return snapshot?.simulator?.productionRecordingReady!==false;}
export function simulatorStatus(view){return copy(view.statusKey,{name:simulatorName(view.effective.id)});}
export function connectionStatus(snapshot){
  const source=snapshot?.simulator;
  if(!source)return snapshot?.connection?.message||'';
  const reason=snapshot.connection?.message;
  const key=reason==='ApiAccessDenied'?'simulator.accessDenied':reason==='UnsupportedApiVersion'?'simulator.unsupportedApi'
    :reason==='SimulatorSourceMismatch'?'simulator.sourceMismatch':snapshot.connection?.state==='connected'&&snapshot.telemetry?'simulator.readOnlyConnected'
    :reason?.includes('Stale')||reason==='SnapshotSpanExceeded'?'simulator.stale':snapshot.connection?.state==='unavailable'?'simulator.invalidApi':snapshot.connection?.state==='disconnected'?'simulator.waiting':'simulator.waitingData';
  return copy(key,{name:simulatorName(source.id)});
}
async function refresh(){
  if(!api)return;
  if(pending)return pending;
  const own=generation;
  pending=(async()=>{
    try{const value=await api('/api/settings/simulator');if(own!==generation)return;if(value.version!==1||!['msfs2024','xplane12'].includes(value.effective?.id))throw Error(t('simulator.invalidSaved'));host=value;error='';}
    catch(e){if(own===generation)error=e.message;}
    finally{pending=null;for(const update of subscribers)update();}
  })();return pending;
}
export async function connectSimulatorHost(client){
  api=client;await refresh();
  if(!timer){timer=setInterval(()=>{if(!document.hidden)refresh();},10000);window.addEventListener('focus',refresh);}
}
export async function simulatorControls(parent){
  const root=node('section',undefined,'simulator-settings');root.setAttribute('aria-labelledby','simulator-settings-title');
  const title=node('h2',copy('simulator.title'));title.id='simulator-settings-title';
  const fields=node('div',undefined,'presentation-fields'),select=node('select');select.id='simulator-choice';
  for(const id of ['msfs2024','xplane12']){const option=node('option',simulatorName(id));option.value=id;select.append(option);}
  const active=node('p'),saved=node('p'),restart=node('p',copy('simulator.restart'));
  restart.hidden=true;root.append(title,fields,active,saved,restart);formField(fields,copy('simulator.title'),select);
  const advanced=node('details'),summary=node('summary',copy('simulator.advanced')),port=node('input');port.type='number';port.required=true;port.min='1';port.max='65535';port.step='1';port.id='simulator-port';port.inputMode='numeric';
  advanced.append(summary);formField(advanced,copy('simulator.port'),port);advanced.append(node('p',copy('simulator.localOnly')));
  const diagnostic=node('p');advanced.append(diagnostic);root.append(advanced);
  const save=node('button',copy('simulator.save')),reload=node('button',copy('simulator.reload'));save.type=reload.type='button';root.append(save,reload);
  const feedback=status(root,'');root.append(node('p',copy('simulator.offline')));parent.prepend(root);
  let draft=false,busy=false,revision=null,instance=null;
  select.onchange=port.oninput=()=>{draft=true;};
  function draw(){
    if(!root.isConnected){subscribers.delete(draw);return;}
    if(host){
      if(instance&&instance!==host.instanceId){draft=false;error=t('simulator.changed');}
      instance=host.instanceId;
      if(!draft&&host.preferences){select.value=host.preferences.id;port.value=host.preferences.xPlanePort;revision=host.preferences.revision;}
      setPresentationText(active,copy('simulator.active',{name:simulatorName(host.effective.id)}));
      setPresentationText(saved,host.preferences?copy('simulator.saved',{name:simulatorName(host.preferences.id)}):copy('simulator.invalidSaved'));
      restart.hidden=!host.restartRequired;setPresentationText(diagnostic,host.technicalReason||'');
    }
    select.disabled=port.disabled=save.disabled=busy||!host?.canWrite||!!error;
    setPresentationText(feedback,error||host?.warning||(!host?copy('simulator.invalidApi'):!host.canWrite?copy('simulator.localOwner'):simulatorStatus(host)));
  }
  save.onclick=async()=>{
    if(busy||!host?.canWrite||!port.checkValidity()) {port.reportValidity();return;}
    const restoreFocus=document.activeElement===save;
    busy=true;++generation;draw();
    try{host=await api('/api/settings/simulator',{expectedRevision:revision,id:select.value,xPlanePort:Number(port.value)});++generation;draft=false;error='';draw();setPresentationText(feedback,copy(host.restartRequired?'simulator.restart':'simulator.savedNotice'));}
    catch(e){setPresentationText(feedback,e.message);feedback.setAttribute('role','alert');}
    finally{busy=false;select.disabled=port.disabled=save.disabled=!host?.canWrite||!!error;
      if(restoreFocus&&!save.disabled&&document.activeElement===document.body)save.focus({preventScroll:true});}
  };
  reload.onclick=async()=>{draft=false;await refresh();};
  subscribers.add(draw);await refresh();draw();
  return {element:root,dispose:()=>subscribers.delete(draw)};
}
