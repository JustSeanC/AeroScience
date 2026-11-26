import {message,t} from './i18n.js';
import {node} from './workstation-ui.js';

// Display persisted evidence only. Current Settings never identifies an old flight.
export function simulatorSourceLabel(origin,source){
  if(source==='simulated')return t('source.synthetic');
  if(origin?.version===1&&['msfs2024','xplane12'].includes(origin.simulatorId))return t('simulator.'+origin.simulatorId);
  return t('simulator.legacyUnknown');
}
export function simulatorOriginDetails(parent,origin,source){
  const details=node('details',undefined,'simulator-provenance');
  details.append(node('summary',message('simulator.sourceSummary',{name:simulatorSourceLabel(origin,source)})));
  if(origin?.version===1&&['msfs2024','xplane12'].includes(origin.simulatorId)){
    details.dataset.simulatorOrigin=origin.simulatorId;
    details.append(node('p',message('simulator.sourceBasis')));
    const rows=node('dl');
    for(const [key,value] of [['simulator.reportedVersion',origin.reportedVersion],['simulator.reportedBuild',origin.reportedBuild],
      ['simulator.aircraftType',origin.aircraftType],['simulator.aircraftRegistration',origin.aircraftRegistration],
      ['simulator.adapter',`${origin.adapterId} / ${origin.adapterVersion}`],['simulator.apiVersion',origin.apiVersion],
      ['simulator.mapping',origin.mappingRevision],['simulator.epoch',origin.connectionEpoch],['simulator.timestampBasis',origin.timestampBasis]]){
      const row=node('div');row.append(node('dt',message(key)),node('dd',value==null?t('common.unknown'):String(value)));rows.append(row);
    }
    details.append(rows);for(const limitation of origin.limitations||[])details.append(node('p',String(limitation)));
  }else details.append(node('p',message(source==='simulated'?'simulator.syntheticBasis':'simulator.legacyBasis')));
  parent.append(details);return details;
}
