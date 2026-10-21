import { newIdentity } from "./workstation-ui.js";
// Session-bound commands deliberately retain the state visible at click time. A stale
// End must conflict, never be silently retried against a newer session.
export function initializeScienceSessions({api},parent) {
  let busy=false, flight=null, message="", view="";
  const live=document.createElement('article');live.className='panel research-panel';live.id='science-sessions';
  parent.after(live);
  const element=(tag,value)=>{const n=document.createElement(tag);n.textContent=value;return n;};
  async function refresh(){
    if(busy||document.hidden||!live.offsetParent)return;
    try{
      const active=(await api('/api/state')).activeMission;flight=active?.id;
      if(!flight){view='';live.replaceChildren(element('h2','Science sessions'),element('p','Start a flight to arm science or begin a session. Flight recording and scientific collection are separate.'));return;}
      const result=await api(`/api/missions/${flight}/collection`);const state=result.state;
      const key=JSON.stringify([state.missionId,state.phase,state.currentSessionId,state.authority,state.manualAutomationHold,state.continuityHold,state.holds,result.fault,message,(result.sessions||[]).map(s=>[s.id,s.status,s.endedUtc])]);
      if(view===key)return;view=key; // telemetry revisions must not destroy keyboard focus
      live.replaceChildren(element('h2',`Science: ${state.phase}`),element('p',state.currentSessionId?`Current session: ${state.currentSessionId}`:'No open science session. Flight recording continues.'));
      live.append(element('p',`Automation: ${state.authority}${state.manualAutomationHold?' · pilot override — rearm required':''}${state.continuityHold?' · continuity hold':''}${result.fault?' · storage hold; resolve storage and rearm':''}`));
      const actions=[['StartData','Start new science session',!state.currentSessionId],['PauseData','Pause science collection',state.phase==='collecting'],['ResumeData','Resume science collection',state.phase==='paused'],['EndData','End current science session',!!state.currentSessionId],['RearmAutomation','Rearm automatic collection',true]];
      for(const [action,label,enabled] of actions){
        const button=element('button',label);button.type='button';button.disabled=!enabled;
        button.onclick=async()=>{if(busy)return;busy=true;button.disabled=true;
          try{for(let attempt=0;attempt<2;attempt++){
            const fresh=(await api(`/api/missions/${flight}/collection`)).state;
            if(fresh.currentSessionId!==state.currentSessionId||fresh.phase!==state.phase||fresh.authority!==state.authority||fresh.manualAutomationHold!==state.manualAutomationHold||fresh.continuityHold!==state.continuityHold)throw new Error("Science session or authority changed; no command was applied.");
            try{const outcome=await api(`/api/missions/${flight}/collection/control`,{version:1,missionId:state.missionId,requestId:newIdentity(),expectedRevision:fresh.revision,expectedSessionId:state.currentSessionId??null,action});message=outcome.event.reason;break;}
            catch(e){if(attempt||!e.message.includes('(409)'))throw e;}
          }}
          catch(e){message=`${e.message} Synchronize and issue a new command if still intended.`;}
          finally{busy=false;await refresh();}
        };live.append(button);
      }
      if(message){const status=element('p',message);status.setAttribute('role','status');live.append(status);}
      live.append(element('p','End current science session preserves its results. A new session remains possible. End mission / End flight separately stops flight recording. Rearming does not start or resume collection.'));
      const list=element('ul','');for(const session of result.sessions||[])list.append(element('li',`Session ${session.sequence} · ${session.id} · ${session.status} · ${session.startedUtc}${session.endedUtc?' → '+session.endedUtc:''}`));live.append(list);

    }catch(e){view='';live.replaceChildren(element('p','Science session state unavailable: '+e.message));}
  }
  const timer=setInterval(()=>{if(!live.isConnected){clearInterval(timer);window.removeEventListener('hashchange',refresh);return;}refresh();},2000);window.addEventListener('hashchange',refresh);refresh();
}
