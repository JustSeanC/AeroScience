import {node,button,select,dateTime,disclosure,table} from './workstation-ui.js';

// Protected manager inventory only. Receipt is deliberately separate from scientific review.
export function submissionUi(ui){
 const {api,root,heading,modal,field,submit,draftCommand,dialog}=ui;
 async function inventory(offset=0,prefix=''){
  const page=await api('/organization/submissions?offset='+offset+'&prefix='+encodeURIComponent(prefix));heading('Received Evidence','Expressly shared pilot evidence. Received / Awaiting Review is not a reviewer decision or scientific approval.');button(root,'Back to Programs',ui.back);
  const search=node('form'),input=field(search,'Submission name starts with','text',prefix,100);root.append(search);submit(search,'Find Submissions',()=>inventory(0,input.value));
  for(const value of page.items){const row=node('article');row.append(node('h2',value.name),node('p',value.status),node('p',`Received ${dateTime(value.receivedUtc)} · ${value.payloadBytes.toLocaleString()} bytes`));button(row,'Inspect Received Evidence',()=>inspect(value));root.append(row);}
  if(!page.items.length)root.append(node('p','No received evidence on this page. Local drafts, claims and queued deliveries are not received submissions.'));
  if(offset)button(root,'Newer Submissions',()=>inventory(Math.max(0,offset-20),prefix));if(page.nextOffset!==null)button(root,'Older Submissions',()=>inventory(page.nextOffset,prefix));
 }
 async function inspect(value){
  const d=await api(`/organization/submissions/${value.pilotId}/${value.id}`),m=d.manifest,box=modal('Received Evidence');box.append(node('h3',m.name),node('p',`${m.organizationName} · ${m.campaignName}`),node('p','Received / Awaiting Review. Human review and approvals are not implemented in this checkpoint.'),node('p',m.historyDisclosure),node('p',m.privacyDisclosure));
  for(const binding of m.cards)box.append(node('p',`Issued card revision ${binding.issuedRevision} · ${dateTime(binding.startUtc)} → ${dateTime(binding.endUtc)} · ${binding.knownRecordedAttempts} retained attempt(s); unshared history may exist.`));
  const content=node('div');box.append(content);let offset=0;
  function more(){const rows=d.evidence.slice(offset,offset+20);for(const item of rows){const details=disclosure(content,item.reference.description,`${item.reference.records} record(s) · ${item.reference.contentBytes} bytes · ${item.reference.privacy}`),data=item.data;if(data.documentation)for(const note of data.documentation)details.append(node('h4','Pilot Observation'),node('p',note.text),node('p',`${note.category} · ${dateTime(note.timestampUtc)}`));if(data.observations)table(details,'Original scientific observations',['Original UTC','Measurement','Value','Units','Quality','Provenance / source'],data.observations.map(o=>[dateTime(o.timestampUtc),o.measurementId,o.value,o.unit,o.quality,`${o.provenance} · ${o.source}`]));if(data.result)details.append(node('p',`Local result: ${data.result.outcome}. This is not organization approval.`));if(data.definition)details.append(node('p',`${data.definition.name} · frozen local revision ${data.definition.revision}`));const raw=disclosure(details,'Exact selected evidence & original references');raw.append(node('pre',JSON.stringify(item,null,2)));}offset+=rows.length;load.disabled=offset>=d.evidence.length;}
  const load=button(box,'More Selected Evidence',more);more();button(box,'Close',()=>dialog.close());
 }
 async function policy(card){const current=await api('/organization/submissions/policies/'+card.id),box=modal('Submission Policy'),form=node('form');box.append(form);form.append(node('h3',card.name),node('p',`Published submission policy ${current.revision}. Issued scientific requirements and existing claims are unchanged.`));
  const mode=select(form,'Evidence required to submit',[['requireMinimumEvidence','Require Minimum Evidence'],['allowIncompleteSubmission','Allow Incomplete Submission']],current.mode);form.append(node('p',current.minimumRule),node('p','Incomplete evidence never becomes a Pass. An unsupported automatic completeness rule requires Allow Incomplete Submission and later human review. Changing policy invalidates earlier unsent approvals.'),node('p',current.retentionDisclosure));const command=draftCommand();submit(form,'Publish Submission Policy',async()=>{await api('/organization/submissions/policies/'+card.id,command({version:1,expectedRevision:current.revision,mode:mode.value}));dialog.close();});button(form,'Cancel',()=>dialog.close());
 }
 return{inventory,policy};
}
