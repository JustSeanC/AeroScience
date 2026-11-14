import {initializePresentation,presentationControls,setPresentationLocaleOverride} from './presentation-settings.js';
import {localeManifest,message as copy,t} from './i18n.js';
import {node,formField,status} from './workstation-ui.js';
// Portal browser overrides and member preferences never read the companion's
// localStorage key or host API. Member choice is kept in memory until sign-out.
initializePresentation({scope:'organization'});
const parent=document.getElementById('appearance');
if(parent)presentationControls(parent,{includeHost:false});
let generation=0,identity=null;
export async function updatePortalIdentity(session){
  const user=session?.user?.id||null,scope=user?user+':'+!!session.accountSecurityRequired:null;
  if(identity===scope)return;
  identity=scope;const own=++generation;setPresentationLocaleOverride(null);
  parent?.querySelector('.member-language')?.remove();
  if(!user||session.accountSecurityRequired||!parent)return;
  const section=node('section',undefined,'member-language');parent.append(section);
  section.append(node('h2',copy('portal.language.memberTitle')),node('p',copy('portal.language.scope')));
  const choice=node('select');choice.id='organization-member-language';
  const follow=node('option',copy('portal.language.followBrowser'));follow.value='';choice.append(follow);
  for(const locale of localeManifest.locales){const option=node('option',locale.name);option.value=locale.id;option.lang=locale.id;choice.append(option);}
  formField(section,copy('portal.language.memberLabel'),choice);
  const save=node('button',copy('portal.language.save')),reload=node('button',copy('settings.hostReload'));save.type=reload.type='button';section.append(save,reload);
  const feedback=status(section,'');let revision=null,saving=false;
  async function request(change){
    const response=await fetch('/api/account/presentation',{credentials:'same-origin',cache:'no-store',...(change?{method:'POST',headers:{'Content-Type':'application/json','X-AeroScience-Organization-CSRF':session.csrf},body:JSON.stringify(change)}:{})});
    if(!response.ok)throw Error(t(response.status===409?'portal.language.conflict':'portal.language.unavailable'));
    const value=await response.json();
    if(value.version!==1||!Number.isSafeInteger(value.revision)||value.locale!==null&&!localeManifest.locales.some(l=>l.id===value.locale))throw Error(t('portal.language.unavailable'));
    return value;
  }
  function accept(value){if(own!==generation)return;revision=value.revision;choice.value=value.locale||'';setPresentationLocaleOverride(value.locale);}
  async function load(){save.disabled=choice.disabled=reload.disabled=true;try{accept(await request());}catch(error){if(own===generation)feedback.textContent=error.message;}finally{if(own===generation)save.disabled=choice.disabled=revision===null;reload.disabled=false;}}
  save.onclick=async()=>{if(saving||revision===null)return;saving=true;save.disabled=choice.disabled=reload.disabled=true;
    try{const value=await request({version:1,expectedRevision:revision,locale:choice.value||null});if(own!==generation)return;accept(value);feedback.textContent=t('portal.language.saved');}
    catch(error){if(own===generation){feedback.textContent=error.message;feedback.setAttribute('role','alert');}}
    finally{saving=false;if(own===generation)save.disabled=choice.disabled=reload.disabled=false;}
  };
  reload.onclick=load;await load();
}
