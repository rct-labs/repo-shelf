import test from 'node:test';
import assert from 'node:assert/strict';
import { openDatabase } from '../server/db.js';
import { createGitHubClient, GitHubError } from '../server/github.js';
import { saveRepo } from '../server/repos.js';
import { createAutoRefresh } from '../server/auto-refresh.js';
import { FakeGitHub, makeRepo } from './helpers.js';

test('automatic refresh skips fresh records and persists hourly cooldown', async () => {
  const db = openDatabase(':memory:');
  const fake = new FakeGitHub();
  fake.addRepo(makeRepo({ id: 1 }), { readme: 'hello' });
  const gh = createGitHubClient({ fetchImpl: fake.fetch, baseUrl: 'http://fake' });
  await saveRepo(db, gh, { url: 'https://github.com/octocat/repo', reason: 'keep' });
  const scheduler = createAutoRefresh({ db, gh });
  const now = Date.now();
  assert.equal(await scheduler.runOnce(now), 0);
  db.prepare("UPDATE repos SET fetched_at='2020-01-01T00:00:00Z'").run();
  assert.equal(await scheduler.runOnce(now + 3600000), 1);
  assert.equal(db.prepare('select reason from annotations').get().reason, 'keep');
  db.prepare("UPDATE repos SET fetched_at='2020-01-01T00:00:00Z'").run();
  const restarted = createAutoRefresh({ db, gh });
  assert.equal(await restarted.runOnce(now + 3601000), 0);
  await scheduler.stop();
  await restarted.stop();
  db.close();
});

test('automatic refresh pauses on rate limit without attempting remaining repositories', async () => {
  const db = openDatabase(':memory:');
  const fake = new FakeGitHub();
  fake.addRepo(makeRepo({ id: 1 }));
  fake.addRepo(makeRepo({ id: 2, name: 'second' }));
  const gh = createGitHubClient({ fetchImpl: fake.fetch, baseUrl: 'http://fake' });
  await saveRepo(db, gh, { url: 'https://github.com/octocat/repo' });
  await saveRepo(db, gh, { url: 'https://github.com/octocat/second' });
  db.prepare("UPDATE repos SET fetched_at='2020-01-01T00:00:00Z'").run();
  let calls = 0;
  const limited = { fetchRepo: async () => { calls++; throw new GitHubError('rate_limited', 'limited', { retryAfterMs: 7200000 }); } };
  const scheduler = createAutoRefresh({ db, gh: limited });
  const now = Date.now();
  assert.equal(await scheduler.runOnce(now), 0);
  assert.equal(calls, 1);
  assert.equal(await scheduler.runOnce(now + 3600000), 0);
  assert.equal(calls, 1);
  await scheduler.stop();
  db.close();
});
