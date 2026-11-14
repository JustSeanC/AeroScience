import {message as localizedCopy,setPresentationText} from './i18n.js';
import {t as textForUI} from './i18n.js';
import {node,button,dateTime} from './workstation-ui.js';
export function organizationSetup(ui){
 function begin(title){
  const generation=ui.navigate();ui.heading(title,localizedCopy("ui.organizationSetup.messageCheckingSavedServerInformation"));
  const loading=node('p',localizedCopy("ui.organizationSetup.messageLoadingSavedProgressNoSetupActionIs"));loading.setAttribute('role','status');loading.setAttribute('aria-busy','true');ui.root.append(loading);
  return ()=>ui.isCurrent(generation);
 }
 function failed(current,error,title,retry){
  if(!current()||error.name==='AbortError')return;
  ui.heading(title,localizedCopy("ui.organizationSetup.messageYourSavedOrganizationAndResearchHaveNot"));
  const warning=node('p',error.message==='Failed to fetch'?localizedCopy("ui.organizationSetup.messageTheServerConnectionWasLostReconnectThen"):error.message||localizedCopy("ui.organizationSetup.messageThisCheckCouldNotFinishTryAgain"),'action-error');warning.setAttribute('role','alert');ui.root.append(warning);button(ui.root,retry===setup?localizedCopy("ui.organizationSetup.messageTrySetupAgain"):localizedCopy("ui.organizationSetup.messageTryHealthAgain"),retry);button(ui.root,localizedCopy("ui.organizationSetup.messageCheckSignIn"),ui.signIn);button(ui.root,localizedCopy("ui.organizationMembers.messageBackToPrograms"),ui.programs);
 }
 async function health(){
  const current=begin(localizedCopy("ui.organizationSetup.messageServerHealth"));
  try{
   const value=await ui.api('/organization/server-health');if(!current())return;
   ui.heading(localizedCopy("ui.organizationSetup.messageServerHealth"),value.deploymentState);ui.root.append(node('p',localizedCopy("ui.organizationSetup.messageApplication",{value1:(String(value.version)),value2:(String(value.publicOrigin))})),node('p',localizedCopy("ui.organizationSetup.messageChecked",{value1:(String(dateTime(value.checkedUtc)))})),node('p',localizedCopy("ui.organizationSetup.messageProductionReadyNoCompleteTheProtection"),'notice'));
   for(const check of value.checks){const row=node('article');row.append(node('h2',check.name),node('p',check.state),node('p',check.message));ui.root.append(row);}
   button(ui.root,localizedCopy("ui.organizationSetup.messageCheckServerHealth"),health);button(ui.root,localizedCopy("ui.organizationSetup.messageServerProtection"),ui.protection);button(ui.root,localizedCopy("ui.organizationSetup.messageOrganizationSetup"),setup);button(ui.root,localizedCopy("ui.organizationMembers.messageBackToPrograms"),ui.programs);
  }catch(error){failed(current,error,localizedCopy("ui.organizationSetup.messageServerHealth"),health);}
 }
 async function setup(){
  const current=begin(localizedCopy("ui.organizationSetup.messageOrganizationSetup"));
  try{
   const [identity,programs,healthStatus,security]=await Promise.all([ui.api('/organization/overview'),ui.api('/organization/programs'),ui.api('/organization/server-health'),ui.api('/account-security')]);if(!current())return;
   ui.heading(localizedCopy("ui.organizationSetup.messageOrganizationSetup"),identity.name);
   ui.root.append(node('p',localizedCopy("ui.organizationSetup.messageHostingStages13CreateServerSelectRailway"),'notice'),node('p',localizedCopy("ui.organizationSetup.messageRailwayHandlesHostingAccountsAndChargesAeroScience")));
   const name=node('article');name.append(node('h2',localizedCopy("ui.organizationSetup.messageYourOrganization")),node('p',identity.name),node('p',identity.description),node('p',healthStatus.publicOrigin));ui.root.append(name);
   const secure=node('article');secure.append(node('h2',localizedCopy("ui.organizationSetup.message4SecureYourOrganization")),node('p',security.enabled&&security.recoverySaved?localizedCopy("ui.organizationSetup.messageYourAuthenticatorAndSavedRecoveryHaveBeen"):localizedCopy("ui.organizationSetup.messageYourAccountStillNeedsAuthenticatorAndSavedCode")),node('p',localizedCopy("ui.organizationSetup.messageMFAIsRequiredForProductionAdministratorsKeep")));
   const backup=healthStatus.checks.find(c=>c.name==='Backups & recovery');if(backup)secure.append(node('p',backup.message));
   button(secure,localizedCopy("ui.organizationSetup.messageAccountSecurity"),ui.security);button(secure,localizedCopy("ui.organizationSetup.messageServerProtection"),ui.protection);ui.root.append(secure);
   const research=node('article');research.append(node('h2',localizedCopy("ui.organizationSetup.messageStartResearch")),node('p',programs.items.length?localizedCopy("ui.organizationSetup.messageOpenAProgramAndCreateACampaign"):localizedCopy("ui.organizationSetup.messageCreateYourFirstProgramThenChooseCreate")));button(research,programs.items.length?localizedCopy("ui.organizationSetup.messageOpenResearchPrograms"):localizedCopy("ui.organizationSetup.messageCreateFirstProgram"),programs.items.length?ui.programs:ui.createProgram);ui.root.append(research);
   const invite=node('article');invite.append(node('h2',localizedCopy("ui.organizationSetup.message5InvitePilots")),node('p',localizedCopy("ui.organizationSetup.messageChooseCreateInvitationInMembersCopyThe")));button(invite,localizedCopy("ui.organizationSetup.messageInvitePilots"),ui.members);ui.root.append(invite);
   const finish=node('article');finish.append(node('h2',localizedCopy("ui.organizationMembers.messageConnectAeroScience")),node('p',localizedCopy("ui.organizationSetup.messageAPilotOpensMyResearchOrganizations",{value1:(String(healthStatus.publicOrigin))})),node('p',localizedCopy("ui.organizationSetup.messageSavedSetupCanBeReopenedAfterSigning"),'notice'));button(finish,localizedCopy("ui.organizationSetup.messageRefreshSetupProgress"),setup);button(finish,localizedCopy("ui.organizationSetup.messageServerHealth"),health);button(finish,localizedCopy("ui.accountSecurity.messageOpenManagementPortal"),ui.programs);ui.root.append(finish);
  }catch(error){failed(current,error,localizedCopy("ui.organizationSetup.messageOrganizationSetup"),setup);}
 }
 return {setup,health};
}
