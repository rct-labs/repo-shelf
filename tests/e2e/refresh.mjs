// Read-only browser regression against a running app; refresh jobs are mocked.
import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';

const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage();
  for (const file of ['app.js', 'i18n.js']) {
    await page.route(`**/${file}`, async route => route.fulfill({
      contentType: 'text/javascript', body: await fs.readFile(new URL(`../../public/${file}`, import.meta.url), 'utf8'),
    }));
  }
  let searches = 0;
  page.on('request', req => { if (new URL(req.url()).pathname === '/api/repos') searches++; });
  let failed = false;
  await page.route('**/api/jobs/**', async route => {
    const starting = route.request().method() === 'POST';
    await route.fulfill({ json: { ok: true, data: { job: {
      id: 'mock-refresh', status: starting ? 'running' : 'done',
      processed: 2, updated: failed ? 1 : 2, failed: failed ? 1 : 0,
      failures: failed ? [{ subject: '#2', code: 'github_rate_limited' }] : [],
    } } } });
  });
  await page.goto(process.env.REPO_SHELF_URL || 'http://127.0.0.1:4790');
  await page.click('#btn-settings');
  for (const partial of [false, true]) {
    failed = partial;
    const before = searches;
    await page.click('#btn-refresh-all');
    assert.equal(await page.isDisabled('#btn-refresh-all'), true);
    await page.waitForFunction(() => !document.querySelector('#btn-refresh-all').disabled);
    assert.ok(searches > before, 'completion reloads the library');
    const status = await page.textContent('#refresh-all-state');
    assert.ok(status.includes(partial ? 'github_rate_limited' : '2'));
  }
  await page.route('**/api/jobs/**', route => route.fulfill({ status: 503,
    json: { ok: false, error: { message: 'Refresh unavailable' } },
  }));
  await page.click('#btn-refresh-all');
  await page.waitForFunction(() => !document.querySelector('#btn-refresh-all').disabled);
  assert.equal(await page.textContent('#refresh-all-state'), 'Refresh unavailable');
  console.log('PASS: refresh completion, partial failure, request failure, button recovery, library reload');
} finally {
  await browser.close();
}
