import {expect} from '@playwright/test';
const origin='http://127.0.0.1:5080';
export async function removeRetainedFixtureSources(request,sourceIds){
 // A reviewed selection does not reserve a source-maintenance lease. Retained
 // preparation/inspection can start between preview and execution in the shared
 // real host. Wait only for that explicit busy response, never bypass the guard.
 const session=await(await request.post('/api/session',{headers:{Origin:origin},data:{}})).json();
 const headers={Origin:origin,'X-AeroScience-CSRF':session.csrf};
 const deadline=Date.now()+5000;
 while(true){
  const response=await request.post('/api/science/sources/deletion/preview',{headers,data:{sourceIds}});expect(response.ok(),await response.text()).toBeTruthy();const review=await response.json();
  expect(review.totalSources).toBe(sourceIds.length);
  if(review.blockedSources){
   const blockers=review.sources.flatMap(s=>s.blockers);
   expect(blockers.length).toBeGreaterThan(0);
   for(const reason of blockers)expect(reason).toBe('Scientific acquisition/import/analysis or source maintenance is active. Finish or cancel it before deletion.');
  }else{
   expect(review.eligibleSources).toBe(sourceIds.length);
   const execution=await request.post('/api/science/sources/deletion/execute',{headers,data:{previewId:review.id,selectionHash:review.selectionHash}});
   if(execution.ok()){const result=await execution.json();expect(result.state).toBe('Completed');expect(result.removedSources).toBe(sourceIds.length);break;}
   expect(execution.status()).toBe(409);const error=(await execution.json()).error;
   expect([
    'Finish active scientific acquisition, import or analysis before deleting external sources.',
    'Selected source is protected or cannot be safely verified. Nothing was removed: Scientific acquisition/import/analysis or source maintenance is active.',
   ]).toContain(error);
  }
  expect(Date.now(), 'Temporary source activity must quiesce; deletion protections were retained.').toBeLessThan(deadline);
  await new Promise(resolve=>setTimeout(resolve,50));
 }
 let after=0,through=null;do{const q=new URLSearchParams({limit:100,after});if(through!=null)q.set('through',through);const remaining=await(await request.get('/api/science/sources/page?'+q)).json();expect(remaining.items.some(item=>sourceIds.includes(item.source.id))).toBe(false);through=remaining.through;if(remaining.next==null)break;after=remaining.next;}while(true);
}
