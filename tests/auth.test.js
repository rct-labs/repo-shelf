import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { openDatabase } from '../server/db.js';
import { createGitHubClient } from '../server/github.js';
import { createJobManager } from '../server/jobs.js';
import { createApp } from '../server/http.js';
import { FakeGitHub, makeRepo } from './helpers.js';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const publicDir = path.join(__dirname, '..', 'public');

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

async function startTestServer() {
  const port = await getFreePort();
  const db = openDatabase(':memory:');
  const fake = new FakeGitHub();
  const gh = createGitHubClient({ fetchImpl: fake.fetch, baseUrl: 'http://fake' });
  const jobs = createJobManager({ db, gh });
  const pairingToken = 'test-pairing-token';
  const server = createApp({
    db, gh, jobs, pairingToken,
    config: { port, host: '127.0.0.1', dataDir: ':memory:' },
    publicDir,
    vendorFiles: {},
  });
  await new Promise((resolve) => server.listen(port, '127.0.0.1', resolve));
  return {
    db, fake, server,
    base: `http://127.0.0.1:${port}`,
    token: pairingToken,
    close: () => new Promise((resolve) => server.close(resolve)),
  };
}

function api(base, path, { token = 'test-pairing-token', ...opts } = {}) {
  const headers = { ...(opts.headers || {}) };
  if (token) headers['x-reposhelf-token'] = token;
  return fetch(`${base}${path}`, { ...opts, headers });
}

test('API requests without a token are rejected', async () => {
  const srv = await startTestServer();
  try {
    const noToken = await api(srv.base, '/api/repos', { token: null });
    assert.equal(noToken.status, 401);
    const wrongToken = await api(srv.base, '/api/repos', { token: 'wrong' });
    assert.equal(wrongToken.status, 401);
    const body = await wrongToken.json();
    assert.equal(body.error.code, 'unauthorized');
  } finally {
    await srv.close();
  }
});

test('foreign website origins are rejected even with the token', async () => {
  const srv = await startTestServer();
  try {
    const res = await api(srv.base, '/api/repos', { headers: { origin: 'https://evil.example.com' } });
    assert.equal(res.status, 403);
    assert.equal((await res.json()).error.code, 'origin_not_allowed');
  } finally {
    await srv.close();
  }
});

test('browser-extension origins are accepted with the token and get CORS headers', async () => {
  const srv = await startTestServer();
  try {
    const origin = 'chrome-extension://abcdefghijklmnop';
    const res = await api(srv.base, '/api/repos', { headers: { origin } });
    assert.equal(res.status, 200);
    assert.equal(res.headers.get('access-control-allow-origin'), origin);

    const preflight = await fetch(`${srv.base}/api/repos`, { method: 'OPTIONS', headers: { origin } });
    assert.equal(preflight.status, 204);
    assert.equal(preflight.headers.get('access-control-allow-origin'), origin);

    const webPreflight = await fetch(`${srv.base}/api/repos`, { method: 'OPTIONS', headers: { origin: 'https://evil.example.com' } });
    assert.equal(webPreflight.headers.get('access-control-allow-origin'), null);
  } finally {
    await srv.close();
  }
});

test('spoofed Host headers are rejected (DNS rebinding protection)', async () => {
  const srv = await startTestServer();
  try {
    const res = await new Promise((resolve, reject) => {
      const req = http.request({
        host: '127.0.0.1',
        port: new URL(srv.base).port,
        path: '/api/repos',
        headers: { host: 'attacker.example.com', 'x-reposhelf-token': srv.token },
      }, resolve);
      req.on('error', reject);
      req.end();
    });
    assert.equal(res.statusCode, 403);
  } finally {
    await srv.close();
  }
});

test('health endpoint is public but minimal; UI HTML is served with embedded token', async () => {
  const srv = await startTestServer();
  try {
    const health = await api(srv.base, '/api/health', { token: null });
    assert.equal(health.status, 200);
    const healthBody = await health.json();
    assert.equal(healthBody.ok, true);
    assert.ok(!JSON.stringify(healthBody).includes(srv.token));

    const page = await fetch(`${srv.base}/`);
    assert.equal(page.status, 200);
    const html = await page.text();
    assert.ok(html.includes(srv.token), 'UI HTML carries the pairing token for same-origin API calls');
  } finally {
    await srv.close();
  }
});

test('capture through the HTTP API: create, dedupe, annotate, search', async () => {
  const srv = await startTestServer();
  try {
    srv.fake.addRepo(makeRepo({ id: 100, owner: 'octocat', name: 'Hello-World', description: 'sample' }), { readme: '# Hello World' });
    const created = await api(srv.base, '/api/repos', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ url: 'https://github.com/octocat/Hello-World', reason: 'api 测试', tags: ['demo'] }),
    });
    assert.equal(created.status, 201);
    const createdBody = await created.json();
    const repoId = createdBody.data.repo.id;

    const dupe = await api(srv.base, '/api/repos', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ url: 'https://github.com/octocat/Hello-World/pulls' }),
    });
    assert.equal(dupe.status, 200);
    assert.equal((await dupe.json()).data.repo.id, repoId);

    const patched = await api(srv.base, `/api/repos/${repoId}/annotation`, {
      method: 'PATCH',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ notes: '中文搜索词', status: 'tried' }),
    });
    assert.equal(patched.status, 200);

    const found = await api(srv.base, `/api/repos?q=${encodeURIComponent('中文搜索')}`);
    const foundBody = await found.json();
    assert.equal(foundBody.data.total, 1);
    assert.equal(foundBody.data.items[0].repo.id, repoId);
    assert.ok(foundBody.data.items[0].snippet.includes('<mark>'));
    assert.equal(foundBody.data.items[0].repo.readme, null, 'list payload omits README text');

    const detail = await api(srv.base, `/api/repos/${repoId}`);
    const detailBody = await detail.json();
    assert.equal(detailBody.data.repo.readme, '# Hello World');
    assert.equal(detailBody.data.repo.annotation.status, 'tried');
  } finally {
    await srv.close();
  }
});

test('export endpoint excludes credentials; restore endpoint round-trips', async () => {
  const srv = await startTestServer();
  try {
    srv.fake.addRepo(makeRepo({ id: 200, owner: 'a', name: 'b' }), { readme: '# B' });
    await api(srv.base, '/api/repos', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ url: 'https://github.com/a/b', reason: 'backup test' }),
    });
    await api(srv.base, '/api/settings/github-token', {
      method: 'PUT',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ token: 'ghp_super_secret' }),
    });

    const exported = await api(srv.base, '/api/export');
    assert.equal(exported.status, 200);
    const text = await exported.text();
    assert.ok(!text.includes('ghp_super_secret'));
    assert.ok(!text.includes(srv.token));
    assert.match(exported.headers.get('content-disposition') || '', /attachment/);

    // Wipe and restore through the API.
    const id = srv.db.prepare('SELECT id FROM repos').get().id;
    await api(srv.base, `/api/repos/${id}`, { method: 'DELETE' });
    assert.equal(srv.db.prepare('SELECT COUNT(*) c FROM repos').get().c, 0);

    const restored = await api(srv.base, '/api/restore?mode=overwrite', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: text,
    });
    assert.equal(restored.status, 200);
    assert.deepEqual(await restored.json(), { ok: true, data: { added: 1, skipped: 0, overwritten: 0 } });
    assert.equal(srv.db.prepare('SELECT COUNT(*) c FROM repos').get().c, 1);
  } finally {
    await srv.close();
  }
});
