import {message as localizedCopy,setPresentationText} from './i18n.js';
import {t as textForUI} from './i18n.js';
import {node,bytes} from './workstation-ui.js';
const known=value=>Number.isFinite(value)&&value>=0;
export function storageSummary(parent,snapshot){
 const grid=node('div',undefined,'storage-summary');parent.append(grid);
 for(const [key,title] of [['flight',textForUI("ui.storageSummary.messageFlightRecordings")],['science',textForUI("results.library")]]){
  const value=snapshot?.[key],box=node('section');setPresentationText(box,localizedCopy("ui.storageSummary.messageStorage",{value1:(title)}),"aria-label");box.append(node('h3',title));grid.append(box);
  if(!value){box.append(node('p',localizedCopy("ui.storageSummary.messageStorageStatusUnknownCapacityCouldNotBe"),'warning'));continue;}
  const cap=value.limitBytes,used=value.usedBytes;
  box.append(node('p',localizedCopy("ui.storageSummary.messageUsedApplicationQuota",{value1:(String(known(used)?bytes(used):'Unknown')),value2:(String(cap===null?'Unlimited':known(cap)?bytes(cap):'Unknown'))})));
  const quotaFree=known(cap)&&known(used)?Math.max(0,cap-used-(value.reservedBytes||0)-(value.pendingBytes||0)):null;
  box.append(node('p',localizedCopy("ui.storageSummary.messageRemainingApplicationQuotaAfterPendingReservedWork",{value1:(String(cap===null?'Unlimited':quotaFree==null?'Unknown':bytes(quotaFree)))})));
  box.append(node('p',localizedCopy("ui.storageSummary.messagePhysicalDriveFreeSpaceAnUnlimited",{value1:(String(value.available&&known(value.freeBytes)?bytes(value.freeBytes):'Unknown'))})));
  if(known(cap)&&cap>0&&known(used)&&value.available){const meter=node('progress');meter.max=cap;meter.value=Math.min(cap,used);setPresentationText(meter,localizedCopy("ui.storageSummary.messageApplicationQuotaUsed",{value1:(title)}),"aria-label");box.append(meter,node('p',localizedCopy("ui.storageSummary.messageOfApplicationQuotaUsedPhysical",{value1:(String(Math.round(100*used/cap)))})));}
  if(value.pendingBytes||value.pendingOperations||value.reservedBytes)box.append(node('p',localizedCopy("ui.storageSummary.messagePendingOperationsReserved",{value1:(String(bytes(value.pendingBytes??0))),value2:(String(value.pendingOperations??0)),value3:(String(bytes(value.reservedBytes??0)))})));
  const warning=!value.available||value.status!=='Normal'||value.error||quotaFree===0;
  box.append(node('p',`${!value.available?localizedCopy("ui.storageSummary.messageStorageDriveUnavailable"):value.status||localizedCopy("ui.storageSummary.messageStatusUnknown")}${value.error?': '+value.error:''}`,warning?'warning':''));
  if(!value.available)box.append(node('p',localizedCopy("ui.storageSummary.messageRestoreTheSelectedDriveOrCorrectIts"),'warning'));
 }
 return grid;
}
