// Browser smoke test: loads the real extension into Chrome (or Edge), captures
// a repository through the popup, verifies offline pending-queue + retry, and
// checks search + hostile README sanitization in the web UI.
//
// Uses the real public GitHub API for metadata (octocat/Hello-World), so it
// needs network access. The extension is loaded into Playwright's Chromium
// build because branded Chrome/Edge >= 137 ignore --load-extension.
// Run with: pnpm test:e2e

import { spawn } from 'node:child_process';
import crypto from 'node:crypto';
import fs from 'node:fs';
import http from 'node:http';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const rootDir = path.join(__dirname, '..', '..');
const extensionDir = path.join(rootDir, 'extension');
const TOKEN = 'e2e-smoke-token';

function log(step) {
  console.log(`[smoke] ${step}`);
}

function getFreePort() {
  return new Promise((resolve, reject) => {
    const srv = http.createServer();
    srv.listen(0, '127.0.0.1', () => {
      const { port } = srv.address();
      srv.close(() => resolve(port));
    });
    srv.on('error', reject);
  });
}

function extensionIdFromManifest() {
  const manifest = JSON.parse(fs.readFileSync(path.join(extensionDir, 'manifest.json'), 'utf8'));
  const der = Buffer.from(manifest.key, 'base64');
  const hash = crypto.createHash('sha256').update(der).digest();
  let id = '';
  for (const byte of hash.subarray(0, 16)) {
    id += String.fromCharCode(97 + (byte >> 4)) + String.fromCharCode(97 + (byte & 15));
  }
  return id;
}

async function waitForHealth(port, timeoutMs = 10_000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const res = await fetch(`http://127.0.0.1:${port}/api/health`);
      if (res.ok) return;
    } catch { /* retry */ }
    await new Promise((r) => setTimeout(r, 200));
  }
  throw new Error('server did not become healthy');
}

