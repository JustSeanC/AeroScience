import {test,expect} from '@playwright/test';

test('replacing a map during zoom leaves no delayed Leaflet callbacks on removed panes',async({page})=>{
  const errors=[];page.on('pageerror',error=>errors.push(error.stack));
  await page.goto('/#control');await page.clock.install();
  await page.evaluate(async()=>{
    const {ResearchMap}=await import('/map.js');
    const holder=document.createElement('div');holder.style.cssText='position:fixed;top:0;left:0;width:600px;height:420px;z-index:10000';document.body.append(holder);
    const model=new ResearchMap(holder);await model.ready;
    model.map.setView([40,-75],8,{animate:false});
    model.map.once('zoomstart',()=>requestAnimationFrame(()=>{model.destroy();model.destroy();holder.remove();window.lifecycleZoomRemoved=true;}));
    model.map.setView([40.001,-74.999],9,{animate:true});
  });
  // Advance past Leaflet's 250 ms zoom fallback and queued animation frames.
  await page.clock.runFor(600);
  expect(await page.evaluate(()=>window.lifecycleZoomRemoved)).toBe(true);
  expect(errors).toEqual([]);
});

test('geography that finishes after disposal cannot add layers to a removed map',async({page})=>{
  let release;const held=new Promise(resolve=>release=resolve);
  await page.route('**/geography/airport-search.json',async route=>{await held;await route.continue();});
  await page.goto('/#control');
  await page.evaluate(async()=>{
    const {ResearchMap}=await import('/map.js');const holder=document.createElement('div');document.body.append(holder);
    const model=new ResearchMap(holder);window.lifecycleGeography=model;
    model.destroy();holder.remove();window.lifecycleLateLayerAdds=0;
    const native=model.map,add=native.addLayer;
    native.addLayer=function(...args){window.lifecycleLateLayerAdds++;return add.apply(this,args);};
  });
  release();await page.evaluate(()=>window.lifecycleGeography.ready);
  expect(await page.evaluate(()=>window.lifecycleLateLayerAdds)).toBe(0);
});

test('a removed product viewer does not resume drawing after a late protection response',async({page})=>{
  const errors=[];page.on('pageerror',error=>errors.push(error.stack));
  await page.goto('/#control');
  const result=await page.evaluate(async()=>{
    const {viewProduct}=await import('/remote-science.js'),{ResearchMap}=await import('/map.js');
    const parent=document.createElement('div');document.body.append(parent);
    const boundary=[{latitude:40,longitude:-75},{latitude:40.001,longitude:-75},{latitude:40.001,longitude:-74.999}];
    // UI lifecycle fixture only; not evidence of scientific acquisition.
    const data={product:{id:crypto.randomUUID(),datasetId:crypto.randomUUID(),instrumentId:'thermal-imager',width:1,height:1,availableViews:['temperature'],footprint:{boundary},warnings:[],provenance:'SYNTHETIC UI LIFECYCLE FIXTURE',sha256:'fixture'},source:{manifest:{name:'SYNTHETIC UI LIFECYCLE FIXTURE',kind:'thermal',provider:'test fixture',license:'test',verticalDatum:'unknown'},manifestSha256:'fixture'},bands:[],derived:{temperature:[300]},sampleLocations:[boundary[0]],processingMethods:[]};
    let release;const held=new Promise(resolve=>release=resolve);
    const pending=viewProduct(parent,data,async()=>{await held;return {isProtected:false};});
    parent.replaceChildren();
    const replacement=new ResearchMap(parent); // disposes the detached earlier map
    release();let failure=null;
    try{await pending;}catch(error){failure=error.stack;}
    const staleControls=parent.querySelectorAll('.remote-product-view').length;
    const staleProtection=[...parent.querySelectorAll('button')].filter(button=>button.textContent==='Protect Dataset'||button.textContent==='Remove Dataset Protection').length;
    replacement.destroy();parent.remove();return {failure,staleControls,staleProtection};
  });
  expect(result.failure).toBeNull();expect(result.staleControls).toBe(0);expect(result.staleProtection).toBe(0);expect(errors).toEqual([]);
});
