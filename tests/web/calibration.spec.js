import {test,expect} from '@playwright/test';
const origin='http://127.0.0.1:5080';
async function pair(request){return (await request.post('/api/session',{data:{},headers:{Origin:origin}})).json();}
async function post(request,path,data={}){
 const session=await pair(request);
 const response=await request.post(path,{data,headers:{Origin:origin,'X-AeroScience-CSRF':session.csrf}});
 expect(response.ok(),await response.text()).toBeTruthy();return response;
}
test.afterEach(async({request})=>{
 await pair(request);
 const state=await(await request.get('/api/state')).json();
 if(state.activeMission)await post(request,`/api/missions/${state.activeMission.id}/end`);
 await post(request,'/api/instruments',[]);await post(request,'/api/mission-selection',{planId:null});
});

test('user calibration revisions persist, preserve originals and require explicit application',async({page,request})=>{
 await post(request,'/api/mission-selection',{planId:null});await post(request,'/api/instruments',[]);
 await page.goto('/#science');
 const instrument=page.locator('[data-payload-instrument="ambient-temperature"]');
 await instrument.getByLabel('Ambient thermometer',{exact:true}).check();
 const calibration=instrument.locator('.calibration-settings');await calibration.getByText('Calibration & provenance',{exact:true}).click();
 await expect(calibration).toContainText('Reference status: unknown');
 await calibration.getByRole('button',{name:'Create or revise user calibration',exact:true}).click();
 await calibration.getByLabel('Calibration author',{exact:true}).fill('Synthetic browser test author');
 await calibration.getByLabel('Basis / provenance',{exact:true}).fill('Synthetic scalar arithmetic test only; not genuine scientific acceptance.');
 await calibration.getByText('Add a separate user-adjusted scalar channel',{exact:true}).click();
 await calibration.getByLabel('Offset (original channel unit)',{exact:true}).fill('0.25');
 await calibration.getByRole('button',{name:'Save calibration revision',exact:true}).click();
 await expect(calibration.getByRole('status')).toContainText('revision 1');
 let configurations=await(await request.get('/api/instruments/configuration')).json();expect(configurations.find(c=>c.instrumentId==='ambient-temperature').calibration).toBeUndefined();
 await page.getByRole('button',{name:'Apply instrument configuration',exact:true}).click();
 await expect.poll(async()=>{const items=await(await request.get('/api/instruments/configuration')).json();return items.find(c=>c.instrumentId==='ambient-temperature').calibration?.revision;}).toBe(1);
 await page.reload({waitUntil:'domcontentloaded'});
 await page.locator('[data-calibration-instrument="ambient-temperature"] > summary').click();
 await expect(page.locator('[data-calibration-instrument="ambient-temperature"]')).toContainText('revision 1');
 const response=await post(request,'/api/missions',{name:'Synthetic calibrated browser flight',mode:'realistic'});const mission=await response.json();
 await expect.poll(async()=>{const rows=(await(await request.get(`/api/missions/${mission.id}/science`)).json()).observations;return rows.some(r=>r.measurementId==='temperature.user-adjusted');}).toBeTruthy();
 await post(request,`/api/missions/${mission.id}/end`);
 const rows=(await(await request.get(`/api/missions/${mission.id}/science`)).json()).observations;
 const derived=rows.find(r=>r.calibration),original=rows.find(r=>r.timestampUtc===derived.timestampUtc&&r.measurementId==='temperature');
 expect(original.provenance).toBe('simulatorReported');expect(derived.provenance).toBe('derived');
 expect(derived.value).toBeCloseTo(original.value+0.25,12);expect(derived.calibration.originalValue).toBe(original.value);
 expect(derived.source).toContain('Synthetic');expect(original.calibration).toBeUndefined();
 await page.goto('/#science');const revised=page.locator('[data-calibration-instrument="ambient-temperature"]');if(await revised.getAttribute('open')===null)await revised.locator('summary').click();
 await revised.getByRole('button',{name:'Create or revise user calibration',exact:true}).click();
 await revised.getByLabel('Offset (original channel unit)',{exact:true}).fill('1');
 await revised.getByRole('button',{name:'Save calibration revision',exact:true}).click();await expect(revised.getByRole('status')).toContainText('revision 2');
 const history=await(await request.get(`/api/missions/${mission.id}/export`)).json();
 expect(history.science.observations.find(r=>r.calibration).calibration.definition.revision).toBe(1);
});

test('external calibration is metadata only and paired clients cannot create definitions',async({page})=>{
 await page.goto('/#science');
 const remote=page.locator('[data-calibration-instrument="thermal-imager"]');await remote.locator('summary').click();
 await expect(remote).toContainText('Reference status: unavailable');
 await remote.getByRole('button',{name:'Create or revise user calibration',exact:true}).click();
 await expect(remote.getByText('Add a separate user-adjusted scalar channel',{exact:true}).locator('input')).toBeDisabled();
 const result=await page.evaluate(async()=>{
  const {calibrationSettings}=await import('/calibration-settings.js');const catalog=await(await fetch('/api/instruments/calibration')).json();catalog.canConfigure=false;
  const parent=document.createElement('div');const read=calibrationSettings(parent,{instrumentId:'ambient-temperature'},catalog,()=>{throw Error('must not configure remotely');});
  return {disabled:parent.querySelector('button').disabled,original:read(),warning:parent.textContent.includes('companion PC')};
 });
 expect(result).toEqual({disabled:true,original:null,warning:true});
});
