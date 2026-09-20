import test from 'node:test';
import assert from 'node:assert/strict';

import { createGitHubClient } from '../server/github.js';

function responder(status, { body = {}, headers = {} } = {}) {
  const map = new Map(Object.entries(headers).map(([k, v]) => [k.toLowerCase(), String(v)]));
  return {
    status,
    headers: { get: (n) => (map.has(n.toLowerCase()) ? map.get(n.toLowerCase()) : null) },
    json: async () => body,
    text: async () => JSON.stringify(body),
  };
}

function clientWith(handler) {
  const calls = [];
  const fetchImpl = async (url, opts) => {
    calls.push(url);
    return handler(url, opts, calls.length);
  };
  const client = createGitHubClient({ fetchImpl, baseUrl: 'http://fake' });
  return { client, calls };
}

test('404 maps to a not_found error', async () => {
  const { client } = clientWith(() => responder(404, { body: { message: 'Not Found' } }));
  await assert.rejects(() => client.fetchRepo('a', 'b'), (e) => e.code === 'not_found' && e.status === 404);
});

test('403 with exhausted rate limit reports reset time and is retryable', async () => {
  const resetAt = Math.floor(Date.now() / 1000) + 1800;
  const { client } = clientWith(() => responder(403, {
    headers: { 'x-ratelimit-remaining': '0', 'x-ratelimit-reset': String(resetAt) },
  }));
  await assert.rejects(() => client.fetchRepo('a', 'b'), (e) => {
    assert.equal(e.code, 'rate_limited');
    assert.equal(e.retryable, true);
    assert.equal(e.rateLimitResetAt, resetAt * 1000);
    assert.ok(e.retryAfterMs > 0);
    return true;
  });
});

test('429 honors the Retry-After header', async () => {
  const { client } = clientWith(() => responder(429, { headers: { 'retry-after': '17' } }));
  await assert.rejects(() => client.fetchRepo('a', 'b'), (e) => {
    assert.equal(e.code, 'rate_limited');
    assert.equal(e.retryAfterMs, 17_000);
    return true;
  });
});

test('transient network errors are retried with bounded attempts', async () => {
  const { client, calls } = clientWith(() => { throw new TypeError('socket hangup'); });
  const started = Date.now();
  await assert.rejects(() => client.fetchRepo('a', 'b'), (e) => e.code === 'upstream_unavailable' && e.retryable);
  assert.equal(calls.length, 4); // 1 initial + 3 retries, then it gives up
  assert.ok(Date.now() - started < 15_000);
});

test('a transient failure followed by success resolves normally', async () => {
  const { client, calls } = clientWith((url, opts, attempt) => {
    if (attempt === 1) throw new TypeError('flaky');
    return responder(200, { body: { id: 1, owner: { login: 'a' }, name: 'b', full_name: 'a/b', html_url: 'u' } });
  });
  const meta = await client.fetchRepo('a', 'b');
  assert.equal(meta.githubId, 1);
  assert.equal(calls.length, 2);
});

test('401 reports a bad token distinctly', async () => {
  const { client } = clientWith(() => responder(401, { body: { message: 'Bad credentials' } }));
  await assert.rejects(() => client.fetchRepo('a', 'b'), (e) => e.code === 'unauthorized');
});

test('readme 404 returns null (repo without README)', async () => {
  const { client } = clientWith(() => responder(404));
  assert.equal(await client.fetchReadme('a', 'b'), null);
});

test('authorization header is attached when a token is configured', async () => {
  let seenAuth = null;
  const fetchImpl = async (url, opts) => {
    seenAuth = opts.headers.authorization || null;
    return responder(200, { body: { id: 1, owner: { login: 'a' }, name: 'b', full_name: 'a/b', html_url: 'u' } });
  };
  const client = createGitHubClient({ fetchImpl, baseUrl: 'http://fake', getToken: () => 'tok123' });
  await client.fetchRepo('a', 'b');
  assert.equal(seenAuth, 'Bearer tok123');
});
