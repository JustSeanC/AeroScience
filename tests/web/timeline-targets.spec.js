import {test,expect} from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
test('coincident original timeline records retain exact identities, reachable targets and focus after reflow',async({page})=>{
 await page.goto('/#settings');await expect(page.locator('.presentation-settings')).toBeVisible();await page.evaluate(async()=>{
  const {missionEvidenceTimeline}=await import('/mission-timeline.js');const host=document.createElement('section');host.id='timeline-layout-fixture';document.getElementById('settings').append(host);const now=Date.now();
  const items=Array.from({length:16},(_,i)=>({lane:'flight',timestampUtc:new Date(now+(i<8?0:1000)).toISOString(),title:'Synthetic original event '+i,summary:'Synthetic evidence',reference:{source:'flightEvent',id:String(i)}}));
  window.timelineOriginal=JSON.stringify(items);window.timelineItems=items;window.timelineRequests=[];
  const api=async path=>{window.timelineRequests.push(path);if(path.includes('/timeline?'))return{flightId:'fixture',items,generatedUtc:new Date().toISOString(),flightStartedUtc:items[0].timestampUtc,recording:false,nextCursor:null,limitations:[]};throw Error('Synthetic original selection is deliberately unavailable.');};
  window.timelineFixture=missionEvidenceTimeline(host,api);window.timelineFixture.setFlight('fixture');
 });
 const root=page.locator('#timeline-layout-fixture'),points=root.locator('.evidence-time-point');await expect(points).toHaveCount(16);await points.first().focus();const focused=await points.first().getAttribute('data-evidence-reference');
 for(const width of [1440,390,820]){await page.setViewportSize({width,height:1000});await expect(points.first()).toBeFocused();await expect.poll(()=>points.evaluateAll(nodes=>{const boxes=nodes.map(n=>n.getBoundingClientRect());return boxes.every((a,i)=>a.width>=44&&a.height>=44&&boxes.every((b,j)=>i===j||a.right<=b.left||b.right<=a.left||a.bottom<=b.top||b.bottom<=a.top));})).toBe(true);expect((await new AxeBuilder({page}).include('#timeline-layout-fixture').withTags(['wcag2a','wcag2aa','wcag21aa','wcag22aa']).analyze()).violations).toEqual([]);}
 expect(await points.evaluateAll(nodes=>nodes.map(n=>n.dataset.evidenceReference))).toEqual(Array.from({length:16},(_,i)=>'flightEvent:'+i));expect(await page.evaluate(()=>JSON.stringify(window.timelineItems))).toBe(await page.evaluate(()=>window.timelineOriginal));await points.first().press('Enter');await expect(root).toContainText('Synthetic original selection is deliberately unavailable');expect((await page.evaluate(()=>window.timelineRequests)).some(path=>path.includes('referenceId=0'))).toBe(true);await expect(points.first()).toHaveAttribute('data-evidence-reference',focused);
});
