import test from 'node:test';
import assert from 'node:assert/strict';

import { openDatabase, getSetting, setSetting } from '../server/db.js';
import { upsertSource, updateAnnotation, getRepo } from '../server/repos.js';
import { exportData, importData, BackupError } from '../server/backup.js';
import { searchRepos } from '../server/search.js';

function meta(id, name) {
  return {
    githubId: id, owner: 'o', name, fullName: `o/${name}`, htmlUrl: `https://github.com/o/${name}`,
    description: `desc ${name}`, topics: ['t1', 't2'], language: 'Go', licenseId: 'MIT',
    archived: false, pushedAt: '2026-09-01T00:00:00Z', stars: 5, defaultBranch: 'main',
  };
}

test('export -> wipe -> restore round-trips all durable fields without credentials', () => {
  const db = openDatabase(':memory:');
  setSetting(db, 'github_token', 'ghp_secret_token_value');
  setSetting(db, 'pairing_token', 'pairing_secret_value');

  const a = upsertSource(db, meta(1, 'alpha'), { readme: '# alpha readme 中文内容', readmeTruncated: 0, starred: 1 });
  updateAnnotation(db, a.repoId, { reason: 'why alpha', notes: '中文笔记 <b>html</b>', tags: ['x', 'y'], projects: ['proj'], status: 'adopted' });
  const b = upsertSource(db, meta(2, 'beta'), { readme: '# beta', readmeTruncated: 0 });
  updateAnnotation(db, b.repoId, { notes: 'beta notes', status: 'to_investigate' });
  upsertSource(db, meta(3, 'gamma'), { readme: null });

  const exported = exportData(db);
  const serialized = JSON.stringify(exported);
  assert.ok(!serialized.includes('ghp_secret_token_value'), 'GitHub token must not appear in export');
  assert.ok(!serialized.includes('pairing_secret_value'), 'pairing token must not appear in export');
  assert.equal(exported.version, 1);
  assert.equal(exported.repos.length, 3);

  const fresh = openDatabase(':memory:');
  const counts = importData(fresh, JSON.parse(serialized));
  assert.deepEqual(counts, { added: 3, skipped: 0, overwritten: 0 });

  const sourceRows = db.prepare('SELECT * FROM repos ORDER BY github_id').all();
  for (const src of sourceRows) {
    const restored = getRepo(fresh, db.prepare('SELECT id FROM repos WHERE github_id = ?').get(src.github_id).id);
    for (const [key, value] of Object.entries({
      githubId: src.github_id, owner: src.owner, name: src.name, fullName: src.full_name,
      htmlUrl: src.html_url, description: src.description, language: src.language,
      licenseId: src.license_id, pushedAt: src.pushed_at, stars: src.stars,
      defaultBranch: src.default_branch, readme: src.readme,
      archived: Boolean(src.archived), readmeTruncated: Boolean(src.readme_truncated),
      starredUpstream: Boolean(src.starred_upstream), createdAt: src.created_at,
    })) {
      assert.deepEqual(restored[key], value, `${src.name}.${key}`);
    }
    assert.deepEqual(restored.topics, JSON.parse(src.topics));
  }
  const restoredA = getRepo(fresh, db.prepare('SELECT id FROM repos WHERE github_id = 1').get().id);
  assert.equal(restoredA.annotation.reason, 'why alpha');
  assert.equal(restoredA.annotation.notes, '中文笔记 <b>html</b>');
  assert.deepEqual(restoredA.annotation.tags, ['x', 'y']);
  assert.deepEqual(restoredA.annotation.projects, ['proj']);
  assert.equal(restoredA.annotation.status, 'adopted');

  // Full-text search works on restored data.
  assert.equal(searchRepos(fresh, { q: '中文笔记' }).total, 1);
  assert.equal(searchRepos(fresh, { q: 'alpha' }).total, 1);

  // Fresh database has no credentials either.
  assert.equal(getSetting(fresh, 'github_token'), null);
});

test('merge mode keeps existing records; overwrite replaces them', () => {
  const db = openDatabase(':memory:');
  const { repoId } = upsertSource(db, meta(1, 'alpha'), { readme: '# new' });
  updateAnnotation(db, repoId, { notes: 'current notes' });

  const backup = {
    app: 'repo-shelf', version: 1, repos: [{
      ...meta(1, 'alpha'), description: 'old desc', readme: '# old', readmeTruncated: false,
      starredUpstream: true, createdAt: '2020-01-01T00:00:00Z',
      annotation: { reason: 'old reason', notes: 'old notes', tags: ['old'], projects: [], status: 'tried' },
    }],
  };

  const merged = importData(db, backup, { mode: 'merge' });
  assert.deepEqual(merged, { added: 0, skipped: 1, overwritten: 0 });
  assert.equal(getRepo(db, repoId).annotation.notes, 'current notes');

  const overwritten = importData(db, backup, { mode: 'overwrite' });
  assert.deepEqual(overwritten, { added: 0, skipped: 0, overwritten: 1 });
  const after = getRepo(db, repoId);
  assert.equal(after.annotation.notes, 'old notes');
  assert.equal(after.description, 'old desc');
  assert.equal(after.readme, '# old');
});

test('invalid backups are rejected', () => {
  const db = openDatabase(':memory:');
  assert.throws(() => importData(db, null), BackupError);
  assert.throws(() => importData(db, { app: 'other', version: 1, repos: [] }), BackupError);
  assert.throws(() => importData(db, { app: 'repo-shelf', version: 99, repos: [] }), BackupError);
  assert.throws(() => importData(db, { app: 'repo-shelf', version: 1, repos: [{ fullName: 'x/y' }] }), BackupError);
});
