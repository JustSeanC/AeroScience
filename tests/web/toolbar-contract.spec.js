import { test, expect } from '@playwright/test';
import { mountToolbar } from './helpers/toolbar.mjs';

test('toolbar sends selected mode through CommBus and renders central state (SDK mocked)', async ({ page }) => {
  await mountToolbar(page);
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
  await expect(page.locator('#as-mode')).toHaveValue('universal');
  await expect(page.locator('#as-source')).toHaveText('SIMULATED TEST DATA');
  await page.locator('#as-end').click();
  expect(await page.evaluate(() => window.bus.sent.find(x => x.command.op === 'end').command.missionId)).toBe('00000000-0000-4000-8000-000000000001');
  await page.evaluate(() => document.querySelector('ingamepanel-aeroscience').remove());
});
