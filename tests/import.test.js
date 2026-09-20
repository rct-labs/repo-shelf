import test from 'node:test';
import assert from 'node:assert/strict';

import { openDatabase } from '../server/db.js';
import { createGitHubClient } from '../server/github.js';
import { createJobManager } from '../server/jobs.js';
import { updateAnnotation, getRepo, saveRepo } from '../server/repos.js';
import { makeRepo, FakeGitHub, waitForJob } from './helpers.js';

function setup({ maxRateLimitWaitMs = 0 } = {}) {
  const db = openDatabase(':memory:');
  const fake = new FakeGitHub();
  const gh = createGitHubClient({ fetchImpl: fake.fetch, baseUrl: 'http://fake' });
  const jobs = createJobManager({ db, gh, maxRateLimitWaitMs });
  return { db, fake, gh, jobs };
}

function starredBatch(prefix, count, startId = 1) {
  return Array.from({ length: count }, (_, i) => makeRepo({
    id: startId + i,
    owner: 'community',
    name: `${prefix}-${startId + i}`,
    description: `Repository number ${startId + i}`,
  }));
}

test('imports multiple pages of stars with progress and no duplicates', async () => {
  const { db, fake, jobs } = setup();
  const list = starredBatch('proj', 205, 1000);
  for (const raw of list) fake.addRepo(raw, { readme: `# ${raw.name}` });
  fake.setStarred('someone', list);

  const job = jobs.startStarsImport({ username: 'someone' });
  const done = await waitForJob(jobs, job.id);
  assert.equal(done.status, 'done');
  assert.equal(done.processed, 205);
  assert.equal(done.added, 205);
  assert.equal(done.failed, 0);
  assert.equal(db.prepare('SELECT COUNT(*) c FROM repos').get().c, 205);
  assert.equal(db.prepare('SELECT COUNT(*) c FROM repos WHERE starred_upstream = 1').get().c, 205);
  // READMEs were fetched during import.
  assert.equal(db.prepare("SELECT COUNT(*) c FROM repos WHERE readme LIKE '# proj-%'").get().c, 205);

  // Repeating the import updates in place instead of duplicating.
  const again = jobs.startStarsImport({ username: 'someone' });
  const againDone = await waitForJob(jobs, again.id);
  assert.equal(againDone.status, 'done');
  assert.equal(againDone.added, 0);
  assert.equal(againDone.updated, 205);
  assert.equal(db.prepare('SELECT COUNT(*) c FROM repos').get().c, 205);
});

test('mid-import failure preserves progress and resume completes without duplicates', async () => {
  const { db, fake, jobs } = setup();
  const list = starredBatch('item', 150, 2000);
  for (const raw of list) fake.addRepo(raw);
  fake.setStarred('flaky', list);

  // Page 2 fails once with a transient server error storm (4 attempts -> fail).
  let page2Failures = 0;
  fake.failOnce((url) => url.includes('page=2'), () => { page2Failures += 1; throw new TypeError('network down'); });
  fake.failOnce((url) => url.includes('page=2'), () => { page2Failures += 1; throw new TypeError('network down'); });
  fake.failOnce((url) => url.includes('page=2'), () => { page2Failures += 1; throw new TypeError('network down'); });
  fake.failOnce((url) => url.includes('page=2'), () => { page2Failures += 1; throw new TypeError('network down'); });

  const job = jobs.startStarsImport({ username: 'flaky', includeReadme: false });
  const failed = await waitForJob(jobs, job.id);
  assert.equal(failed.status, 'failed');
  assert.ok(failed.error.includes('unreachable'));
  assert.equal(failed.nextPage, 2); // page 1 committed, page 2 pending
  assert.equal(failed.processed, 100);
  assert.equal(db.prepare('SELECT COUNT(*) c FROM repos').get().c, 100);

  const resumed = jobs.resumeJob(job.id);
  assert.equal(resumed.status, 'running');
  const done = await waitForJob(jobs, job.id);
  assert.equal(done.status, 'done');
  assert.equal(done.processed, 150);
  assert.equal(db.prepare('SELECT COUNT(*) c FROM repos').get().c, 150);
  assert.ok(page2Failures >= 1);
});

