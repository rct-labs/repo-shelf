// Discovery end-to-end test (acceptance 16a): runs the .NET desktop service
// (`RepoShelf.exe --service`) against a fake GitHub API and a fake DeepSeek
// API, then drives the real web UI in headless Chromium: add a project,
// generate its profile, run discovery, triage by keyboard (a / s / d) and
// find the accepted repository in the Library tab. No real network is used.
//
// The service is built with `dotnet build -c Release` unless
// REPO_SHELF_SERVER_BIN points at an existing RepoShelf.exe.
// Run with: pnpm test:e2e:discover

import { spawn, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import http from 'node:http';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const rootDir = path.join(__dirname, '..', '..');
const TOKEN = 'e2e-discover-token';
const SECRET = 'E2E_SECRET_MUST_NOT_LEAVE_THE_MACHINE';
const PROJECT = 'e2e-shelf';

const NEEDS = [
  'Parse Markdown into safe HTML for README previews.',
  'Run fast full-text search over saved repositories.',
  'Drive a real browser in end-to-end tests.',
  'Schedule weekly background jobs reliably.',
  'Package a desktop app with automatic updates.',
];
const QUERIES = ['markdown sanitizer', 'browser automation'];

// Fake upstream repositories. Scores come from the fake model; `epsilon`
// gets a malformed reply and `delta` scores below the 60 threshold.
const now = Date.now();
const iso = (daysAgo) => new Date(now - daysAgo * 86_400_000).toISOString();
const REPOS = [
  { id: 910001, name: 'alpha', stars: 9000, score: 90, need: 0 },
  { id: 910002, name: 'beta', stars: 7000, score: 80, need: 2 },
  { id: 910003, name: 'gamma', stars: 5000, score: 70, need: 1 },
  { id: 910004, name: 'delta', stars: 3000, score: 40, need: 3 },
  { id: 910005, name: 'epsilon', stars: 1000, score: null, need: 4 },
].map((r) => ({ ...r, fullName: `fakeorg/${r.name}` }));

function log(step) {
  console.log(`[discover] ${step}`);
}

function repoJson(r) {
  return {
    id: r.id,
    name: r.name,
    full_name: r.fullName,
    owner: { login: 'fakeorg' },
    html_url: `https://github.com/${r.fullName}`,
    description: `${r.name} fixture repository`,
    topics: ['fixture'],
    language: 'TypeScript',
    license: { spdx_id: 'MIT' },
    archived: false,
    fork: false,
    stargazers_count: r.stars,
    default_branch: 'main',
    created_at: iso(365),
    pushed_at: iso(3),
  };
}

function listen(server) {
  return new Promise((resolve) => {
    server.listen(0, '127.0.0.1', () => resolve(server.address().port));
  });
}

function send(res, status, body, type = 'application/json') {
  res.writeHead(status, { 'content-type': type });
  res.end(typeof body === 'string' ? body : JSON.stringify(body));
}

function startFakeGitHub() {
  const counts = { search: 0, readme: 0, repo: 0, other: 0 };
  const server = http.createServer((req, res) => {
    const url = new URL(req.url, 'http://fake');
    let m;
    if (url.pathname === '/search/repositories') {
      counts.search += 1;
      return send(res, 200, { total_count: REPOS.length, items: REPOS.map(repoJson) });
    }
    if ((m = url.pathname.match(/^\/repos\/fakeorg\/([^/]+)\/readme$/))) {
      counts.readme += 1;
      return send(res, 200, `# ${m[1]}\n\nFixture README for ${m[1]}.`, 'text/plain');
    }
    if ((m = url.pathname.match(/^\/repos\/fakeorg\/([^/]+)$/))) {
      counts.repo += 1;
      const r = REPOS.find((x) => x.name === m[1]);
      return r ? send(res, 200, repoJson(r)) : send(res, 404, { message: 'Not Found' });
    }
    counts.other += 1;
    return send(res, 404, { message: 'Not Found' });
  });
  return { server, counts };
}

function startFakeDeepSeek() {
  const calls = { profile: 0, score: 0, other: 0, bodies: [] };
  const reply = (res, content) => send(res, 200, {
    id: 'fake', object: 'chat.completion', model: 'deepseek-chat',
    choices: [{ index: 0, message: { role: 'assistant', content }, finish_reason: 'stop' }],
  });
  const server = http.createServer((req, res) => {
    let raw = '';
    req.on('data', (d) => { raw += d; });
    req.on('end', () => {
      if (req.method !== 'POST' || !req.url.endsWith('/chat/completions')) {
        calls.other += 1;
        return send(res, 404, { error: 'not found' });
      }
      calls.bodies.push(raw);
      const body = JSON.parse(raw);
      const system = body.messages?.[0]?.content || '';
      const user = body.messages?.[1]?.content || '';
      if (system.startsWith('You profile a local software project')) {
        calls.profile += 1;
        return reply(res, JSON.stringify({
          needs: NEEDS, queries: QUERIES, languages: ['TypeScript'], dependencies: ['marked'],
        }));
      }
      if (system.startsWith('You judge whether')) {
        calls.score += 1;
        const name = user.match(/^Repository: (\S+)/m)?.[1];
        const r = REPOS.find((x) => x.fullName === name);
        if (!r || r.score == null) return reply(res, 'this is not json');
        return reply(res, JSON.stringify({
          score: r.score, matchedNeed: NEEDS[r.need], cost: 'low', reason: `${r.name} fits the need.`,
        }));
      }
      calls.other += 1;
      return reply(res, '{}');
    });
  });
  return { server, calls };
}

function resolveServiceBin() {
  if (process.env.REPO_SHELF_SERVER_BIN) return process.env.REPO_SHELF_SERVER_BIN;
  log('building app/RepoShelf (Release)…');
  const r = spawnSync('dotnet', ['build', 'app/RepoShelf/RepoShelf.csproj', '-c', 'Release', '--nologo', '-v', 'q'], {
    cwd: rootDir, encoding: 'utf8',
    env: { ...process.env, DOTNET_CLI_UI_LANGUAGE: 'en' },
  });
  if (r.status !== 0) {
    process.stdout.write(`${r.stdout || ''}${r.stderr || ''}`.split('\n').slice(-30).join('\n'));
    throw new Error('dotnet build failed');
  }
  const bin = path.join(rootDir, 'app', 'RepoShelf', 'bin', 'Release', 'net10.0-windows', 'win-x64', 'RepoShelf.exe');
  if (!fs.existsSync(bin)) throw new Error(`service executable not found: ${bin}`);
  return bin;
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

async function waitForHealth(port, timeoutMs = 30_000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    try {
      const res = await fetch(`http://127.0.0.1:${port}/api/health`);
      if (res.ok) return;
    } catch { /* retry */ }
    await new Promise((r) => setTimeout(r, 250));
  }
  throw new Error('service did not become healthy');
}

// Resolves once the child has exited, so the next build step never finds
// RepoShelf.exe (and its DLLs) still locked.
function stopService(child) {
  if (!child || child.exitCode !== null || child.signalCode !== null) return Promise.resolve();
  return new Promise((resolve) => {
    const timer = setTimeout(() => {
      if (process.platform === 'win32') spawnSync('taskkill', ['/pid', String(child.pid), '/t', '/f']);
      resolve();
    }, 10_000);
    child.once('exit', () => { clearTimeout(timer); resolve(); });
    child.kill();
  });
}

async function api(port, pathname, opts = {}) {
  const res = await fetch(`http://127.0.0.1:${port}${pathname}`, {
    ...opts,
    headers: { 'x-reposhelf-token': TOKEN, 'content-type': 'application/json', ...(opts.headers || {}) },
  });
  return { status: res.status, json: await res.json().catch(() => null) };
}

function makeProjectDir() {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'repo-shelf-project-'));
  fs.writeFileSync(path.join(dir, 'README.md'), '# e2e shelf\n\nA fixture project that renders Markdown.\n');
  fs.writeFileSync(path.join(dir, 'package.json'), JSON.stringify({ name: 'e2e-shelf', dependencies: { marked: '^15.0.0' } }));
  fs.writeFileSync(path.join(dir, '.env'), `API_KEY=${SECRET}\n`);
  fs.mkdirSync(path.join(dir, 'memory'));
  fs.writeFileSync(path.join(dir, 'memory', 'notes.md'), `${SECRET}\n`);
  return dir;
}

