import {test,expect} from '@playwright/test';

test('live duration uses actual source time and paused science remains independent, then clears with expired positions',async({page})=>{
 await page.goto('/#home');
 await page.evaluate(async()=>{
  const now=new Date().toISOString(),root=document.createElement('section');root.id='live-duration-fixture';document.querySelector('main').append(root);
  window.p06LiveData={checkedUtc:now,policy:{enabled:true},flights:[{sessionId:'fixture-live',flightId:'fixture-flight',displayLabel:'SYNTHETIC live duration fixture',flightStartedUtc:'2026-01-01T00:00:00Z',consentExpiresUtc:'2099-01-01T00:00:00Z',receivedUtc:now,cards:[],current:{source:'simulated',state:'connected',sourceUtc:'2026-01-01T00:01:30Z',aircraft:'Synthetic test aircraft',latitude:40,longitude:-75,science:{phase:'paused',instruments:[]}}}]};
  (await import('/organization-live-ui.js')).organizationLiveUi({root,read:async()=>structuredClone(window.p06LiveData),back:()=>{}});
 });
 const root=page.locator('#live-duration-fixture');await root.getByRole('button',{name:'SYNTHETIC live duration fixture',exact:true}).click();const detail=root.getByRole('region',{name:'Selected shared flight',exact:true});await expect(detail).toContainText('Flight duration at last source update: 0:01:30');await expect(detail).toContainText('Science collection is paused');await expect(detail).toContainText('It does not establish recording continuity or science collection');
 await page.evaluate(()=>{window.p06LiveData.flights[0].flightStartedUtc=null;});await root.getByRole('button',{name:'Refresh Live Flights',exact:true}).click();await expect(detail).toContainText('Flight duration at last source update: Unavailable');
 await page.evaluate(()=>{window.p06LiveData.flights[0].receivedUtc=new Date(Date.now()-31000).toISOString();window.p06LiveData.checkedUtc=new Date().toISOString();});await root.getByRole('button',{name:'Refresh Live Flights',exact:true}).click();await expect(detail).not.toContainText('Position:');await expect(detail).not.toContainText('Science collection is paused');await expect(detail).toContainText('Flight duration at last source update: Unavailable');
});
