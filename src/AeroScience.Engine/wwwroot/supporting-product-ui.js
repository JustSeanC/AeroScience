import {node,disclosure,table,bytes} from './workstation-ui.js';
import {t} from './i18n.js';

// The same bounded reference projection is shown before approval and after receipt.
// It never fetches originals, parses a viewer export or infers numeric evidence.
export function supportingProductEvidence(parent,p){
 const box=node('section',undefined,'supporting-product-evidence');box.setAttribute('aria-label',t('submissions.supportingTitle'));
 box.append(node('h4',t('submissions.referenceOnly')),node('p',p.evidenceClass),node('p',t('submissions.noSourceValues'),'notice'),node('p',p.disclosure));
 const details=node('dl');for(const [key,value]of [['product',`${p.kind} · ${p.id}`],['processed',p.processedUtc],['method',p.algorithm],['corridor',`${p.geometryId} · SHA-256 ${p.geometrySha256}`],['scope',t('submissions.referenceCounts',{values:p.referencedValues,missing:p.referencedMissing})]])details.append(node('dt',t('submissions.field.'+key)),node('dd',value));box.append(details);
 for(const s of p.sources){const source=disclosure(box,s.name);source.append(node('p',`${s.project} · ${s.surface}`),node('p',t('submissions.sourceDate',{date:s.acquisitionUtc||t('common.unknown')})),node('p',`${s.horizontalCrs} · ${s.verticalReference} · ${s.unit}`),node('p',t('submissions.sourceAttributes',{attributes:s.dimensions.join(', ')})),node('p',s.license),node('p',s.citation));
  table(source,t('submissions.originalReferences'),[t('submissions.fileOrdinal'),t('submissions.fileSize'),'SHA-256'],s.originals.map(f=>[f.ordinal,bytes(f.bytes),f.sha256]));source.append(node('p',`Source ${s.id} · Manifest SHA-256 ${s.manifestSha256}`));}
 const exact=disclosure(box,t('submissions.exactReference'));exact.append(node('pre',JSON.stringify(p,null,2)));parent.append(box);return box;
}
