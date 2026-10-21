import { defineConfig } from '@playwright/test';
export default defineConfig({
  globalSetup: './tests/web/helpers/auth-setup.mjs',
  globalTeardown: './tests/web/helpers/auth-teardown.mjs',
  testDir: './tests/web', fullyParallel: false, workers: 1,
  timeout: 30000, reporter: [['list'], ['json', { outputFile: 'artifacts/web-results.json' }]],
  use: { storageState: 'artifacts/browser-auth.json', baseURL: 'http://127.0.0.1:5080', headless: true, screenshot: 'only-on-failure',
    launchOptions: process.env.CHROMIUM_PATH ? { executablePath: process.env.CHROMIUM_PATH } : {} },
  outputDir: 'artifacts/browser-tests',
  webServer: { command: 'node scripts/build/test-server.mjs', url: 'http://127.0.0.1:5080/api/health', reuseExistingServer: false, timeout: 30000 }
});
