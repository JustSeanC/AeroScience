import {message as localizedCopy,setPresentationText} from './i18n.js';
import {t as textForUI} from './i18n.js';
import {node as el, bytes, button, status,informationDetails,scientificNotice} from './workstation-ui.js';

async function copyPath(path, feedback) {
  try {
    await navigator.clipboard.writeText(path);
    setPresentationText(feedback,localizedCopy("ui.libraryLocation.messageStoragePathCopiedOpeningItDoesNot"));
  } catch {
    setPresentationText(feedback,localizedCopy("ui.libraryLocation.messageCopyIsUnavailableInThisBrowserSelect"));
  }
}

export async function libraryLocation(parent, api, {sourceId, stopped=()=>false}={}) {
  const panel=el('section',undefined,'library-location'), path=el('p'), info=status(panel,localizedCopy("ui.libraryLocation.messageReadingConfiguredScientificStorage"));
  setPresentationText(panel,sourceId?localizedCopy("ui.libraryLocation.messageRetainedSourceFolder"):localizedCopy("ui.libraryLocation.messageScientificLibraryLocation"),"aria-label");
  info.setAttribute('role','note');setPresentationText(info,localizedCopy("ui.libraryLocation.messageConfiguredScientificStorage"),"aria-label");
  path.className='storage-path';const context=sourceId?panel:informationDetails(panel,localizedCopy("ui.libraryLocation.messageStorageLocationAccounting"));context.prepend(path);parent.append(panel);
  try {
    const data=await api('/api/science/library/location'+(sourceId?'?sourceId='+encodeURIComponent(sourceId):''));
    if(stopped()||!panel.isConnected)return;
    setPresentationText(path,data.path);path.style.overflowWrap='anywhere';path.tabIndex=0;
    setPresentationText(info,data.available?localizedCopy("ui.libraryLocation.messageTotalScientificLibraryUsageFree",{value1:(String(bytes(data.usedBytes))),value2:(String(data.freeBytes==null?'Unknown':bytes(data.freeBytes)))}):localizedCopy("ui.libraryLocation.messageStorageUnavailableNoReplacementFolderWasCreated",{value1:(String(data.error))}));
    if(data.available&&data.accounting)context.append(el('p',data.accounting));if(!data.available)scientificNotice(info,'critical',{problem:localizedCopy("ui.libraryLocation.messageScientificLibraryStorageIsUnavailable"),consequence:localizedCopy("ui.libraryLocation.messageOriginalSourcesCannotBeRead"),action:localizedCopy("ui.libraryLocation.messageOpenScientificLibraryStorageAndReconnect")});
    const actions=el('div',undefined,'action-row');context.append(actions);
    const open=button(actions,sourceId?localizedCopy("ui.libraryLocation.messageOpenSourceFolder"):localizedCopy("ui.libraryLocation.messageOpenDataFolder"),async()=>{
      open.disabled=true;
      try { await api('/api/science/library/open-folder',{libraryId:data.libraryId,sourceId:sourceId||null});setPresentationText(info,localizedCopy("ui.libraryLocation.messageRequestedWindowsFileExplorerAtTheVerified")); }
      catch(error){setPresentationText(info,error.message);}
      finally {open.disabled=!data.canOpen||!data.available;}
    },'secondary');
    open.disabled=!data.canOpen||!data.available;
    button(actions,localizedCopy("ui.libraryLocation.messageCopyPath"),()=>copyPath(data.path,info),'secondary');
    if(!data.canOpen)context.append(el('p',localizedCopy("ui.libraryLocation.messagePairedDevicesCanCopyThisWindowsPath")));
  } catch(error) {setPresentationText(info,localizedCopy("ui.libraryLocation.messageNoReplacementFolderWasCreated",{value1:(String(error.message))}));}
  return ()=>panel.remove();
}
