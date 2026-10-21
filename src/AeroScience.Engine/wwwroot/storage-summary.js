import {node,bytes} from './workstation-ui.js';
const known=value=>Number.isFinite(value)&&value>=0;
export function storageSummary(parent,snapshot){
 const grid=node('div',undefined,'storage-summary');parent.append(grid);
 for(const [key,title] of [['flight','Flight recordings'],['science','Scientific Library']]){
  const value=snapshot?.[key],box=node('section');box.setAttribute('aria-label',title+' storage');box.append(node('h3',title));grid.append(box);
  if(!value){box.append(node('p','Storage status: Unknown. Capacity could not be assessed.','warning'));continue;}
  const cap=value.limitBytes,used=value.usedBytes;
  box.append(node('p',`Used: ${known(used)?bytes(used):'Unknown'} · Application quota: ${cap===null?'Unlimited':known(cap)?bytes(cap):'Unknown'}`));
  const quotaFree=known(cap)&&known(used)?Math.max(0,cap-used-(value.reservedBytes||0)-(value.pendingBytes||0)):null;
  box.append(node('p',`Remaining application quota after pending/reserved work: ${cap===null?'Unlimited':quotaFree==null?'Unknown':bytes(quotaFree)}.`));
  box.append(node('p',`Physical drive free space: ${value.available&&known(value.freeBytes)?bytes(value.freeBytes):'Unknown'}. An unlimited application quota does not mean unlimited disk space.`));
  if(known(cap)&&cap>0&&known(used)&&value.available){const meter=node('progress');meter.max=cap;meter.value=Math.min(cap,used);meter.setAttribute('aria-label',title+' application quota used');box.append(meter,node('p',`${Math.round(100*used/cap)}% of application quota used; physical drive capacity is separate.`));}
  if(value.pendingBytes||value.pendingOperations||value.reservedBytes)box.append(node('p',`Pending: ${bytes(value.pendingBytes??0)} (${value.pendingOperations??0} operations) · Reserved: ${bytes(value.reservedBytes??0)}.`));
  const warning=!value.available||value.status!=='Normal'||value.error||quotaFree===0;
  box.append(node('p',`${!value.available?'Storage drive unavailable':value.status||'Status Unknown'}${value.error?': '+value.error:''}`,warning?'warning':''));
  if(!value.available)box.append(node('p','Restore the selected drive or correct its access before downloading. No replacement empty Library is created.','warning'));
 }
 return grid;
}
