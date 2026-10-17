import { test, expect } from '@playwright/test';

test.afterEach(async ({ request }) => {
  const headers = { Origin: 'http://127.0.0.1:5080' };
  const session = await (await request.post('/api/session', { data: {}, headers })).json();
  const state = await (await request.get('/api/state')).json();
  if (state.activeMission) await request.post(`/api/missions/${state.activeMission.id}/end`, {
    data: {}, headers: { ...headers, 'X-AeroScience-CSRF': session.csrf }
  });
});

test('two displays share recording, history and safe mission detail', async ({ page, context }) => {
  const errors = []; page.on('pageerror', error => errors.push(error.message));
  await page.goto('/');
  await expect(page.locator('#source-badge')).toHaveText('SIMULATED TELEMETRY');
  await expect(page.locator('#altitude')).not.toHaveText('—');
  await expect(page.locator('#mach')).toHaveText('Mach 0.82');
  await expect(page.locator('#sampling-label')).toContainText('Target');
  const name = '<img src=x onerror=alert(1)> · Browser survey';
  await page.getByLabel('MISSION NAME', { exact: true }).fill(name);
  await expect(page.getByLabel('SCIENCE OPERATING MODE')).toHaveValue('universal');
  await page.getByLabel('SCIENCE OPERATING MODE').selectOption('realistic');
  await expect(page.locator('#mode-help')).toContainText('future release');
  await page.getByRole('button', { name: 'Start mission', exact: true }).click();
  await expect(page.locator('#active-name')).toHaveText(name);
  await expect.poll(async () => Number((await page.locator('#active-samples').textContent()).replaceAll(',', ''))).toBeGreaterThan(1);
  const second = await context.newPage(); await second.goto('/');
  await expect(second.locator('#active-name')).toHaveText(name);
  await expect(second.locator('#active-mode')).toContainText('Realistic Mode');
  await second.getByRole('button', { name: 'End mission', exact: true }).click();
  await expect(page.locator('#mission-idle')).toBeVisible();
  await expect(page.getByLabel('SCIENCE OPERATING MODE')).toHaveValue('universal');
  await page.getByRole('link', { name: 'Mission History' }).click();
  await expect(page.getByRole('heading', { name, exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'View mission' }).first().click();
  await expect(page.getByRole('dialog')).toBeVisible();
  await expect(page.locator('#detail-content')).toContainText('completed');
  await expect(page.locator('#detail-content')).toContainText('simulated');
  await expect(page.locator('#detail-content')).toContainText('Realistic Mode');
  const [download] = await Promise.all([
    page.waitForEvent('download'),
    page.getByRole('link', { name: 'Export complete mission (JSON)' }).click()
  ]);
  expect(download.suggestedFilename()).toMatch(/^AeroScience-mission-.*\.json$/);
  await expect(page.locator('#detail-content img')).toHaveCount(0);
  await page.getByRole('button', { name: 'Close mission detail' }).click();
  await page.reload(); await expect(page.getByRole('heading', { name, exact: true })).toBeVisible();
  expect(errors).toEqual([]); await second.close();
});

for (const viewport of [{ width: 744, height: 1133 }, { width: 375, height: 812 }]) {
  test(`responsive touch layout at ${viewport.width}px`, async ({ page }) => {
    await page.setViewportSize(viewport); await page.goto('/');
    await expect(page.locator('#altitude')).not.toHaveText('—');
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    expect((await page.getByRole('button', { name: 'Start mission', exact: true }).boundingBox()).height).toBeGreaterThanOrEqual(44);
    await page.getByRole('link', { name: 'Settings', exact: true }).click();
    await expect(page.locator('#settings-content')).toContainText('Disabled — localhost only');
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  });
}

test('display closure does not stop recording', async ({ page, context }) => {
  await page.goto('/'); await page.getByLabel('MISSION NAME', { exact: true }).fill('Display independence');
  await page.getByRole('button', { name: 'Start mission', exact: true }).click();
  await expect(page.locator('#active-name')).toHaveText('Display independence');
  await page.close();
  const display = await context.newPage(); await display.goto('/');
  await expect(display.locator('#active-name')).toHaveText('Display independence');
  await expect.poll(async () => Number(await display.locator('#active-samples').textContent())).toBeGreaterThan(1);
  await display.getByRole('button', { name: 'End mission', exact: true }).click();
  await expect(display.locator('#mission-idle')).toBeVisible();
});
