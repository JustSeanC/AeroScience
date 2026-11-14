import { expect } from '@playwright/test';
import { readFileSync } from 'node:fs';
import {compileNative,nativeFlightStatus} from '../../../scripts/build/native-javascript.mjs';
import {nativeCatalogAssets} from '../../../scripts/build/build-locales.mjs';
import {parse} from 'acorn';

const folder = 'msfs-toolbar/PackageSources/html_ui/InGamePanels/AeroScience/';

// Representative bounded wrappers only. This does not implement or verify the
// simulator's native dragging, resizing, input routing or HTML-import templates.
export async function mountToolbar(page, { autoReply = false, activeMission = null, mapFails = false, templateDelay = 0, script = null, htmlSource = null, busDelay=0, includeCallback=true, registrationFailures=0, legacyConnectivity=false } = {}) {
  const html = (htmlSource || readFileSync(folder + 'AeroScience.html', 'utf8'))
    .replace(/<script[\s\S]*?<\/script>/g, '').replace(/<link[^>]*>/g, '');
  await page.setViewportSize({ width: 1280, height: 1000 });
  await page.setContent(html);
  await page.addStyleTag({ content: readFileSync(folder + 'AeroScience.css', 'utf8') });
  await page.evaluate(({ autoReply, activeMission,busDelay,includeCallback,registrationFailures,legacyConnectivity }) => {
    const frame = document.getElementById('aeroscience-frame');
    Object.assign(frame.style, { position: 'absolute', left: '0px', top: '0px', width: '640px', height: '720px' });
    const content = document.createElement('div'); content.className = 'ingameUiContent';
    content.append(document.getElementById('as-root'));
    const wrapper = document.createElement('div'); wrapper.className = 'ingameUiWrapper'; wrapper.append(content);
    const header = document.createElement('ingame-ui-header');
    header.textContent = 'Browser layout fixture — native window not tested';
    Object.assign(header.style, { height: '48px', minHeight: '48px', fontSize: '12px', background: '#52687d' });
    frame.append(header, wrapper);
    window.TemplateElement = class extends HTMLElement { connectedCallback() {} };
    if(legacyConnectivity)Object.defineProperty(document.querySelector('ingamepanel-aeroscience'),'isConnected',{value:undefined});
    window.checkAutoload = () => {};
    window.Include = { addScript: (_path, callback) => {if(includeCallback&&callback)setTimeout(callback,0);} };
    window.listenerRegistrations = 0;
    window.bus = {
      handlers: {}, sent: [], unregistered: 0,
      state: {
        connection: { source: 'simulated', message: 'Synthetic test telemetry | no simulator connection' },
        telemetry: {
          timestampUtc: new Date().toISOString(), aircraft: 'Synthetic research trajectory',
          altitudeMslFeet: 60204, indicatedAirspeedKnots: 205, headingDegrees: 76, verticalSpeedFeetPerMinute: 96
        },
        activeMission
      },
      on(event, callback) { this.handlers[event] = callback; },
      off(event) { delete this.handlers[event]; },
      unregister() { this.unregistered++; },
      callSimConnect(event, payload) {
        const command = JSON.parse(payload); this.sent.push({ event, command });
        if(autoReply && command.op==='select-plan'){this.state.selectedPlanId=command.planId||null;setTimeout(()=>this.handlers['AeroScience.Reply.v1']?.(JSON.stringify({protocol:1,requestId:command.requestId,ok:true,state:this.state})),0);}
        if(autoReply && command.op==='map-plans')setTimeout(()=>this.handlers['AeroScience.Reply.v1']?.(JSON.stringify({protocol:1,requestId:command.requestId,ok:true,map:[]})),0);
        if (autoReply && command.op === 'state') setTimeout(() => {
          this.state.telemetry.timestampUtc = new Date().toISOString();
          this.handlers['AeroScience.Reply.v1']?.(JSON.stringify({
            protocol: 1, requestId: command.requestId, ok: true, state: this.state
          }));
        }, 0);
      }
    };
    const register=callback=>{window.listenerRegistrations++;if(registrationFailures-->0)return{on(){throw Error('Controlled SDK listener not ready');},unregister(){window.bus.unregistered++;}};setTimeout(callback,0);return window.bus;};
    if(busDelay)setTimeout(()=>window.RegisterCommBusListener=register,busDelay);else window.RegisterCommBusListener=register;
    frame.classList.remove('panelInvisible');
  }, { autoReply, activeMission,busDelay,includeCallback,registrationFailures,legacyConnectivity });
  await page.evaluate(data => {window.AeroScienceCountries=data[0];window.AeroSciencePlaces=data[1];window.AeroScienceAirports=data[2];}, ['countries','places','airports'].map(name=>JSON.parse(readFileSync('src/AeroScience.Engine/wwwroot/geography/'+name+'.geojson','utf8'))));
  for(const [name,content]of Object.entries(nativeCatalogAssets()))await page.addScriptTag({content:compileNative(content,name)});
  await page.addScriptTag({content:compileNative(readFileSync(folder+'AeroScience.I18n.js','utf8'))});
  await page.addScriptTag({content:compileNative(nativeFlightStatus(),'AeroScience.FlightStatus.js')});
  await page.addScriptTag({ content: compileNative(readFileSync('src/AeroScience.Engine/wwwroot/geographic-view.js', 'utf8')) });
  if(mapFails)await page.evaluate(()=>{window.AeroScienceMovingMap=class{constructor(){throw Error('Injected browser fixture renderer failure');}};});
  if(templateDelay)await page.evaluate(()=>{const panel=document.querySelector('ingamepanel-aeroscience');window.delayedToolbarChildren=Array.from(panel.childNodes);panel.replaceChildren();});
  const nativeScript=script||compileNative(readFileSync(folder + 'AeroScience.js', 'utf8'));
  parse(nativeScript,{ecmaVersion:2017});await page.addScriptTag({ content:nativeScript });
  if(templateDelay)await page.evaluate(delay=>setTimeout(()=>document.querySelector('ingamepanel-aeroscience').append(...window.delayedToolbarChildren),delay),templateDelay);
  await expect.poll(() => page.evaluate(() => window.bus.sent.length)).toBeGreaterThan(0);
  if (autoReply) await expect(page.locator('#as-source')).toHaveText('SIMULATED TEST DATA');
}

export async function mountRealEngine(page){
 // The simulator SDK document is a separate host, not the dashboard CSP. Supply a
 // test-only document for native inline wrappers; production CSP remains unchanged.
 await page.route('**/__native_sdk_fixture',route=>route.fulfill({contentType:'text/html',body:'<!doctype html><html><head></head><body></body></html>'}));await page.goto('/__native_sdk_fixture');await mountToolbar(page);await page.evaluate(async()=>{
 const session=await(await fetch('/api/session',{method:'POST',headers:{'Content-Type':'application/json'},body:'{}'})).json();
 window.bus.callSimConnect=function(event,payload){const command=JSON.parse(payload);this.sent.push({event,command});return fetch('/api/testing/toolbar',{method:'POST',headers:{'Content-Type':'application/json','X-AeroScience-CSRF':session.csrf},body:payload}).then(r=>r.text()).then(reply=>this.handlers['AeroScience.Reply.v1']?.(reply));};
 document.querySelector('ingamepanel-aeroscience').tick();
 });await expect.poll(()=>page.evaluate(()=>{const panel=document.querySelector('ingamepanel-aeroscience');return panel.state?.connection.source==='simulated'&&document.querySelector('#as-source').textContent===AeroScienceI18n.text('native.simulatedTestData','SIMULATED TEST DATA');})).toBe(true);}