test('rate limit information aborts the job cleanly instead of hammering', async () => {
  const { fake, jobs } = setup({ maxRateLimitWaitMs: 0 });
  const list = starredBatch('rl', 101, 3000);
  for (const raw of list) fake.addRepo(raw);
  fake.setStarred('limited', list);
  const resetAt = Math.floor(Date.now() / 1000) + 3600;
  fake.failOnce((url) => url.includes('page=2'), () => ({
    status: 403,
    headers: { get: (n) => ({ 'x-ratelimit-remaining': '0', 'x-ratelimit-reset': String(resetAt) }[n] ?? null) },
    json: async () => ({ message: 'API rate limit exceeded' }),
    text: async () => 'rate limited',
  }));

  const job = jobs.startStarsImport({ username: 'limited', includeReadme: false });
  const done = await waitForJob(jobs, job.id);
  assert.equal(done.status, 'failed');
  assert.match(done.error, /rate limit/i);
  assert.equal(done.nextPage, 2);
});

test('re-import preserves annotations; upstream unstar keeps the local record', async () => {
  const { db, fake, jobs } = setup();
  const kept = makeRepo({ id: 5, owner: 'u', name: 'kept' });
  const unstarred = makeRepo({ id: 6, owner: 'u', name: 'unstarred' });
  fake.addRepo(kept);
  fake.addRepo(unstarred);
  fake.setStarred('fan', [kept, unstarred]);

  let job = jobs.startStarsImport({ username: 'fan', includeReadme: false });
  await waitForJob(jobs, job.id);
  const unstarredLocal = db.prepare('SELECT id FROM repos WHERE github_id = 6').get().id;
  updateAnnotation(db, unstarredLocal, { notes: '已取消收藏但笔记要留下', status: 'dismissed' });
  const keptLocal = db.prepare('SELECT id FROM repos WHERE github_id = 5').get().id;
  updateAnnotation(db, keptLocal, { reason: 'my reason', status: 'adopted' });

  // Upstream: unstar id 6 and change id 5's description.
  kept.description = 'changed upstream';
  fake.setStarred('fan', [kept]);

  job = jobs.startStarsImport({ username: 'fan', includeReadme: false });
  const done = await waitForJob(jobs, job.id);
  assert.equal(done.status, 'done');

  const after = getRepo(db, unstarredLocal);
  assert.ok(after, 'unstarred repo must not be deleted');
  assert.equal(after.starredUpstream, false);
  assert.equal(after.annotation.notes, '已取消收藏但笔记要留下');
  assert.equal(after.annotation.status, 'dismissed');

  const keptAfter = getRepo(db, keptLocal);
  assert.equal(keptAfter.description, 'changed upstream');
  assert.equal(keptAfter.annotation.reason, 'my reason');
  assert.equal(keptAfter.annotation.status, 'adopted');
  assert.equal(keptAfter.starredUpstream, true);
});

test('importing an already-saved repo updates instead of duplicating', async () => {
  const { db, fake, gh, jobs } = setup();
  const raw = makeRepo({ id: 42, owner: 'o', name: 'shared' });
  fake.addRepo(raw);
  const saved = await saveRepo(db, gh, { url: 'https://github.com/o/shared', reason: 'saved manually' });

  fake.setStarred('me', [raw]);
  const job = jobs.startStarsImport({ username: 'me', includeReadme: false });
  await waitForJob(jobs, job.id);
  assert.equal(db.prepare('SELECT COUNT(*) c FROM repos').get().c, 1);
  const repo = getRepo(db, saved.repo.id);
  assert.equal(repo.starredUpstream, true);
  assert.equal(repo.annotation.reason, 'saved manually');
});

test('unknown GitHub user fails the import cleanly', async () => {
  const { jobs } = setup();
  const job = jobs.startStarsImport({ username: 'ghost-user' });
  const done = await waitForJob(jobs, job.id);
  assert.equal(done.status, 'failed');
  assert.match(done.error, /not found/i);
});
