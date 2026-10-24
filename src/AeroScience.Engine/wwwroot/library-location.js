import {node as el, bytes, button, status} from './workstation-ui.js';

async function copyPath(path, feedback) {
  try {
    await navigator.clipboard.writeText(path);
    feedback.textContent = 'Storage path copied. Opening it does not import, move or modify scientific data.';
  } catch {
    feedback.textContent = 'Copy is unavailable in this browser. Select and copy the displayed storage path.';
  }
}

export async function libraryLocation(parent, api, {sourceId, stopped=()=>false}={}) {
  const panel=el('section',undefined,'library-location'), path=el('p'), info=status(panel,'Reading configured scientific storage…');
  panel.setAttribute('aria-label',sourceId?'Retained source folder':'Scientific Library location');
  info.setAttribute('role','note');info.setAttribute('aria-label','Configured scientific storage');
  path.className='storage-path';panel.prepend(path);parent.append(panel);
  try {
    const data=await api('/api/science/library/location'+(sourceId?'?sourceId='+encodeURIComponent(sourceId):''));
    if(stopped()||!panel.isConnected)return;
    path.textContent=data.path;path.style.overflowWrap='anywhere';path.tabIndex=0;
    info.textContent=data.available?`${bytes(data.usedBytes)} total Scientific Library usage · ${data.freeBytes==null?'Unknown':bytes(data.freeBytes)} free on the physical drive. ${data.accounting}`:`Storage unavailable: ${data.error} No replacement folder was created.`;
    const actions=el('div',undefined,'action-row');panel.append(actions);
    const open=button(actions,sourceId?'Open Source Folder':'Open Data Folder',async()=>{
      open.disabled=true;
      try { await api('/api/science/library/open-folder',{libraryId:data.libraryId,sourceId:sourceId||null});info.textContent='Requested Windows File Explorer at the verified configured location. No data was changed.'; }
      catch(error){info.textContent=error.message;}
      finally {open.disabled=!data.canOpen||!data.available;}
    },'secondary');
    open.disabled=!data.canOpen||!data.available;
    button(actions,'Copy Path',()=>copyPath(data.path,info),'secondary');
    if(!data.canOpen)panel.append(el('p','Paired devices can copy this Windows path. They cannot open File Explorer on the companion PC.'));
  } catch(error) {info.textContent=error.message+' No replacement folder was created.';}
  return ()=>panel.remove();
}
