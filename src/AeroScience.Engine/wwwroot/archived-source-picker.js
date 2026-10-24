import {button} from './workstation-ui.js';

// Saved source snapshots describe the acquired values even after originals are deleted.
export async function archivedSources(api,parent,missions=[],instrument=null,selected=null){
 const items=[];let next=null,stopped=false;const query={missions:missions.length?missions:null,instrument,limit:20};
 const load=async after=>{const page=await api('/api/science/workspace/sources',{...query,after});if(stopped||!parent.isConnected)return;for(const item of page.items)if(!items.some(old=>old.source.id===item.source.id))items.push(item);next=page.next;return page;};
 await load(0);
 if(selected&&!items.some(item=>item.source.id===selected)){const page=await api('/api/science/workspace/sources',{...query,sourceId:selected});for(const item of page.items)if(!items.some(old=>old.source.id===item.source.id))items.push(item);}
 const more=button(parent,'More saved source scenes',async()=>{if(next==null)return;await load(next);more.hidden=next==null;parent.dispatchEvent(new CustomEvent('aeroscience-archived-sources',{detail:items}));},'secondary');more.hidden=next==null;
 return {items,stop:()=>{stopped=true;}};
}
