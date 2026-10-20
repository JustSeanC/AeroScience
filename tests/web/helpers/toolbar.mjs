import { expect } from '@playwright/test';
import { readFileSync } from 'node:fs';

const folder = 'msfs-toolbar/PackageSources/html_ui/InGamePanels/AeroScience/';

// Representative bounded wrappers only. This does not implement or verify the
// simulator's native dragging, resizing, input routing or HTML-import templates.
export async function mountToolbar(page, { autoReply = false, activeMission = null, mapFails = false } = {}) {
  const html = readFileSync(folder + 'AeroScience.html', 'utf8')
    .replace(/<script[\s\S]*?<\/script>/g, '').replace(/<link[^>]*>/g, '');
  await page.setViewportSize({ width: 1280, height: 1000 });
  await page.setContent(html);
  await page.addStyleTag({ content: readFileSync(folder + 'AeroScience.css', 'utf8') });
  await page.evaluate(({ autoReply, activeMission }) => {
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
    window.checkAutoload = () => {};
    window.Include = { addScript: (_path, callback) => setTimeout(callback, 0) };
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
    window.RegisterCommBusListener = callback => {
      window.listenerRegistrations++; setTimeout(callback, 0); return window.bus;
    };
    frame.classList.remove('panelInvisible');
  }, { autoReply, activeMission });
  await page.evaluate(data => {window.AeroScienceCountries=data[0];window.AeroSciencePlaces=data[1];window.AeroScienceAirports=data[2];}, ['countries','places','airports'].map(name=>JSON.parse(readFileSync('src/AeroScience.Engine/wwwroot/geography/'+name+'.geojson','utf8'))));
  await page.addScriptTag({ content: readFileSync('src/AeroScience.Engine/wwwroot/geographic-view.js', 'utf8') });
  if(mapFails)await page.evaluate(()=>{window.AeroScienceMovingMap=class{constructor(){throw Error('Injected browser fixture renderer failure');}};});
  await page.addScriptTag({ content: readFileSync(folder + 'AeroScience.js', 'utf8') });
  await expect.poll(() => page.evaluate(() => window.bus.sent.length)).toBeGreaterThan(0);
  if (autoReply) await expect(page.locator('#as-source')).toHaveText('SIMULATED TEST DATA');
}
