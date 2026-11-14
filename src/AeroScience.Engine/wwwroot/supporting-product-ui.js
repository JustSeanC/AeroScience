import {message as localizedCopy,setPresentationText} from './i18n.js';
import {node,disclosure,table,bytes} from './workstation-ui.js';
import {t as textForUI,t} from './i18n.js';

// The same bounded reference projection is shown before approval and after receipt.
// It never fetches originals, parses a viewer export or infers numeric evidence.
export function supportingProductEvidence(parent,p){
 const box=node('section',undefined,'supporting-product-evidence');setPresentationText(box,localizedCopy('submissions.supportingTitle'),"aria-label");
 box.append(node('h4',localizedCopy('submissions.referenceOnly')),node('p',p.evidenceClass),node('p',localizedCopy('submissions.noSourceValues'),'notice'),node('p',p.disclosure));
 const details=node('dl');for(const [key,value]of [['product',`${p.kind} · ${p.id}`],['processed',p.processedUtc],['method',p.algorithm],['corridor',textForUI("ui.supportingProductUi.messageSHA256",{value1:(String(p.geometryId)),value2:(String(p.geometrySha256))})],['scope',t('submissions.referenceCounts',{values:p.referencedValues,missing:p.referencedMissing})]])details.append(node('dt',localizedCopy('submissions.field.'+key)),node('dd',value));box.append(details);
 for(const s of p.sources){const source=disclosure(box,s.name);source.append(node('p',`${s.project} · ${s.surface}`),node('p',localizedCopy('submissions.sourceDate',{date:s.acquisitionUtc||t('common.unknown')})),node('p',`${s.horizontalCrs} · ${s.verticalReference} · ${s.unit}`),node('p',localizedCopy('submissions.sourceAttributes',{attributes:s.dimensions.join(', ')})),node('p',s.license),node('p',s.citation));
  table(source,localizedCopy('submissions.originalReferences'),[localizedCopy('submissions.fileOrdinal'),localizedCopy('submissions.fileSize'),'SHA-256'],s.originals.map(f=>[f.ordinal,bytes(f.bytes),f.sha256]));source.append(node('p',localizedCopy("ui.supportingProductUi.messageSourceManifestSHA256",{value1:(String(s.id)),value2:(String(s.manifestSha256))})));}
 const exact=disclosure(box,localizedCopy('submissions.exactReference'));exact.append(node('pre',JSON.stringify(p,null,2)));parent.append(box);return box;
}
