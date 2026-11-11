import {test,expect} from '@playwright/test';
const origin='http://127.0.0.1:5080';
test.beforeEach(({page})=>{page.on('pageerror',error=>console.error('Terrain browser error:',error.message));page.on('response',response=>{if(response.status()>=400&&response.url().includes('/api/science/'))console.error('Terrain API error:',response.status(),new URL(response.url()).pathname);});});
async function post(request,path,data={}){const session=await(await request.post('/api/session',{headers:{Origin:origin},data:{}})).json();const r=await request.post(path,{data,headers:{Origin:origin,'X-AeroScience-CSRF':session.csrf}});expect(r.ok(),await r.text()).toBeTruthy();return r.json();}
async function units(request,altitude){const current=await(await request.get('/api/display/units')).json();return post(request,'/api/display/units',{...current.preferences,altitude,distance:altitude==='feet'?'nauticalMiles':'kilometres',planningProfile:altitude==='feet'?'metric':'aviation'});}
test('terrain profile uses retained DEM, geographic distance, linked map location and live display preferences',async({page,request})=>{
 const f=await post(request,'/api/testing/remote/Terrain');await units(request,'feet');
 await page.goto(`/#workspace/map?missions=${f.missionId}&instrument=terrain-scanner`);const profile=page.getByRole('region',{name:'Elevation Profile',exact:true});await expect(profile).toBeVisible();await expect(page.locator('.raster-legend')).toContainText('ft');
 await profile.getByLabel('Elevation profile path',{exact:true}).selectOption('line');
 for(const [name,value]of [['A latitude','40.01'],['A longitude','-75.02'],['B latitude','40.01'],['B longitude','-74.98']])await profile.getByLabel('Profile '+name,{exact:true}).fill(value);
 await profile.getByRole('button',{name:'Generate Elevation Profile',exact:true}).click();await expect(profile.getByRole('status').first()).toContainText('profile ready');await expect(profile).toContainText('ft');await expect(profile).toContainText('NAVD88');
 const initial=await profile.locator('canvas').evaluate(canvas=>{const c=window.Chart.getChart(canvas);return {axis:c.options.scales.x.title.text,unit:c.options.scales.y.title.text,points:c.data.datasets[0].data.filter(p=>p.y!=null)};});expect(initial.axis).toContain('NM');expect(initial.unit).toContain('ft');expect(initial.points[0].y).toBeCloseTo(404.3635,3);expect(initial.points.at(-1).x).toBeGreaterThan(1);
 const cursor=profile.getByRole('slider',{name:'Inspect elevation profile location'});await cursor.focus();await cursor.press('End');await expect(profile.getByRole('status').last()).toContainText('Profile position:');
 await units(request,'metres');await expect.poll(()=>profile.locator('canvas').evaluate(canvas=>window.Chart.getChart(canvas).options.scales.y.title.text)).toContain('(m)');expect(await profile.locator('canvas').evaluate(canvas=>window.Chart.getChart(canvas).data.datasets[0].data.find(p=>p.y!=null).y)).toBe(123.25);
 await profile.screenshot({path:'artifacts/terrain-profile-display.png'});
 const raw=await(await request.get(`/api/science/products/${f.product.id}`)).json();expect(raw.product.sha256).toBe(f.product.sha256);expect(raw.bands[0].unit).toBe('m');
 await profile.getByRole('button',{name:'Draw profile line',exact:true}).click();await page.locator('.workspace-map .leaflet-container').click({position:{x:200,y:200}});await page.locator('.workspace-map .leaflet-container').click({position:{x:250,y:230}});await expect(profile.getByLabel('Profile A latitude',{exact:true})).not.toHaveValue('40.01');
});
test('3805 native terrain cells use bounded canvas rendering and preserve explicit native detail',async({page,request})=>{
 const f=await post(request,'/api/testing/remote/Terrain'),base=await(await request.get(`/api/science/products/${f.product.id}/map-layer?measurement=elevation`)).json();const template=base.features[0],positions=base.features.flatMap(f=>f.geometry.coordinates[0]);const westEdge=Math.min(...positions.map(p=>p[0])),eastEdge=Math.max(...positions.map(p=>p[0])),northEdge=Math.max(...positions.map(p=>p[1])),southEdge=Math.min(...positions.map(p=>p[1])),dx=(eastEdge-westEdge)/64,dy=(northEdge-southEdge)/64;const features=[];
 for(let y=0;y<64&&features.length<3805;y++)for(let x=0;x<64&&features.length<3805;x++){if(x>=28&&x<36&&y>=28&&y<36)continue;const west=westEdge+x*dx,north=northEdge-y*dy;features.push({...template,geometry:{type:'Polygon',coordinates:[[[west,north],[west+dx,north],[west+dx,north-dy],[west,north-dy],[west,north]]]},properties:{...template.properties,value:100+x+y}});}
 // Synthetic load/mask projection only; no mocked provider data is published as real evidence.
 await page.route(`**/api/science/products/${f.product.id}/map-layer?measurement=elevation`,r=>r.fulfill({json:{...base,features,minimum:100,maximum:226}}));await page.goto(`/#workspace/map?missions=${f.missionId}&instrument=terrain-scanner`);
 await expect(page.locator('.map-overview-counts')).toHaveAttribute('data-displayed-cells','3805');await expect(page.locator('.terrain-elevation-surface')).toHaveCount(1);expect(await page.locator('.workspace-map svg path').count()).toBeLessThan(100);
 const pixels=await page.locator('.terrain-elevation-surface').evaluate(c=>{const data=c.getContext('2d').getImageData(0,0,c.width,c.height).data;let painted=0;for(let i=3;i<data.length;i+=4)if(data[i])painted++;return {painted,transparent:c.width*c.height-painted,centerAlpha:data[(Math.floor(c.height/2)*c.width+Math.floor(c.width/2))*4+3]};});expect(pixels.painted).toBeGreaterThan(1000);expect(pixels.transparent).toBeGreaterThan(1000);expect(pixels.centerAlpha).toBe(0);await page.locator('.workspace-map').screenshot({path:'artifacts/terrain-dense-native-surface.png'});
 await page.getByRole('checkbox',{name:'Detailed native terrain cells',exact:true}).check();await expect(page.locator('.terrain-elevation-surface')).toHaveCount(1);const opacity=page.getByRole('slider',{name:'Science layer opacity',exact:true});await opacity.focus();await opacity.press('Home');await opacity.press('ArrowRight');await expect(opacity).toHaveValue('0.05');
 expect((await(await request.get(`/api/science/products/${f.product.id}`)).json()).product.sha256).toBe(f.product.sha256);
});
test('terrain comparison presentation updates units without changing canonical differences or source evidence',async({page,request})=>{
 const a=await post(request,'/api/testing/remote/Terrain'),b=await post(request,'/api/testing/remote/Terrain');await units(request,'feet');
 await page.goto(`/#workspace/compare?missions=${a.missionId},${b.missionId}`);await expect(page.locator('.comparison-card').first().locator('.scientific-legend')).toContainText('ft');
 await page.getByRole('button',{name:'Compare Recorded Values',exact:true}).click();const result=page.getByRole('region',{name:'Scientific comparison result',exact:true});await expect(result).toContainText('Constant difference: 0 ft');
 const plotted=()=>result.getByRole('img',{name:'Matched recorded values: Dataset A versus Dataset B',exact:true}).evaluate(c=>{const plot=window.Chart.getChart(c);return {unit:plot.options.scales.y.title.text,value:plot.data.datasets[0].data[0].x};});expect((await plotted()).unit).toContain('(ft)');expect((await plotted()).value).toBeCloseTo(404.3635,3);
 await units(request,'metres');await expect(result).toContainText('Constant difference: 0 m');await expect(page.locator('.comparison-card').first().locator('.scientific-legend')).toContainText(' m');expect((await plotted()).value).toBe(123.25);
 const raw=await post(request,'/api/science/workspace/compare',{products:[a.product.id,b.product.id],measurement:'elevation'});expect(raw.units).toEqual(['m']);expect(raw.differenceCells[0].baseline).toBe(123.25);expect(raw.meanDelta).toBe(0);
 expect((await(await request.get(`/api/science/products/${a.product.id}`)).json()).product.sha256).toBe(a.product.sha256);
});