function startServer(port, dataDir) {
  // REPO_SHELF_SERVER_BIN selects the published .NET desktop/service exe;
  // default is the Node.js reference implementation.
  const bin = process.env.REPO_SHELF_SERVER_BIN;
  const command = bin || process.execPath;
  const args = bin ? ['--service'] : [path.join(rootDir, 'server', 'index.js')];
  const child = spawn(command, args, {
    env: {
      ...process.env,
      REPO_SHELF_PORT: String(port),
      REPO_SHELF_DATA_DIR: dataDir,
      REPO_SHELF_TOKEN: TOKEN,
    },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  child.stderr.on('data', (d) => process.stderr.write(`[server] ${d}`));
  return child;
}

async function api(port, pathname, opts = {}) {
  const res = await fetch(`http://127.0.0.1:${port}${pathname}`, {
    ...opts,
    headers: { 'x-reposhelf-token': TOKEN, ...(opts.headers || {}) },
  });
  return { status: res.status, json: await res.json().catch(() => null) };
}

async function seedHostileReadme(port) {
  const backup = {
    app: 'repo-shelf',
    version: 1,
    repos: [{
      githubId: 999999001,
      owner: 'attacker',
      name: 'evil-readme',
      fullName: 'attacker/evil-readme',
      htmlUrl: 'https://github.com/attacker/evil-readme',
      description: 'hostile fixture',
      topics: [],
      language: null,
      licenseId: null,
      archived: false,
      pushedAt: null,
      stars: 0,
      defaultBranch: 'main',
      readme: [
        '# Evil README',
        '<script>window.__pwned = true;</script>',
        '<img src="x" onerror="window.__pwned = true">',
        '[click me](javascript:window.__pwned=true)',
        '<iframe src="https://evil.example/"></iframe>',
      ].join('\n\n'),
      readmeTruncated: false,
      annotation: { reason: '', notes: '', tags: [], projects: [], status: 'inbox' },
    }],
  };
  const { status } = await api(port, '/api/restore?mode=overwrite', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(backup),
  });
  if (status !== 200) throw new Error(`seeding hostile fixture failed: ${status}`);
}

async function launchBrowser() {
  // Branded Chrome/Edge builds (>= 137) ignore --load-extension for security,
  // so automated extension tests run on Playwright's Chromium build, which
  // keeps the flag. Manual loading in real Chrome/Edge (developer mode) is
  // documented in README and unchanged by this.
  const profile = fs.mkdtempSync(path.join(os.tmpdir(), 'repo-shelf-profile-'));
  const args = [
    `--disable-extensions-except=${extensionDir}`,
    `--load-extension=${extensionDir}`,
    '--no-first-run',
    '--no-default-browser-check',
  ];
  const context = await chromium.launchPersistentContext(profile, {
    channel: 'chromium',
    headless: false,
    args,
    viewport: { width: 1200, height: 800 },
  });
  return { context, profile, channel: 'chromium' };
}

async function main() {
  const port = await getFreePort();
  const dataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'repo-shelf-data-'));
  let server = startServer(port, dataDir);
  await waitForHealth(port);
  log(`server on :${port}, data ${dataDir}`);
  await seedHostileReadme(port);

  const expectedId = extensionIdFromManifest();
  log(`expected extension id: ${expectedId}`);
  const { context, profile, channel } = await launchBrowser();
  log(`browser channel: ${channel}`);

  let failures = 0;
  const check = (name, cond) => {
    if (cond) {
      console.log(`  PASS ${name}`);
    } else {
      failures += 1;
      console.error(`  FAIL ${name}`);
    }
  };

  // The background service worker confirms the extension actually loaded and
  // reports the true runtime extension id.
  const sw = context.serviceWorkers()[0] || await context.waitForEvent('serviceworker', { timeout: 15_000 });
  const extensionId = new URL(sw.url()).host;
  check('extension loaded with the pinned key id', extensionId === expectedId);

  try {
    // ---- Extension popup: pairing + capture -------------------------------
    const popup = await context.newPage();
    popup.on('pageerror', (err) => { failures += 1; console.error(`  FAIL popup pageerror: ${err.message}`); });
    await popup.goto(`chrome-extension://${extensionId}/popup.html`);
    await popup.evaluate(({ serverUrl, token }) => (
      chrome.storage.local.set({ rsConfig: { serverUrl, token }, rsLang: 'en' })
    ), { serverUrl: `http://127.0.0.1:${port}`, token: TOKEN });
    await popup.reload();
    await popup.waitForSelector('#capture:not(.hidden)', { timeout: 5000 });
    log('popup paired');

    // The popup is open as a tab, so the manual URL input is shown.
    await popup.fill('#input-url', 'https://github.com/octocat/Hello-World');
    await popup.fill('#input-reason', 'E2E smoke capture reason 冒烟');
    await popup.click('#btn-save');
    await popup.waitForSelector('#message.ok', { timeout: 30_000 });
    log('captured octocat/Hello-World via extension popup');

    let res = await api(port, '/api/repos?q=Hello-World');
    check('capture stored exactly once', res.json?.data?.total === 1);
    check('reason preserved', res.json?.data?.items?.[0]?.repo?.annotation?.reason === 'E2E smoke capture reason 冒烟');

    // ---- Pending queue while the service is offline -----------------------
    server.kill();
    await new Promise((r) => setTimeout(r, 500));
    await popup.fill('#input-url', 'https://github.com/octocat/Spoon-Knife');
    await popup.fill('#input-reason', '');
    await popup.click('#btn-save');
    await popup.waitForSelector('#message.info', { timeout: 10_000 });
    await popup.waitForSelector('#pending:not(.hidden)', { timeout: 5000 });
    const pendingCount = await popup.locator('.pending-item').count();
    check('offline capture queued as pending', pendingCount === 1);
    log('offline capture queued');

    server = startServer(port, dataDir);
    await waitForHealth(port);
    await popup.waitForSelector('.pending-item button.primary', { timeout: 5000 });
    await popup.click('.pending-item button.primary');
    await popup.waitForFunction(
      () => document.querySelectorAll('.pending-item').length === 0,
      { timeout: 30_000 },
    );
    res = await api(port, '/api/repos?q=Spoon-Knife');
    check('retry saved exactly one record after recovery', res.json?.data?.total === 1);
    log('pending retry succeeded after recovery');

    // ---- Web UI: search, detail, hostile README sanitization --------------
    const app = await context.newPage();
    app.on('pageerror', (err) => { failures += 1; console.error(`  FAIL app pageerror: ${err.message}`); });
    await app.goto(`http://127.0.0.1:${port}/`);
    await app.waitForSelector('.result-item', { timeout: 10_000 });
    const initialCount = await app.locator('.result-item').count();
    check('library lists saved repositories', initialCount >= 2);

    await app.fill('#search-input', '冒烟');
    await app.waitForFunction(
      () => document.querySelectorAll('.result-item').length === 1,
      { timeout: 5000 },
    );
    log('Chinese note search matched');
    await app.click('.result-item');
    await app.waitForSelector('#d-reason', { timeout: 5000 });
    const reasonValue = await app.inputValue('#d-reason');
    check('detail shows personal reason', reasonValue.includes('冒烟'));
    check('detail shows README content', (await app.locator('.readme-render').textContent()).length > 10);

    await app.fill('#search-input', 'evil-readme');
    await app.waitForFunction(
      () => document.querySelectorAll('.result-item').length === 1,
      { timeout: 5000 },
    );
    await app.click('.result-item');
    await app.waitForSelector('.readme-render', { timeout: 5000 });
    await app.waitForTimeout(300);
    const pwned = await app.evaluate(() => window.__pwned);
    check('hostile README scripts did not execute', pwned === undefined);
    check('no <script> survived sanitization', (await app.locator('.readme-render script').count()) === 0);
    check('no <iframe> survived sanitization', (await app.locator('.readme-render iframe').count()) === 0);
    check('no javascript: links survived', (await app.locator('.readme-render a[href^="javascript:"]').count()) === 0);
    check('no inline event handlers survived', (await app.locator('.readme-render [onerror]').count()) === 0);
    log('hostile README rendered inertly');

    // ---- Language switch --------------------------------------------------
    await app.selectOption('#lang-switch', 'zh-CN');
    await app.waitForTimeout(200);
    const addLabel = await app.locator('#btn-add').textContent();
    check('language switch renders Chinese UI', addLabel === '收藏仓库');
  } finally {
    await context.close().catch(() => {});
    server.kill();
    fs.rmSync(profile, { recursive: true, force: true });
    fs.rmSync(dataDir, { recursive: true, force: true });
  }

  if (failures > 0) {
    console.error(`[smoke] FAILED with ${failures} failing check(s)`);
    process.exit(1);
  }
  console.log('[smoke] ALL CHECKS PASSED');
}

main().catch((err) => {
  console.error('[smoke] fatal:', err);
  process.exit(1);
});
