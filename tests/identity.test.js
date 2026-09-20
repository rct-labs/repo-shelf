import test from 'node:test';
import assert from 'node:assert/strict';

import { openDatabase } from '../server/db.js';
import { createGitHubClient } from '../server/github.js';
import { saveRepo, refreshRepo, getRepo, updateAnnotation, deleteRepo } from '../server/repos.js';
import { searchRepos } from '../server/search.js';
import { makeRepo, FakeGitHub } from './helpers.js';

function setup() {
  const db = openDatabase(':memory:');
  const fake = new FakeGitHub();
  const gh = createGitHubClient({ fetchImpl: fake.fetch, baseUrl: 'http://fake' });
  return { db, fake, gh };
}

test('save creates one record with metadata, README snapshot and inbox status', async () => {
  const { db, fake, gh } = setup();
  fake.addRepo(makeRepo({ id: 1, owner: 'octocat', name: 'Hello-World' }), { readme: '# Hello' });
  const result = await saveRepo(db, gh, { url: 'https://github.com/octocat/Hello-World', reason: 'looks useful', tags: ['demo'] });
  assert.equal(result.outcome, 'created');
  const repo = result.repo;
  assert.equal(repo.githubId, 1);
  assert.equal(repo.readme, '# Hello');
  assert.equal(repo.annotation.status, 'inbox');
  assert.equal(repo.annotation.reason, 'looks useful');
  assert.deepEqual(repo.annotation.tags, ['demo']);
  assert.equal(repo.refreshStatus, 'ok');
  assert.ok(repo.fetchedAt);
});

test('saving the same URL twice keeps a single record and personal fields', async () => {
  const { db, fake, gh } = setup();
  fake.addRepo(makeRepo({ id: 1, owner: 'octocat', name: 'Hello-World' }));
  const first = await saveRepo(db, gh, { url: 'https://github.com/octocat/Hello-World', reason: 'first reason' });
  const second = await saveRepo(db, gh, { url: 'https://github.com/octocat/Hello-World/tree/main', reason: 'ignored reason', tags: ['extra'] });
  assert.equal(second.outcome, 'already_exists');
  assert.equal(second.repo.id, first.repo.id);
  assert.equal(db.prepare('SELECT COUNT(*) c FROM repos').get().c, 1);
  assert.equal(second.repo.annotation.reason, 'first reason'); // existing reason is never overwritten
  assert.deepEqual(second.repo.annotation.tags, ['extra']); // tags merge as a union
});

test('rename keeps identity and does not duplicate', async () => {
  const { db, fake, gh } = setup();
  const raw = fake.addRepo(makeRepo({ id: 7, owner: 'oldowner', name: 'oldname' }));
  const saved = await saveRepo(db, gh, { url: 'https://github.com/oldowner/oldname' });
  updateAnnotation(db, saved.repo.id, { notes: '改名后笔记必须还在', status: 'adopted' });

  fake.moveRepo(raw, 'newowner', 'newname'); // GitHub answers redirects; the API id stays 7
  // Saving the new URL resolves to the same record via the durable id.
  const again = await saveRepo(db, gh, { url: 'https://github.com/newowner/newname' });
  assert.equal(again.repo.id, saved.repo.id);
  assert.equal(db.prepare('SELECT COUNT(*) c FROM repos').get().c, 1);

  const refreshed = await refreshRepo(db, gh, saved.repo.id);
  assert.equal(refreshed.repo.fullName, 'newowner/newname');
  assert.equal(refreshed.repo.annotation.notes, '改名后笔记必须还在');
  assert.equal(refreshed.repo.annotation.status, 'adopted');
});