test('retained terrain has truthful bounded acquisition outlines at whole-flight, regional and detailed zoom',async({page,request})=>{
 const f=await post(request,'/api/testing/remote/Terrain');let release,started,first=true;
 const barrier=new Promise(resolve=>{release=resolve;}),held=new Promise(resolve=>{started=resolve;});
 await page.route(`**/api/science/products/${f.product.id}/map-layer?measurement=elevation`,async route=>{if(first){first=false;started();await barrier;}await route.continue();});
 // ResearchMap deliberately uses Leaflet canvas. Inspect actual mounted/projected layers,
 // rather than assuming its marker class names create individual SVG DOM elements.
 await page.addInitScript(()=>{let leaflet;Object.defineProperty(window,'L',{configurable:true,get:()=>leaflet,set(value){leaflet=value;value?.Map?.addInitHook(function(){if(this.getContainer().closest('.workspace-map'))window.maintenanceScienceMap=this;});}});});
 const loaded=page.waitForResponse(response=>response.url().endsWith('/api/science/workspace/overview')&&response.ok()&&response.request().postDataJSON()?.area);
 await page.goto(`/#workspace/map?missions=${f.missionId}&instrument=terrain-scanner`);await loaded;await held;const map=page.locator('.workspace-map');
 // Change scale while the first native request is held: the old implementation
 // discarded this moveend during initialization and kept the zoom-17 inventory.
 await page.evaluate(()=>{window.maintenanceScienceMap.setZoom(5,{animate:false});});release();
 const overview=()=>page.evaluate(()=>{const map=window.maintenanceScienceMap,markers=[],extents=[];map.eachLayer(layer=>{const name=layer.options?.className;if(name==='science-overview-location')markers.push(!!(map.hasLayer(layer)&&map.getBounds().contains(layer.getLatLng())&&layer.getRadius()>=6&&layer.options.opacity>0&&map.getRenderer(layer)._container.isConnected));if(name==='science-acquisition-extent')extents.push({fill:layer.options.fill,mounted:map.hasLayer(layer)});});return {markers,extents};});
 for(const zoom of [5,9,15]){
  await expect.poll(()=>page.evaluate(()=>!!window.maintenanceScienceMap)).toBe(true);
  await page.evaluate(zoom=>{window.maintenanceScienceMap.setZoom(zoom,{animate:false});},zoom);
  await expect(map).toHaveAttribute('data-overview-zoom',String(zoom));
  await expect.poll(async()=>{const value=await overview();return value.markers.filter(Boolean).length;}).toBeGreaterThan(0);
  const rendered=await overview();expect(rendered.extents.length).toBeGreaterThan(0);expect(rendered.extents.length).toBeLessThanOrEqual(96);expect(rendered.extents.every(e=>e.mounted&&!e.fill)).toBe(true);
  expect(await map.locator('svg path').count()).toBeLessThan(100);await expect(page.locator('.raster-legend')).toContainText('Zoom in for individual collected points');
 }
 await expect(page.locator('.terrain-elevation-surface')).not.toHaveCount(0);expect((await(await request.get(`/api/science/products/${f.product.id}`)).json()).product.sha256).toBe(f.product.sha256);
 await map.screenshot({path:'artifacts/terrain-maintenance-overview.png'});
});
