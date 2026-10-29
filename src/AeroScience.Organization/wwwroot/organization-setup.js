import {node,button,dateTime} from './workstation-ui.js';
export function organizationSetup(ui){
 function begin(title){
  const generation=ui.navigate();ui.heading(title,'Checking saved server information…');
  const loading=node('p','Loading saved progress. No setup action is being run.');loading.setAttribute('role','status');loading.setAttribute('aria-busy','true');ui.root.append(loading);
  return ()=>ui.isCurrent(generation);
 }
 function failed(current,error,title,retry){
  if(!current()||error.name==='AbortError')return;
  ui.heading(title,'Your saved organization and research have not been changed.');
  const warning=node('p',error.message==='Failed to fetch'?'The server connection was lost. Reconnect, then try again.':error.message||'This check could not finish. Try again.','action-error');warning.setAttribute('role','alert');ui.root.append(warning);button(ui.root,title==='Organization Setup'?'Try Setup Again':'Try Health Again',retry);button(ui.root,'Check Sign-In',ui.signIn);button(ui.root,'Back to Programs',ui.programs);
 }
 async function health(){
  const current=begin('Server Health');
  try{
   const value=await ui.api('/organization/server-health');if(!current())return;
   ui.heading('Server Health',value.deploymentState);ui.root.append(node('p',`Application ${value.version} · ${value.publicOrigin}`),node('p',`Checked ${dateTime(value.checkedUtc)}`),node('p','Production Ready: No — complete the protection and provider gates before important research.','notice'));
   for(const check of value.checks){const row=node('article');row.append(node('h2',check.name),node('p',check.state),node('p',check.message));ui.root.append(row);}
   button(ui.root,'Check Server Health',health);button(ui.root,'Server Protection',ui.protection);button(ui.root,'Organization Setup',setup);button(ui.root,'Back to Programs',ui.programs);
  }catch(error){failed(current,error,'Server Health',health);}
 }
 async function setup(){
  const current=begin('Organization Setup');
  try{
   const [identity,programs,healthStatus,security]=await Promise.all([ui.api('/organization/overview'),ui.api('/organization/programs'),ui.api('/organization/server-health'),ui.api('/account-security')]);if(!current())return;
   ui.heading('Organization Setup',identity.name);
   ui.root.append(node('p','Hosting stages 1–3: create server, select Railway and review actual charges in Railway. Provider deployment is still a candidate; this page cannot confirm provider sign-in or billing.','notice'),node('p','Railway handles hosting accounts and charges. AeroScience does not collect payment information. Only expressly approved evidence leaves a pilot’s computer.'));
   const name=node('article');name.append(node('h2','Your Organization'),node('p',identity.name),node('p',identity.description),node('p',healthStatus.publicOrigin));ui.root.append(name);
   const secure=node('article');secure.append(node('h2','4. Secure Your Organization'),node('p',security.enabled&&security.recoverySaved?'Your authenticator and saved recovery have been verified by this server.':'Your account still needs authenticator and saved-code recovery setup.'),node('p','MFA is required for production administrators. Keep recovery codes and the private server recovery key outside the hosting account. Passkeys are not implemented.'));
   const backup=healthStatus.checks.find(c=>c.name==='Backups & recovery');if(backup)secure.append(node('p',backup.message));
   button(secure,'Account Security',ui.security);button(secure,'Server Protection',ui.protection);ui.root.append(secure);
   const research=node('article');research.append(node('h2','Start Research'),node('p',programs.items.length?'Open a program and create a campaign from a supported research template.':'Create your first program, then choose Create Campaign. The existing editor keeps its normal draft and publication rules.'));button(research,programs.items.length?'Open Research Programs':'Create First Program',programs.items.length?ui.programs:ui.createProgram);ui.root.append(research);
   const invite=node('article');invite.append(node('h2','5. Invite Pilots'),node('p','Choose Create Invitation in Members. Copy the expiring, single-use link and share it yourself. Email setup is not required.'));button(invite,'Invite Pilots',ui.members);ui.root.append(invite);
   const finish=node('article');finish.append(node('h2','Connect AeroScience'),node('p','A pilot opens My Research → Organizations on their own computer and connects to '+healthStatus.publicOrigin+'. They review this organization and approve the matching browser connection code. Joining shares no personal flights.'),node('p','Saved setup can be reopened after signing in. This does not certify hosting, backups or the five-stage journey as production ready.','notice'));button(finish,'Refresh Setup Progress',setup);button(finish,'Server Health',health);button(finish,'Open Management Portal',ui.programs);ui.root.append(finish);
  }catch(error){failed(current,error,'Organization Setup',setup);}
 }
 return {setup,health};
}
