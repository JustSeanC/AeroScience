import {openLibrary,applyBounds} from './helpers/library-ui.js';
import { test, expect } from '@playwright/test';
const origin = 'http://127.0.0.1:5080';
async function post(request, path, data = {}) {
  const session = await (await request.post('/api/session', { headers: { Origin: origin }, data: {} })).json();
  const response = await request.post(path, { headers: { Origin: origin, 'X-AeroScience-CSRF': session.csrf }, data });
  expect(response.ok(), await response.text()).toBeTruthy();
  return response.json();
}
test.beforeEach(async ({ request }) => {
  // The workspace fixture leaves scientific instruments enabled while its synthetic
  // aircraft keeps moving. Establish this scenario's explicit no-flight/no-collection
  // precondition; otherwise dozens of legitimate higher-priority requests precede it.
  expect((await (await request.get('/api/state')).json()).activeMission).toBeNull();
  await post(request, '/api/instruments', []);
  await post(request, '/api/science/prepare/cancel');
  await post(request, '/api/science/preparation/control', { action: 'resume' });
  await post(request, '/api/testing/library/setup');
  await post(request, '/api/testing/library/mode/normal');
  await post(request, '/api/testing/nasa/connection'); // Explicit synthetic credential; never a NASA login.
});
test.afterEach(async ({ request }) => {
  for (const job of (await (await request.get('/api/science/library/jobs')).json()).jobs)
    if (!['Completed', 'Cancelled'].includes(job.state)) await post(request, `/api/science/library/jobs/${job.id}/control`, { action: 'cancel' });
  await post(request, '/api/science/earthdata/disconnect');
  await post(request, '/api/testing/library/teardown');
});
async function discover(page, family) {
  await openLibrary(page);
  await page.getByLabel('Data family', { exact: true }).selectOption('hyperspectral');
  await page.getByLabel('Scientific provider / product', { exact: true }).selectOption('nasa-' + family);
  await page.getByLabel('Scene date from (UTC)', { exact: true }).fill(family === 'classic' ? '2008-01-01' : '2014-01-01');
  await page.getByLabel('Scene date to (UTC)', { exact: true }).fill(family === 'classic' ? '2009-01-01' : '2015-01-01');
  for (const [label, value] of [['West longitude', family === 'classic' ? '-119' : '-116'], ['East longitude', family === 'classic' ? '-117' : '-115'], ['South latitude', family === 'classic' ? '33' : '35'], ['North latitude', family === 'classic' ? '35' : '36']])
    await page.getByLabel(label, { exact: true }).fill(value);
  await applyBounds(page);await page.getByRole('button', { name: 'Find Available Data', exact: true }).click();
  await expect(page.getByLabel('Provider catalog results')).toContainText(family === 'classic' ? 'f080611' : 'ang20140621');
}
async function completionState(request, id) {
  const job = await (await request.get(`/api/science/library/jobs/${id}`)).json();
  if (job.state !== 'Queued') return job.state;
  const activity = await (await request.get('/api/science/activity')).json();
  const flight = await (await request.get('/api/state')).json();
  return JSON.stringify({ state: job.state, paused: activity.queue.paused, activeMission: flight.activeMission?.id ?? null, queue: activity.queue, plans: activity.plans });
}
for (const [family, bands] of [['classic', 224], ['ng', 425]]) test(`${family} direct preview, approval, original verification, automatic import and offline reuse`, async ({ page, request }) => {
  await discover(page, family);
  await expect(page.getByLabel('NASA Earthdata connection')).toContainText('Credential saved');
  const response = page.waitForResponse(r => r.url().endsWith('/api/science/library/nasa/preview') && r.request().method() === 'POST');
  await page.getByRole('button', { name: 'Preview NASA Download & Import', exact: true }).first().click();
  const previewResponse = await response; expect(previewResponse.ok(), await previewResponse.text()).toBeTruthy(); const job = await previewResponse.json();
  const review = page.getByLabel('Download preview', { exact: true });
  await expect(review).toContainText('complete original ENVI companions');
  await expect(review).toContainText('.hdr'); await expect(review).toContainText('.bin');
  expect((await (await request.get(`/api/science/library/jobs/${job.id}`)).json()).state).toBe('Awaiting Approval');
  await page.getByRole('button', { name: 'Download & Import', exact: true }).click();
  await expect.poll(() => completionState(request, job.id)).toBe('Completed');
  const completed = await (await request.get(`/api/science/library/jobs/${job.id}`)).json();
  expect(completed.downloadedBytes).toBeGreaterThan(0);expect(completed.progress.stage).toBe("Ready");expect(completed.progress.preparedOriginalBytes).toBe(completed.progress.totalOriginalBytes);expect(completed.progress.completedFiles).toBe(completed.preview.nasa.files.length); expect(JSON.stringify(completed)).not.toContain('synthetic_browser_earthdata_token');
  const sources = await (await request.get('/api/science/sources')).json();
  const source = sources.find(s => s.source.id === completed.items[0].sourceId).source;
  expect(source.manifest.bands).toHaveLength(bands); expect(source.manifest.acquisition.workflow).toBe('nasa-earthdata-original-envi');
  expect((await (await request.get('/api/state')).json()).activeMission).toBeNull();
  await post(request, '/api/science/earthdata/disconnect');
  await post(request, '/api/science/access', { onlineEnabled: false, privacyAccepted: false });
  const reuse = await post(request, '/api/science/library/nasa/preview', { selection: { provider: job.preview.selection.scenes[0].provider, granuleId: source.manifest.sceneId, revision: job.preview.selection.scenes[0].revision }, maximumTransferBytes: 67108864 });
  expect(reuse.preview.estimatedTransferBytes).toBe(0);
  await post(request, `/api/science/library/jobs/${reuse.id}/approve`, { selectionHash: reuse.preview.selectionHash });
  await expect.poll(async () => (await (await request.get(`/api/science/library/jobs/${reuse.id}`)).json()).state).toBe('Completed');
  await page.goto('/#library'); await expect(page.locator('main')).toContainText(source.manifest.name);
});
test('NASA connection and import controls retain explicit EMIT boundary and do not expose tokens', async ({ page, request }) => {
  await discover(page, 'classic');
  await expect(page.getByRole('button', { name: 'Connect Earthdata on this PC', exact: true })).toBeHidden();
  await expect(page.getByRole('button', { name: 'Disconnect Earthdata on this PC', exact: true })).toBeHidden();
  await page.getByText('Manage Earthdata connection', { exact: true }).click();
  await page.getByRole('button', { name: 'Disconnect Earthdata on this PC', exact: true }).click();
  await expect(page.getByLabel('NASA Earthdata connection')).toContainText('Not connected');
  await expect(page.getByRole('button', { name: 'Connect Earthdata on this PC', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Disconnect Earthdata on this PC', exact: true })).toBeHidden();
  const status = await (await request.get('/api/science/earthdata')).json(); expect(status.connected).toBe(false); expect(Object.keys(status).sort()).toEqual(['available', 'connected', 'message']);
  await page.getByLabel('Scientific provider / product', { exact: true }).selectOption('nasa-emit');
  await expect(page.getByRole('button', { name: 'Preview NASA Download & Import', exact: true })).toHaveCount(0);
  await expect(page.locator('main')).toContainText('native NetCDF-4 scientific import not yet supported');
});
