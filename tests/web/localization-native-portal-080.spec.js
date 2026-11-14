import {test,expect} from '@playwright/test';
import {mountRealEngine} from './helpers/toolbar.mjs';
import {localeBundle} from '../../scripts/build/build-locales.mjs';
const bundle=localeBundle(),origin='http://127.0.0.1:5080';
async function post(request,path,data={}){const session=await(await request.post('/api/session',{data:{},headers:{Origin:origin}})).json();const response=await request.post(path,{data,headers:{Origin:origin,'X-AeroScience-CSRF':session.csrf}});expect(response.ok(),await response.text()).toBe(true);return response.json();}
async function state(request){return(await request.get('/api/state')).json();}
test('five native languages follow compatible host metadata while recording, science and interval drafts remain independent',async({page,request})=>{
 test.setTimeout(150000);
 const flight=await post(request,'/api/missions',{name:'P07 immutable Zoë 東京 µm',armScience:true,instruments:[{instrumentId:'ambient-temperature',enabled:true,intervalMs:1000}]});
 try{
  await mountRealEngine(page);await page.locator('#as-payload-page').click();
  const interval=page.locator('#as-instruments > .as-instrument select').first();await interval.selectOption('5000');
  const commandCount=await page.evaluate(()=>window.bus.sent.filter(s=>!['state','research-state'].includes(s.command.op)&&!s.command.op.startsWith('map-')).length);
  for(const locale of bundle.manifest.locales){
   const current=await(await request.get('/api/settings/presentation')).json();
   await post(request,'/api/settings/presentation',{expectedRevision:current.preferences.revision,locale:locale.id,theme:'auto'});
   await expect(page.locator('ingamepanel-aeroscience')).toHaveAttribute('lang',locale.id);
   await expect(page.locator('#as-mission-page')).toHaveText(bundle.catalogs[locale.id].messages['toolbar.flight']);
   const expectedAcquisition=await page.evaluate(()=>AeroScienceI18n.text('toolbar.acquisitionState','',{status:document.querySelector('ingamepanel-aeroscience').nativeLabel('stopped')}));
   await expect(page.locator('#as-science-state')).toHaveText(expectedAcquisition);
   await expect(interval).toHaveValue('5000');await expect(page.locator('#as-root')).toHaveAttribute('data-recording','active');await expect(page.locator('#as-root')).toHaveAttribute('data-science','armed');
   expect((await state(request)).activeMission.id).toBe(flight.id);
   expect(await page.evaluate(()=>window.bus.sent.filter(s=>!['state','research-state'].includes(s.command.op)&&!s.command.op.startsWith('map-')).length)).toBe(commandCount);
  }
  expect((await(await request.get(`/api/missions/${flight.id}/science`)).json()).observations).toEqual([]);
 }finally{await post(request,`/api/missions/${flight.id}/end`);await post(request,'/api/instruments',[]);const current=await(await request.get('/api/settings/presentation')).json();await post(request,'/api/settings/presentation',{expectedRevision:current.preferences.revision,locale:'en-US',theme:'auto'});}
});