async function main() {
  let failures = 0;
  const check = (name, cond) => {
    if (cond) {
      console.log(`  PASS ${name}`);
    } else {
      failures += 1;
      console.error(`  FAIL ${name}`);
    }
  };

  const bin = resolveServiceBin();
  const github = startFakeGitHub();
  const deepseek = startFakeDeepSeek();
  const githubPort = await listen(github.server);
  const deepseekPort = await listen(deepseek.server);
  const port = await getFreePort();
  const dataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'repo-shelf-data-'));
  const projectDir = makeProjectDir();

  const service = spawn(bin, ['--service'], {
    env: {
      ...process.env,
      REPO_SHELF_PORT: String(port),
      REPO_SHELF_DATA_DIR: dataDir,
      REPO_SHELF_TOKEN: TOKEN,
      REPO_SHELF_GITHUB_API: `http://127.0.0.1:${githubPort}`,
      REPO_SHELF_DEEPSEEK_API: `http://127.0.0.1:${deepseekPort}`,
    },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  service.stderr.on('data', (d) => process.stderr.write(`[service] ${d}`));
  let browser;

  try {
    await waitForHealth(port);
    log(`service ${path.relative(rootDir, bin)} on :${port}; fake GitHub :${githubPort}; fake DeepSeek :${deepseekPort}`);

    const settings = await api(port, '/api/settings');
    check('desktop service reports features.discover', settings.json?.data?.features?.discover === true);
    const key = await api(port, '/api/settings/deepseek-key', { method: 'PUT', body: JSON.stringify({ token: 'fake-key' }) });
    check('DeepSeek key stored', key.status === 200);

    browser = await chromium.launch({ headless: true });
    const page = await browser.newPage({ viewport: { width: 1100, height: 800 } });
    page.on('pageerror', (err) => { failures += 1; console.error(`  FAIL pageerror: ${err.message}`); });
    await page.goto(`http://127.0.0.1:${port}/`);
    await page.waitForSelector('#tabs:not(.hidden) .tab[data-tab="feed"]', { timeout: 10_000 });
    check('Recommendations tab is the default', await page.evaluate(() => document.getElementById('app').dataset.tab === 'feed'));

    // ---- Projects tab: add (no network), then generate the profile -------
    await page.click('.tab[data-tab="projects"]');
    await page.fill('#project-name', PROJECT);
    await page.fill('#project-path', projectDir);
    await page.click('#btn-add-project');
    await page.waitForSelector('.project-card .project-generate', { timeout: 10_000 });
    check('adding a project calls no upstream', github.counts.search + github.counts.readme + github.counts.repo === 0
      && deepseek.calls.bodies.length === 0);
    log('project added');

    await page.click('.project-card .project-generate');
    await page.waitForSelector('#modal-confirm[open]', { timeout: 5000 });
    await page.click('#btn-confirm');
    await page.waitForFunction(
      (first) => document.querySelector('.project-card .project-needs')?.value.includes(first),
      NEEDS[0], { timeout: 20_000 },
    );
    const filesRead = await page.locator('.project-card .project-file').allTextContents();
    check('profile read exactly README.md and package.json', JSON.stringify([...filesRead].sort()) === JSON.stringify(['README.md', 'package.json']));
    check('one profile call to DeepSeek', deepseek.calls.profile === 1);
    check('.env and memory/ never sent', deepseek.calls.bodies.every((b) => !b.includes(SECRET)));
    check('queries shown editable', (await page.inputValue('.project-card .project-queries')).includes(QUERIES[0]));
    log('profile generated');

    // ---- Recommendations: run, then keyboard triage ------------------------
    await page.click('.tab[data-tab="feed"]');
    await page.waitForSelector('#btn-run-now', { timeout: 10_000 });
    await page.click('#btn-run-now');
    try {
      await page.waitForFunction(() => document.querySelectorAll('.feed-card').length === 3, null, { timeout: 45_000 });
    } catch (err) {
      const latest = await api(port, '/api/discovery/runs/latest');
      console.error(`[discover] latest run: ${JSON.stringify(latest.json?.data?.run)}`);
      throw err;
    }
    const names = await page.locator('.feed-card .feed-card-name').allTextContents();
    check('feed shows only scores >= 60, best first', JSON.stringify(names) === JSON.stringify(['fakeorg/alpha', 'fakeorg/beta', 'fakeorg/gamma']));
    const run = (await api(port, '/api/discovery/runs/latest')).json?.data?.run;
    check('run finished done despite one malformed AI reply', run?.status === 'done');
    check('GitHub search requests within budget (<= 4, one per query)', github.counts.search === QUERIES.length);
    check('READMEs fetched only for the shortlist', github.counts.readme === REPOS.length);
    check('one scoring call per shortlisted repo', deepseek.calls.score === REPOS.length);
    const pendingSearch = await api(port, '/api/repos?q=alpha');
    check('pending candidates never appear in library search', pendingSearch.json?.data?.total === 0);
    log('run done: 3 candidates');

    await page.click('.feed-run-text');
    const cardCount = (n) => page.waitForFunction((x) => document.querySelectorAll('.feed-card').length === x, n, { timeout: 20_000 });
    await page.keyboard.press('a');
    await cardCount(2);
    log('a: accepted alpha');
    await page.keyboard.press('s');
    await cardCount(1);
    log('s: beta moved to later');
    await page.keyboard.press('d');
    await page.waitForSelector('.dismiss-menu', { timeout: 5000 });
    await page.keyboard.press('2');
    await cardCount(0);
    log('d 2: gamma dismissed (too heavy)');

    const byState = async (state) => (await api(port, `/api/discovery/candidates?state=${state}`)).json?.data?.candidates || [];
    const [accepted, later, dismissed] = await Promise.all([byState('accepted'), byState('later'), byState('dismissed')]);
    check('alpha accepted', accepted.length === 1 && accepted[0].fullName === 'fakeorg/alpha');
    check('beta later', later.length === 1 && later[0].fullName === 'fakeorg/beta');
    check('gamma dismissed with reason', dismissed.length === 1 && dismissed[0].fullName === 'fakeorg/gamma'
      && dismissed[0].dismissReason === 'too_heavy');

    const library = await api(port, '/api/repos');
    const items = library.json?.data?.items || [];
    check('library holds exactly the accepted repo', library.json?.data?.total === 1 && items[0]?.repo?.fullName === 'fakeorg/alpha');
    const annotation = items[0]?.repo?.annotation || {};
    check('accepted repo labelled with the project', (annotation.projects || []).includes(PROJECT));
    check('accepted repo status to_investigate', annotation.status === 'to_investigate');
    check('AI output left reason and notes empty', annotation.reason === '' && annotation.notes === '');

    // ---- Library tab shows the accepted repo -------------------------------
    await page.click('.tab[data-tab="library"]');
    await page.waitForFunction(
      () => [...document.querySelectorAll('.result-item')].some((n) => n.textContent.includes('alpha')),
      null, { timeout: 10_000 },
    );
    check('accepted repo visible in Library tab', (await page.locator('.result-item').count()) === 1);
    log('library shows fakeorg/alpha');

    check('no unexpected upstream calls', github.counts.other === 0 && deepseek.calls.other === 0);
  } finally {
    await browser?.close().catch(() => {});
    await stopService(service);
    github.server.close();
    deepseek.server.close();
    fs.rmSync(dataDir, { recursive: true, force: true });
    fs.rmSync(projectDir, { recursive: true, force: true });
  }

  if (failures > 0) {
    console.error(`[discover] FAILED with ${failures} failing check(s)`);
    process.exit(1);
  }
  console.log('[discover] ALL CHECKS PASSED');
}

main().catch((err) => {
  console.error('[discover] fatal:', err);
  process.exit(1);
});
