import {organizationSetup} from './organization-setup.js';
import {researchTemplateUi} from './research-template-ui.js';
import {programWorkflowUi} from './program-workflow-ui.js';
import {campaignProgressUi} from './campaign-progress-ui.js';
import {reviewUi} from './organization-reviews.js';
import {submissionUi} from './organization-submissions.js';
import {opportunityUi} from './organization-opportunities.js';
import {membershipUi} from './organization-members.js';
import {node,button,select,dateTime,disclosure} from './workstation-ui.js';

const root=document.querySelector('#workspace'),account=document.querySelector('#account'),dialog=document.querySelector('#editor');
let session,templates=[],programOffset=0,currentProgram=null,campaignOffset=0,epoch=0,returnFocus,publishingPolicy={mode:'legacy'};
const focusName={scientificResearch:'Scientific Research',experimentalFlightTest:'Experimental Flight Test',mixed:'Mixed'};
let join=null,connect=null,enrollment=false;
function takeEntry(){const fragment=new URLSearchParams(location.hash.slice(1));if(!fragment.has('join')&&!fragment.has('connect')&&!fragment.has('enroll'))return false;join=fragment.get('join');connect=fragment.get('connect');enrollment=fragment.has('enroll');window.history.replaceState(null,'',location.pathname);return true;}
takeEntry();window.addEventListener('hashchange',()=>{if(takeEntry())load();});
const membership=membershipUi({api,root,field,status,heading,submit,modal,dialog,session:()=>session,load,programs:showPrograms,identity:showIdentity,draftCommand,progress:openProgress,approved:openApproved,joined:()=>{join=null;enrollment=false;}});
const reviews=reviewUi({api,root,heading,modal,field,submit,draftCommand,dialog,back:load,campaigns:()=>membership.memberHome(),canAuthorizeException:()=>session?.user?.roles.some(r=>["Administrator","Manager"].includes(r)),exception:(summary,requirement,receipts)=>workflowView().fromReview(summary,requirement,receipts)});
const submissions=submissionUi({api,root,heading,modal,field,submit,draftCommand,dialog,back:showPrograms,review:id=>reviews.workspace(id,null,true)});
const opportunities=opportunityUi({api,root,heading,modal,field,submit,draftCommand,dialog,submissionPolicy:submissions.policy,publicationHistory:(kind,id)=>templateUi.publicationHistory(kind,id),publication:async(kind,v,done)=>{const p=await api('/research-templates/policy');if(p.mode==='legacy')return false;await templateUi.publish(kind,v,done);return true;},back:()=>currentProgram?showProgram(currentProgram):load()});
const templateUi=researchTemplateUi({api,root,heading,modal,field,submit,draftCommand,dialog,session:()=>session,navigate:()=>{epoch++;},back:load,useTemplate:usePublishedTemplate});
templateUi.takeEntry();window.addEventListener('hashchange',()=>{if(templateUi.takeEntry())load();});
function progressView(){const view=node('section');root.replaceChildren(view);return campaignProgressUi({root:view,headingLevel:'h1',read:path=>api('/membership/research-progress'+path),write:(path,body,method)=>api('/membership/research-progress'+path,body,method),back:load,review:id=>reviews.workspace(id,null,true),manage:v=>opportunities.cards({id:v.campaignId,publishedRevision:v.campaignRevision,name:v.name,objective:v.objective})});}
function workflowView(back=load){const view=node('section');root.replaceChildren(view);return programWorkflowUi({root:view,headingLevel:'h1',read:path=>api('/membership/research-progress'+path),write:(path,body,method)=>api('/membership/research-progress'+path,body,method),back,review:id=>reviews.workspace(id,null,true)});}
function openProgress(id){return progressView().campaign(id);}
function openApproved(id){return progressView().results(id);}
const setupUi=organizationSetup({api,root,heading,navigate:()=>{epoch++;},programs:showPrograms,createProgram:()=>editProgram(),members:()=>membership.members(),health:()=>setupUi.health()});
const admin=()=>session?.user?.roles.includes('Administrator');
async function api(path,body,method=body?'POST':'GET'){
 const requestedView=epoch;
 const response=await fetch('/api'+path,{method,credentials:'same-origin',headers:{...(body?{'Content-Type':'application/json','X-AeroScience-Organization-CSRF':session?.csrf||''}:{})},...(body?{body:JSON.stringify(body)}:{})});
 const value=await response.json().catch(()=>null);
 if(method==='GET'&&requestedView!==epoch)throw new DOMException('View changed.','AbortError');
 if(!response.ok)throw Error(value?.error||(response.status===401?'Sign in again to continue.':response.status===403?'This account or page does not have permission for that action.':response.status===429?'Too many requests. Wait a minute before trying again.':'The server could not confirm this action. Keep your draft and try again.'));
 return value;
}
function failure(parent,error){let box=parent.querySelector(':scope > .action-error');if(!box){box=node('div',undefined,'action-error');box.setAttribute('role','alert');parent.prepend(box);}box.textContent=error.message||'The server could not be reached. Your saved research has not been removed.';}
function field(parent,label,type='text',value='',maximum=120){const wrap=node('label',label),input=node(type==='textarea'?'textarea':'input');if(type!=='textarea')input.type=type;input.value=value;input.maxLength=maximum;input.setAttribute('aria-label',label);wrap.append(input);parent.append(wrap);return input;}
function status(parent,message=''){const p=node('p',message,'workspace-status');p.setAttribute('role','status');parent.append(p);return p;}
function heading(title,description){root.replaceChildren(node('h1',title));if(description)root.append(node('p',description,'lead'));}
function modal(title){returnFocus=document.activeElement;dialog.replaceChildren();const head=node('div',undefined,'dialog-head'),h=node('h2',title);h.id='dialog-title';head.append(h);const close=button(head,'×',()=>dialog.close());close.setAttribute('aria-label','Close dialog');dialog.append(head);dialog.showModal();return dialog;}
dialog.addEventListener('close',()=>{if(returnFocus?.isConnected)returnFocus.focus();});
function draftCommand(){let prior='',id;return data=>{const key=JSON.stringify(data);if(key!==prior){prior=key;id=crypto.randomUUID();}return {...data,requestId:id};};}
function submit(form,label,run){const b=node('button',label,'primary');b.type='submit';form.append(b);let busy=false;form.onsubmit=async event=>{event.preventDefault();if(busy)return;busy=true;b.disabled=true;form.querySelector(':scope > .action-error')?.remove();try{await run();}catch(e){failure(form,e);}finally{busy=false;b.disabled=false;}};return b;}
function renderAccount(){account.replaceChildren();if(session?.user){account.append(node('span',session.user.name));templateUi.navigation(account);if(admin()){button(account,'Organization Setup',()=>setupUi.setup());button(account,'Server Health',()=>setupUi.health());}button(account,'Sign Out',async()=>{await api('/sign-out',{});await load();});}}
async function load(){let own=++epoch;try{const next=await api('/session');if(own!==epoch)return;session=next;renderAccount();if(!session.user){if(join||enrollment)await membership.enroll(join);else showIdentity();return;}if(templateUi.hasPending()){await templateUi.proposalEntry();return;}if(connect){await membership.approve(connect);return;}if(join){await membership.enroll(join);return;}if(session.user.roles.includes('Reviewer')){await reviews.queue();return;}if(!session.user.roles.some(r=>['Administrator','Manager'].includes(r))){await membership.memberHome();return;}[templates,publishingPolicy]=await Promise.all([api('/organization/templates'),api('/research-templates/policy')]);if(own!==epoch)return;own=epoch+1;await showPrograms();}catch(e){if(own!==epoch)return;heading('Organization server unavailable','No saved change was confirmed. Try reconnecting to this server.');failure(root,e);button(root,'Try Again',load);}}
function showIdentity(message=''){
 heading('Your organization’s research workspace','Plan scientific investigations and flight-test programs together. Personal Research remains local and independent.');
 const hosting=node('a','Create an Organization Server','button');hosting.href='/organization-hosting.html';root.append(hosting);
 if(session.initialized)button(root,join?'Back to Invitation':'Join Organization',()=>membership.enroll(join));
 const panel=node('section',undefined,'panel auth'),form=node('form');panel.append(node('h2',session.initialized?'Sign In':'Set Up Your Organization'));panel.append(form);root.append(panel);
 if(message)status(form,message);
 let secret,name,description;
 if(!session.initialized){form.append(node('p','Use the protected setup code from your hosting-account owner. In a managed deployment, open the provider’s private service settings and copy Organization__BootstrapSecret. The code expires; only the owner can rotate it. Never put it in a link or public message. Opening this page does not grant administrator access.'));secret=field(form,'Setup code','password','',256);secret.autocomplete='off';secret.required=true;name=field(form,'Organization name');name.required=true;description=field(form,'Organization description','textarea','',4000);}
 const username=field(form,'Account name');username.maxLength=64;username.autocomplete='username';username.required=true;
 const password=field(form,'Password','password','',256);password.autocomplete=session.initialized?'current-password':'new-password';password.required=true;
 if(!session.initialized){password.minLength=14;form.append(node('p','Use at least 14 characters, including uppercase and lowercase letters, a number and a symbol.','help'));}
 submit(form,session.initialized?'Sign In':'Create Organization',async()=>{
  const setup=!session.initialized;
  try{await api(setup?'/bootstrap':'/sign-in',setup?{secret:secret.value,organizationName:name.value,description:description.value,userName:username.value,password:password.value}:{userName:username.value,password:password.value});}
  finally{password.value='';if(secret)secret.value='';}
  session=await api('/session');if(setup)showIdentity('Organization saved. Sign in with your new administrator account.');else await load();
 });
}
async function showPrograms(){
 const own=++epoch;currentProgram=null;
 const [overview,page]=await Promise.all([api('/organization/overview'),api(`/organization/programs?offset=${programOffset}`)]);if(own!==epoch)return;
 heading('Organization Overview',overview.name);root.append(node('p',overview.description));const actions=node('div',undefined,'actions');root.append(actions);button(actions,'Create Program',()=>editProgram());button(actions,'Members',()=>membership.members());button(actions,'Received Evidence',()=>submissions.inventory());button(actions,'Review Queue',()=>reviews.queue());
 const section=node('section');section.setAttribute('aria-label','Programs');section.append(node('h2','Programs'),node('p','Organize related scientific research and experimental flight tests.'));const grid=node('div',undefined,'grid');section.append(grid);root.append(section);
 if(!page.items.length)grid.append(node('p','No programs on this page. Create a program to begin.'));
 for(const program of page.items){const card=node('article');card.append(node('h3',program.name),node('p',program.description),node('p',`Revision ${program.revision} · Updated ${dateTime(program.updatedUtc)} · ${program.authorName}`,'metadata'));button(card,'Open Program',()=>showProgram(program));button(card,'Edit Program',()=>editProgram(program));grid.append(card);}
 const pages=node('div',undefined,'actions');section.append(pages);if(programOffset)button(pages,'Previous Programs',()=>{programOffset=Math.max(0,programOffset-20);return showPrograms();});if(page.nextOffset!==null)button(pages,'Next Programs',()=>{programOffset=page.nextOffset;return showPrograms();});
}
function editProgram(program){
 const box=modal(program?'Edit Program':'Create Program'),form=node('form');box.append(form);const name=field(form,'Program name','text',program?.name||''),description=field(form,'Program description','textarea',program?.description||'',4000);name.required=true;
 const command=draftCommand();submit(form,'Save Program',async()=>{const saved=await api('/organization/programs'+(program?'/'+program.id:''),command({version:1,expectedRevision:program?.revision||0,name:name.value,description:description.value}),program?'PUT':'POST');dialog.close();campaignOffset=0;await showProgram(saved);});button(form,'Cancel',()=>dialog.close());
}
async function showProgram(program){
 const own=++epoch;currentProgram=program;const page=await api(`/organization/programs/${program.id}/campaigns?offset=${campaignOffset}`);if(own!==epoch)return;
 heading(program.name,program.description);const actions=node('div',undefined,'actions');root.append(actions);button(actions,'Back to Programs',()=>{campaignOffset=0;return showPrograms();});button(actions,'Edit Program',()=>editProgram(program));button(actions,'Create Campaign',()=>editCampaign(program));button(actions,'Test-Program Progression',()=>workflowView(()=>showProgram(program)).open(program.id));root.append(node('h2','Campaigns'));
 if(!page.items.length)root.append(node('p','No campaigns on this page. Start with a question or test objective.'));
 for(const campaign of page.items){const card=node('article');card.setAttribute('aria-label',campaign.name);card.append(node('h3',campaign.name),node('span',campaign.status==='published'?'Published':'Draft','badge'),node('span',focusName[campaign.focus],'badge'),node('p',campaign.objective||'Research question or test objective is still needed.'),node('p',campaign.description));const template=templates.find(t=>t.reference.id===campaign.definition.template.id);card.append(node('p',`${template?.name||'Saved research approach'} · approach version ${campaign.definition.template.version}`),node('p',`Revision ${campaign.revision}${campaign.publishedRevision?' · Retained published revision '+campaign.publishedRevision:''} · Updated ${dateTime(campaign.updatedUtc)} · ${campaign.authorName}`,'metadata'));
  const actions=node('div',undefined,'actions');card.append(actions);button(actions,'Edit Campaign',()=>editCampaign(program,campaign));if(campaign.publishedRevision){button(actions,'Campaign Progress',()=>openProgress(campaign.id));button(actions,'Approved Results',()=>openApproved(campaign.id));button(actions,'Campaign Test Cards',()=>opportunities.cards(campaign));button(actions,'Campaign Review Access',()=>reviews.permissions(campaign));}if((admin()||publishingPolicy.mode!=='legacy')&&campaign.status==='draft')button(actions,'Review & Publish',()=>publishCampaign(program,campaign),'primary');button(actions,'Revision History',()=>history(campaign));button(actions,'Publication History',()=>templateUi.publicationHistory('campaign',campaign.id));const details=disclosure(card,'Research Approach Details','Saved definition only. This does not include personal flights, scientific data or results.');details.append(node('pre',JSON.stringify(campaign.definition,null,2)));root.append(card);
 }
 const pages=node('div',undefined,'actions');root.append(pages);if(campaignOffset)button(pages,'Previous Campaigns',()=>{campaignOffset=Math.max(0,campaignOffset-20);return showProgram(program);});if(page.nextOffset!==null)button(pages,'Next Campaigns',()=>{campaignOffset=page.nextOffset;return showProgram(program);});
}
function editCampaign(program,campaign,chosen){
 if(chosen&&!templates.some(t=>t.reference.id===chosen.reference.id&&t.reference.version===chosen.reference.version))templates.push(chosen);
 if(campaign?.sourceTemplate&&!templates.some(t=>t.reference.id===campaign.sourceTemplate.id&&t.reference.version===campaign.sourceTemplate.version))templates.push({reference:campaign.sourceTemplate,name:campaign.name+' — saved published template',description:campaign.description,definition:campaign.definition});
 const box=modal(campaign?'Edit Campaign':'Create Campaign'),form=node('form');box.append(form);
 if(campaign?.publishedRevision)form.append(node('p','Saving creates a new draft. The published revision remains unchanged.','notice'));
 const name=field(form,'Campaign name','text',campaign?.name||''),description=field(form,'Campaign description','textarea',campaign?.description||'',4000),objective=field(form,'Research question or test objective','textarea',campaign?.objective||'',4000);name.required=true;
 const focus=select(form,'Campaign focus',Object.entries(focusName),campaign?.focus||'scientificResearch');
 const templateKey=t=>t.reference.id.startsWith('published:')?t.reference.id+'@'+t.reference.version:t.reference.id;const initial=chosen?.reference||campaign?.sourceTemplate||campaign?.definition.template||templates[0].reference;const approach=select(form,'Research starting point',templates.map(t=>[templateKey(t),t.name+(t.reference.id.startsWith('published:')?' — published revision '+t.reference.version:'')]),initial.id.startsWith('published:')?initial.id+'@'+initial.version:initial.id),preview=node('p',undefined,'help');form.append(preview);
 const describe=()=>preview.textContent=templates.find(t=>templateKey(t)===approach.value)?.description||'Unsupported saved approach.';approach.onchange=describe;describe();form.append(node('p','Save a draft first. An administrator can publish a reviewed revision after its question or objective is complete.','help'));
 const command=draftCommand();submit(form,'Save Draft',async()=>{const template=templates.find(t=>templateKey(t)===approach.value);await api(`/organization/programs/${program.id}/campaigns`+(campaign?'/'+campaign.id:''),command({version:1,expectedRevision:campaign?.revision||0,name:name.value,description:description.value,objective:objective.value,focus:focus.value,template:template.reference}),campaign?'PUT':'POST');dialog.close();await showProgram(program);});button(form,'Cancel',()=>dialog.close());
}
async function publishCampaign(program,campaign){
 const p=await api('/research-templates/policy');if(p.mode!=='legacy')return templateUi.publish('campaign',campaign,()=>showProgram(program));
 const box=modal('Publish Campaign'),form=node('form');box.append(form);form.append(node('h3',campaign.name),node('p',campaign.objective||'No research question or objective has been saved.'),node('p',`${focusName[campaign.focus]} · Reviewed revision ${campaign.revision}`),node('p','Publish this saved definition for your organization. Later edits create new drafts. Publishing does not start flights or share personal evidence.','notice'));
 const request={version:1,requestId:crypto.randomUUID(),expectedRevision:campaign.revision};submit(form,'Publish Campaign',async()=>{await api(`/organization/campaigns/${campaign.id}/publish`,request);dialog.close();await showProgram(program);});button(form,'Cancel',()=>dialog.close());
}
async function history(campaign){
 const box=modal('Campaign Revision History'),content=node('div');box.append(content);let revision=campaign.revision;
 async function read(){const row=await api(`/organization/definitions/${campaign.id}/revisions/${revision}`);if(!box.open)return;const saved=JSON.parse(row.snapshotJson);content.replaceChildren(node('h3',saved.name),node('p',`Revision ${row.revision} · ${saved.status==='published'?'Published':'Draft'} · ${saved.authorName} · ${dateTime(row.timestampUtc)}`),node('p',saved.objective||'No objective saved.'),node('p',saved.description));const actions=node('div',undefined,'actions');content.append(actions);if(revision>1)button(actions,'Previous Revision',()=>{revision--;return read();});if(revision<campaign.revision)button(actions,'Next Revision',()=>{revision++;return read();});}
 try{await read();}catch(e){failure(content,e);}
}
async function usePublishedTemplate(value){
 const box=modal('Use Published Template'),form=node('form');box.append(form);const page=await api('/organization/programs');const list=page.items;let nextOffset=page.nextOffset,campaignOffset=0;const choice=select(form,'Program',list.map(p=>[p.id,p.name]),value.programId||list[0]?.id),campaigns=[],existing=select(form,'Existing campaign',[['','Choose an existing campaign']],'');
 if(nextOffset!==null)button(form,'More Programs',async()=>{if(nextOffset===null)return;const next=await api('/organization/programs?offset='+nextOffset);nextOffset=next.nextOffset;for(const p of next.items){list.push(p);const o=node('option',p.name);o.value=p.id;choice.append(o);}});
 async function loadCampaigns(reset=false){if(reset){campaignOffset=0;campaigns.length=0;existing.replaceChildren();const option=node('option','Choose an existing campaign');option.value='';existing.append(option);}if(!choice.value||campaignOffset===null)return;const next=await api(`/organization/programs/${choice.value}/campaigns?offset=${campaignOffset}`);campaignOffset=next.nextOffset;for(const c of next.items){campaigns.push(c);const option=node('option',`${c.name} — ${c.status}, revision ${c.revision}`);option.value=c.id;existing.append(option);}}
 choice.onchange=()=>loadCampaigns(true).catch(e=>failure(form,e));button(form,'More Campaigns',()=>loadCampaigns());await loadCampaigns(true);
 form.append(node('p','Review changes in the existing campaign editor before saving. A published campaign receives a new draft; existing publications, claims and flights stay frozen.'));
 const chosen={reference:{id:'published:'+value.id,version:value.revision},name:value.template.name,description:value.template.description,definition:value.template.definition};
 submit(form,'Create Campaign from Template',async()=>{const program=list.find(p=>p.id===choice.value);if(!program)throw Error('Create a program first.');dialog.close();editCampaign(program,null,chosen);});
 button(form,'Use in Existing Campaign',()=>{const program=list.find(p=>p.id===choice.value),campaign=campaigns.find(c=>c.id===existing.value);if(!program||!campaign)throw Error('Choose an existing campaign first.');dialog.close();editCampaign(program,campaign,chosen);});
}
await load();
