import { test, expect } from '@playwright/test';
import { readFileSync } from 'node:fs';

test('toolbar sends selected mode through CommBus and renders central state (SDK mocked)', async ({ page }) => {
  const folder = 'msfs-toolbar/PackageSources/html_ui/InGamePanels/AeroScience/';
  const html = readFileSync(folder + 'AeroScience.html', 'utf8').replace(/<script[\s\S]*?<\/script>/g, '').replace(/<link[^>]*>/g, '');
  await page.setContent(html);
  await page.evaluate(() => {
    window.TemplateElement = class extends HTMLElement { connectedCallback() {} };
    window.checkAutoload = () => {};
    window.Include = { addScript: (_path, callback) => setTimeout(callback, 0) };
    window.bus = { handlers: {}, sent: [], on(event, callback) { this.handlers[event] = callback; }, off() {}, unregister() {},
      callSimConnect(event, payload) { this.sent.push({ event, command: JSON.parse(payload) }); } };
    window.RegisterCommBusListener = callback => { setTimeout(callback, 0); return window.bus; };
    document.getElementById('aeroscience-frame').classList.remove('panelInvisible');
  });
  await page.addScriptTag({ content: readFileSync(folder + 'AeroScience.js', 'utf8') });
  await expect.poll(() => page.evaluate(() => window.bus.sent.length)).toBeGreaterThan(0);
  await page.evaluate(() => {
    const requestId = window.bus.sent[0].command.requestId;
    window.bus.handlers['AeroScience.Reply.v1'](JSON.stringify({ protocol: 1, requestId, ok: true, state: {
      connection: { source: 'simulated', message: 'Test channel connected' }, telemetry: null, activeMission: null
    } }));
  });
  await page.locator('#as-mode').selectOption('realistic');
  await expect(page.locator('#as-mode-help')).toContainText('future release');
  await page.locator('#as-name').fill('Rotary-wing research');
  await page.locator('#as-start').click();
  const sent = await page.evaluate(() => window.bus.sent.find(x => x.command.op === 'start'));
  expect(sent.event).toBe('AeroScience.Command.v1'); expect(sent.command.mode).toBe('realistic');
  expect(sent.command.name).toBe('Rotary-wing research');
  await page.evaluate(requestId => {
    window.bus.handlers['AeroScience.Reply.v1'](JSON.stringify({ protocol: 1, requestId, ok: true, state: {
      connection: { source: 'simulated', message: 'Test channel connected' }, telemetry: null,
      activeMission: { id: '00000000-0000-4000-8000-000000000001', name: 'Rotary-wing research', mode: 'realistic', startedUtc: new Date().toISOString(), sampleCount: 5 }
    } }));
  }, sent.command.requestId);
  await expect(page.locator('#as-active-mode')).toContainText('Realistic Mode');
  await expect(page.locator('#as-source')).toHaveText('SIMULATED TEST DATA');
  await page.locator('#as-vr').click(); await expect(page.locator('#as-root')).toHaveClass(/compact/);
  await page.locator('#as-end').click();
  expect(await page.evaluate(() => window.bus.sent.find(x => x.command.op === 'end').command.missionId)).toBe('00000000-0000-4000-8000-000000000001');
  await page.evaluate(() => document.querySelector('ingamepanel-aeroscience').remove());
});
