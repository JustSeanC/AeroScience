import {node,button,dateTime,errorMessage} from './workstation-ui.js';
export function organizationNotifications({api,root,heading,field,submit,back,navigate}){
 const path='/organization/notifications';
 const render=async run=>{try{return await run();}catch(error){if(error.name!=='AbortError')errorMessage(root,error,()=>render(run));}};
 const settings=()=>render(settingsView),pilot=()=>render(pilotView),announcements=(d,before=0)=>render(()=>announcementsView(d,before));
 const action=revision=>({requestId:crypto.randomUUID(),expectedRevision:revision});
 function check(form,label,value){const wrap=node('label'),input=node('input');input.type='checkbox';input.checked=value;wrap.append(input,document.createTextNode(label));form.append(wrap);return input;}
 async function settingsView(){
  navigate();heading('Organization Settings → Notifications','Loading notification settings…');
  const d=await api(path);heading('Organization Settings → Notifications','Optional Discord announcements. OFF until you explicitly enable them. Account Security must show recent administrator MFA and saved recovery codes before a change or test.');
  root.append(node('p',d.notice,'notice'));if(!d.authorizationCurrent)root.append(node('p','Delivery is held because administrator authorization changed. Verify Account Security, review these settings and save them to authorize a new destination revision.','notice'));button(root,'Back',back);
  const form=node('form');root.append(form);const label=field(form,'Destination name','text',d.label||'',80),webhook=field(form,'New Discord webhook (leave blank to keep saved credential)','password','',512);webhook.autocomplete='off';webhook.spellcheck=false;
  root.append(node('p',d.maskedIdentifier?`Saved: ${d.label} · ${d.maskedIdentifier} · revision ${d.revision}`:'No webhook saved. Nothing will be sent.'));
  const enabled=check(form,'Enable Discord notifications',d.enabled),flights=check(form,'Flight sharing started / ended — requires separate pilot consent for each flight',!!(d.categories&1)),research=check(form,'Reviewer-approved research contributions — explicit external disclosure required for each announcement',!!(d.categories&2)),exceptions=check(form,'Accepted documented exceptions — explicit external disclosure required for each announcement',!!(d.categories&4));
  form.append(node('p','Saving any change invalidates pending deliveries and previous flight/disclosure approvals for this destination revision. Previously posted messages cannot be recalled here. Credentials are encrypted; saved full URLs are never shown.'));
  submit(form,'Save Notification Settings',async()=>{try{await api(path,{...action(d.revision),label:label.value,enabled:enabled.checked,categories:(flights.checked?1:0)|(research.checked?2:0)|(exceptions.checked?4:0),webhook:webhook.value||null});}finally{webhook.value='';}await settings();});
  const preview=node('details'),summary=node('summary','Preview Test Notification');preview.append(summary,node('p','AeroScience — Test notification. This administrator-requested message contains no flight or scientific evidence.'));root.append(preview);
  if(d.enabled){button(root,'Send Test Notification',async()=>{await api(path+'/test',action(d.revision));await settings();});}
  root.append(node('p','Sending a test is an explicit action. Saving a webhook does not send a test. Delivery history shows actual acknowledgments, not a connection guess. Scientific measurements, client-reported attempts and local evaluations are never automatically announced. Complete scientific products are not sent to Discord.'));
  if(d.id!=='00000000-0000-0000-0000-000000000000'&&d.maskedIdentifier)button(root,'Delete Destination',async()=>{if(!confirm('Permanently delete this saved webhook credential and cancel pending notifications? Posted Discord messages remain in Discord.'))return;await api(path,{...action(d.revision),label:d.label,enabled:false,categories:0,delete:true});await settings();});
  button(root,'Review External Announcements',()=>announcements(d));
  await history(d,0);
 }
 async function history(d,before){const page=await api(path+'/history?before='+before);const section=node('section');section.append(node('h2','Delivery History'));root.append(section);if(!page.items.length)section.append(node('p','No notifications on this page.'));
  for(const q of page.items){const row=node('article');row.append(node('h3',q.test?'Test Notification':'Event Announcement'),node('p',`${q.state} · ${q.attempts} send attempt(s) · ${dateTime(q.createdUtc)} · destination revision ${q.destinationRevision}`),node('p',q.reason||'Queued; authorization will be checked before sending.'));
   if(q.messageId)row.append(node('p','Discord acknowledgment: '+q.messageId));if(q.state==='ambiguous')row.append(node('p','Check the Discord channel before taking any further action. AeroScience will not automatically retry an uncertain post.'));
   if(['pending','failed','ambiguous'].includes(q.state)&&!q.reason.startsWith('Administrator closed'))button(row,'Cancel Delivery',async()=>{await api(path+'/history/'+q.id+'/cancel',action(d.revision));await settings();});section.append(row);}
  if(page.nextBefore!==null)button(section,'Older Deliveries',()=>history(d,page.nextBefore));button(section,'Refresh Delivery Status',settings);
 }
 async function announcementsView(d,before=0){navigate();heading('Review External Announcements','Loading current approvals…');const page=await api(path+'/announcements?before='+before),items=page.items;heading('Review External Announcements','Only current authoritative approvals and documented exceptions appear here. Review the exact preview before authorizing external disclosure. No private evidence, contributor identity or review notes are included.');button(root,'Back to Notifications',settings);
  if(page.nextBefore!==null)button(root,'Older Announcements',()=>announcements(d,page.nextBefore));
  if(!items.length)root.append(node('p','No current supported approvals to announce. Recorded attempts and client-reported completion are not organization approval.'));
  for(const e of items){const form=node('form');form.append(node('h2',e.category),node('p',e.message));const approved=check(form,'I authorize this exact announcement in the external Discord channel, which may be visible to nonmembers',false);submit(form,'Approve External Announcement',async()=>{if(!approved.checked)throw Error('Review the message and explicitly authorize external disclosure first.');await api(path+'/disclose',{...action(d.revision),eventId:e.eventId,confirmExternalDisclosure:true,messageSha256:e.messageSha256});await settings();});root.append(form);}
 }
 async function pilotView(){navigate();heading('My Discord Flight Announcements','Loading your current flight choices…');const data=await api('/membership/discord');heading('My Discord Flight Announcements','Sharing a flight with organization members does not allow external announcements. Choose separately for each current shared flight.');button(root,'Back',back);
  root.append(node('p','A Discord channel may be visible outside this organization. Only your chosen public flight label and event time are announced. No coordinates, aircraft tracks, notes or scientific data are sent. Revoking stops unsent messages; already delivered posts remain in Discord.','notice'));
  if(!data.destination)root.append(node('p','Discord flight announcements are disabled or unavailable. You may still revoke prior consent below.'));
  else root.append(node('p',`Destination: ${data.destination.label} · ${data.destination.maskedIdentifier} · policy revision ${data.destination.revision}. A destination change requires new consent.`));
  if(!data.flights.length)root.append(node('p','No current shared flight. Start a normal recording and explicitly share it with this organization in AeroScience first. Return here to choose external announcements.'));
  for(const f of data.flights){const row=node('article');row.append(node('h2',f.displayLabel),node('p',`Flight ${f.flightId} · ${f.allowed?'External consent granted':'External consent OFF'} · expires ${dateTime(f.expiresUtc)}`));
   const send=async allow=>{await api('/membership/discord/consent',{requestId:crypto.randomUUID(),sessionId:f.sessionId,expectedRevision:f.revision,destinationRevision:data.destination?.revision||0,allowExternalAnnouncements:allow});await pilot();};
   if(data.destination&&!f.allowed)button(row,'Allow Discord Announcements for This Flight',()=>send(true));if(f.revision>0)button(row,'Revoke Discord Announcements',()=>send(false));root.append(row);}
 }
 return {settings,pilot};
}
