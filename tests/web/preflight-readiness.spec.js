import {test,expect} from '@playwright/test';
test('unsaved Planner assesses real draft inputs without save, flight or downloads and marks edits stale',async({page,request})=>{
 await page.goto('/#planner');await page.getByRole('button',{name:'New mission plan',exact:true}).click();await page.getByLabel('Plan name',{exact:true}).fill('Unsaved readiness fixture');
 const panel=page.locator('#preflight-readiness');await panel.getByRole('button',{name:'Evaluate science readiness'}).click();await expect(panel).toContainText('design.objective');await expect(panel).toContainText('No research objective');
 expect((await(await request.get('/api/plans')).json()).some(p=>p.name==='Unsaved readiness fixture')).toBeFalsy();expect((await(await request.get('/api/state')).json()).activeMission).toBeNull();
 await page.getByLabel('Objective',{exact:true}).fill('Observe an atmospheric profile');await expect(panel.getByRole('status')).toContainText('Stale');await panel.getByRole('button',{name:'Evaluate science readiness'}).click();await expect(panel).toContainText('An explicit research objective');
});
test('an old concurrent readiness response cannot overwrite a newer draft assessment',async({page})=>{
 await page.goto('/#planner');await page.getByRole('button',{name:'New mission plan',exact:true}).click();
 let release;const held=new Promise(r=>release=r);let calls=0;
 await page.route('**/api/plans/readiness',async route=>{if(++calls===1)await held;const draft=route.request().postDataJSON();await route.fulfill({json:{assessedUtc:new Date().toISOString(),checks:[{id:draft.objective||'old',category:'Fixture',status:'caution',reasons:[draft.objective||'old result'],actions:[]}],limitations:[]}});});
 const panel=page.locator('#preflight-readiness');await panel.getByRole('button').click();await expect.poll(()=>calls).toBe(1);await page.getByLabel('Objective',{exact:true}).fill('Newer evidence');await panel.getByRole('button').click();await expect(panel).toContainText('Newer evidence');release();await page.waitForResponse(r=>r.url().endsWith('/api/plans/readiness'));await expect(panel).toContainText('Newer evidence');await expect(panel).not.toContainText('old result');
});
