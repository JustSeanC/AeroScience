import {test,expect} from '@playwright/test';
const origin='http://127.0.0.1:5080';
async function post(request,path,data={}){const session=await(await request.post('/api/session',{headers:{Origin:origin},data:{}})).json();const response=await request.post(path,{data,headers:{Origin:origin,'X-AeroScience-CSRF':session.csrf}});expect(response.ok(),await response.text()).toBe(true);return response.json();}

for(const action of ['zoom','zoom-back','pan'])test(`P08a legacy Science Map honors ${action} while initial native records are held`,async({page,request})=>{
 const f=await post(request,'/api/testing/remote/Terrain'),areas=[],errors=[];page.on('pageerror',e=>errors.push(e.message));
 page.on('request',r=>{if(new URL(r.url()).pathname==='/api/science/workspace/overview'&&r.method()==='POST'){const area=r.postDataJSON()?.area;if(area)areas.push(area);}});
 let release,started,first=true;const barrier=new Promise(resolve=>release=resolve),held=new Promise(resolve=>started=resolve);
 await page.route(`**/api/science/products/${f.product.id}/map-layer?measurement=elevation`,async route=>{if(first){first=false;started();await barrier;}await route.continue();});
 await page.addInitScript(()=>{let leaflet;Object.defineProperty(window,'L',{configurable:true,get:()=>leaflet,set(value){leaflet=value;value?.Map?.addInitHook(function(){if(this.getContainer().closest('.workspace-map'))window.p08aScienceMap=this;});}});});
 await page.goto(`/#workspace/map?missions=${f.missionId}&instrument=terrain-scanner`);await held;
 const finalZoom=await page.evaluate(action=>{const map=window.p08aScienceMap;if(action==='zoom')map.setZoom(5,{animate:false});else if(action==='zoom-back'){map.setZoom(5,{animate:false});map.setZoom(9,{animate:false});}else map.panTo([map.getCenter().lat+.0005,map.getCenter().lng+.0005],{animate:false});return map.getZoom();},action);
 const expected=await page.evaluate(()=>{const b=window.p08aScienceMap.getBounds();return {west:b.getWest(),south:Math.max(-90,b.getSouth()),east:b.getEast(),north:Math.min(90,b.getNorth())};});
 release();const holder=page.locator('.workspace-map');await expect.poll(()=>{const actual=areas.at(-1);return actual?Math.max(...Object.keys(expected).map(key=>Math.abs(actual[key]-expected[key]))):Infinity;}).toBeLessThan(1e-10);
 await expect(holder).toHaveAttribute('data-overview-zoom',String(finalZoom));
 if(action!=='pan'){
  const extents=await page.evaluate(()=>{const extents=[];window.p08aScienceMap.eachLayer(layer=>{if(layer.options?.className==='science-acquisition-extent')extents.push({fill:layer.options.fill,mounted:window.p08aScienceMap.hasLayer(layer)});});return extents;});
  expect(extents.length).toBeGreaterThan(0);expect(extents.length).toBeLessThanOrEqual(96);expect(extents.every(e=>e.mounted&&!e.fill)).toBe(true);
 }
 expect((await(await request.get(`/api/science/products/${f.product.id}`)).json()).product.sha256).toBe(f.product.sha256);expect(errors).toEqual([]);
});