test('refresh follows the durable id when the old name 404s (transfer)', async () => {
  const { db, fake, gh } = setup();
  const raw = fake.addRepo(makeRepo({ id: 9, owner: 'a', name: 'proj' }));
  const saved = await saveRepo(db, gh, { url: 'https://github.com/a/proj' });
  fake.moveRepo(raw, 'b', 'proj');
  fake.failOnce((url) => url.includes('/repos/a/proj'), () => ({ status: 404, headers: { get: () => null }, json: async () => ({}), text: async () => '{}' }));
  const refreshed = await refreshRepo(db, gh, saved.repo.id);
  assert.equal(refreshed.repo.fullName, 'b/proj');
  assert.equal(refreshed.repo.refreshStatus, 'ok');
});

test('deleted upstream repository is kept and marked not_found', async () => {
  const { db, fake, gh } = setup();
  const raw = fake.addRepo(makeRepo({ id: 11, owner: 'gone', name: 'proj' }));
  const saved = await saveRepo(db, gh, { url: 'https://github.com/gone/proj', reason: 'keep me' });
  fake.deleteRepo(raw);
  await assert.rejects(() => refreshRepo(db, gh, saved.repo.id), /no longer exists/);
  const repo = getRepo(db, saved.repo.id);
  assert.equal(repo.refreshStatus, 'not_found');
  assert.equal(repo.annotation.reason, 'keep me');
});

test('refresh updates source fields but never personal fields', async () => {
  const { db, fake, gh } = setup();
  const raw = fake.addRepo(makeRepo({ id: 12, owner: 'o', name: 'r', description: 'old' }));
  const saved = await saveRepo(db, gh, { url: 'https://github.com/o/r' });
  updateAnnotation(db, saved.repo.id, { notes: '私人笔记', tags: ['t'], status: 'tried' });
  raw.description = 'new description';
  raw.archived = true;
  await refreshRepo(db, gh, saved.repo.id);
  const repo = getRepo(db, saved.repo.id);
  assert.equal(repo.description, 'new description');
  assert.equal(repo.archived, true);
  assert.equal(repo.annotation.notes, '私人笔记');
  assert.deepEqual(repo.annotation.tags, ['t']);
  assert.equal(repo.annotation.status, 'tried');
});

test('saving with GitHub unreachable fails retryably and stores nothing', async () => {
  const { db, fake, gh } = setup();
  fake.failOnce(() => true, () => { throw new TypeError('fetch failed'); });
  fake.failOnce(() => true, () => { throw new TypeError('fetch failed'); });
  fake.failOnce(() => true, () => { throw new TypeError('fetch failed'); });
  fake.failOnce(() => true, () => { throw new TypeError('fetch failed'); });
  await assert.rejects(
    () => saveRepo(db, gh, { url: 'https://github.com/octocat/Hello-World' }),
    (err) => {
      assert.equal(err.code, 'upstream_unavailable');
      assert.equal(err.retryable, true);
      return true;
    },
  );
  assert.equal(db.prepare('SELECT COUNT(*) c FROM repos').get().c, 0);
});

test('invalid and unknown repositories are rejected clearly', async () => {
  const { db, gh } = setup();
  await assert.rejects(() => saveRepo(db, gh, { url: 'https://example.com/x/y' }), (e) => e.code === 'invalid_url');
  await assert.rejects(() => saveRepo(db, gh, { url: 'https://github.com/no/such' }), (e) => e.code === 'repository_not_found');
});

test('annotation validation and delete', async () => {
  const { db, fake, gh } = setup();
  fake.addRepo(makeRepo({ id: 21, owner: 'o', name: 'r' }));
  const saved = await saveRepo(db, gh, { url: 'https://github.com/o/r' });
  await assert.rejects(
    async () => updateAnnotation(db, saved.repo.id, { status: 'nope' }),
    (e) => e.code === 'invalid_field',
  );
  const annotation = updateAnnotation(db, saved.repo.id, { status: 'dismissed', projects: ['工作'] });
  assert.equal(annotation.status, 'dismissed');
  deleteRepo(db, saved.repo.id);
  assert.equal(getRepo(db, saved.repo.id), null);
  assert.equal(searchRepos(db, {}).total, 0);
});
