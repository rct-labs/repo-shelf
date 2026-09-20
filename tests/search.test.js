import test from 'node:test';
import assert from 'node:assert/strict';

import { openDatabase } from '../server/db.js';
import { createGitHubClient } from '../server/github.js';
import { upsertSource, updateAnnotation } from '../server/repos.js';
import { searchRepos } from '../server/search.js';
import { makeRepo, FakeGitHub } from './helpers.js';

function seed() {
  const db = openDatabase(':memory:');
  const fake = new FakeGitHub();
  const gh = createGitHubClient({ fetchImpl: fake.fetch, baseUrl: 'http://fake' });

  const dotfiles = makeRepo({ id: 1, owner: 'alice', name: 'dotforge', description: 'Manage your dotfiles with ease', topics: ['dotfiles', 'cli'] });
  upsertSource(db, dotfilesApiToMeta(dotfiles), { readme: '# dotforge\n\nA CLI tool for managing dotfiles across machines. Supports symlinks and templates.' });

  const stateLib = makeRepo({ id: 2, owner: 'bob', name: 'zustand-cn', description: 'State management for React', topics: ['react', 'state'] });
  upsertSource(db, dotfilesApiToMeta(stateLib), { readme: '# zustand-cn\n\nA small state management library for React applications.' });
  updateAnnotation(db, 2, { notes: '轻量的 React 状态管理库，适合中小型项目', tags: ['前端', 'react'], status: 'tried', projects: ['side-app'] });

  const toolCn = makeRepo({ id: 3, owner: 'carol', name: 'note-sync', description: 'Sync notes', language: 'Go', archived: true });
  upsertSource(db, dotfilesApiToMeta(toolCn), { readme: '# note-sync\n\nSync markdown notes between devices.' });
  updateAnnotation(db, 3, { reason: '想研究它的增量同步算法', notes: '配置文件用 YAML，支持增量同步', tags: ['笔记'] });

  return { db, gh };
}

// mapRepo-equivalent for direct seeding (mirrors server/github.js mapping)
function dotfilesApiToMeta(raw) {
  return {
    githubId: raw.id,
    owner: raw.owner.login,
    name: raw.name,
    fullName: raw.full_name,
    htmlUrl: raw.html_url,
    description: raw.description,
    topics: raw.topics,
    language: raw.language,
    licenseId: raw.license?.spdx_id ?? null,
    archived: raw.archived,
    pushedAt: raw.pushed_at,
    stars: raw.stargazers_count,
    defaultBranch: raw.default_branch,
  };
}

test('Chinese query matches Chinese notes with a highlighted snippet', () => {
  const { db } = seed();
  const { total, items } = searchRepos(db, { q: '状态管理' });
  assert.equal(total, 1);
  assert.equal(items[0].row.name, 'zustand-cn');
  assert.ok(items[0].matchedFields.includes('notes'));
  assert.ok(items[0].snippet.includes('<mark>'), items[0].snippet);
});

test('Chinese query crosses into Chinese-only notes (增量同步)', () => {
  const { db } = seed();
  const { items } = searchRepos(db, { q: '增量同步' });
  assert.equal(items.length, 1);
  assert.equal(items[0].row.name, 'note-sync');
  assert.ok(items[0].matchedFields.includes('notes'));
});

test('Chinese reason field is searchable', () => {
  const { db } = seed();
  const { items } = searchRepos(db, { q: '研究' });
  assert.equal(items.length, 1);
  assert.ok(items[0].matchedFields.includes('reason'));
});

test('English query matches README content', () => {
  const { db } = seed();
  const { items } = searchRepos(db, { q: 'symlinks' });
  assert.equal(items.length, 1);
  assert.equal(items[0].row.name, 'dotforge');
  assert.ok(items[0].matchedFields.includes('readme'));
});

test('exact repository name ranks first', () => {
  const db = openDatabase(':memory:');
  upsertSource(db, { githubId: 10, owner: 'a', name: 'react', fullName: 'a/react', htmlUrl: '', description: '', topics: [], language: null, licenseId: null, archived: false, pushedAt: null, stars: 0, defaultBranch: null }, {});
  upsertSource(db, { githubId: 11, owner: 'b', name: 'react-awesome', fullName: 'b/react-awesome', htmlUrl: '', description: 'react things', topics: [], language: null, licenseId: null, archived: false, pushedAt: null, stars: 0, defaultBranch: null }, {});
  const { items } = searchRepos(db, { q: 'react' });
  assert.equal(items[0].row.name, 'react');
});

test('owner and tag search work', () => {
  const { db } = seed();
  assert.equal(searchRepos(db, { q: 'carol' }).total, 1);
  assert.equal(searchRepos(db, { q: 'dotfiles' }).total, 1); // via topics
});

test('AND semantics: all query tokens must match', () => {
  const { db } = seed();
  assert.equal(searchRepos(db, { q: 'react 状态管理' }).total, 1);
  assert.equal(searchRepos(db, { q: 'react 不存在的词' }).total, 0);
});

test('filters combine with keyword search', () => {
  const { db } = seed();
  assert.equal(searchRepos(db, { q: 'react', status: 'tried' }).total, 1);
  assert.equal(searchRepos(db, { q: 'react', status: 'inbox' }).total, 0);
  assert.equal(searchRepos(db, { q: 'react', tag: '前端' }).total, 1);
  assert.equal(searchRepos(db, { q: 'sync', language: 'go' }).total, 1); // language filter is case-insensitive
  assert.equal(searchRepos(db, { q: 'sync', archived: false }).total, 0);
  assert.equal(searchRepos(db, { q: 'sync', archived: true }).total, 1);
  assert.equal(searchRepos(db, { q: 'react', project: 'side-app' }).total, 1);
});

test('empty query lists everything newest first', () => {
  const { db } = seed();
  const { total, items } = searchRepos(db, {});
  assert.equal(total, 3);
  assert.equal(items[0].row.name, 'note-sync');
});

test('snippets escape HTML from stored text', () => {
  const db = openDatabase(':memory:');
  const { repoId } = upsertSource(db, { githubId: 20, owner: 'x', name: 'evil', fullName: 'x/evil', htmlUrl: '', description: '', topics: [], language: null, licenseId: null, archived: false, pushedAt: null, stars: 0, defaultBranch: null }, {});
  updateAnnotation(db, repoId, { notes: '记住 <script>alert(1)</script> 这个 payload' });
  const { items } = searchRepos(db, { q: 'payload' });
  assert.equal(items.length, 1);
  assert.ok(!items[0].snippet.includes('<script>'), items[0].snippet);
  assert.ok(items[0].snippet.includes('&lt;script&gt;'), items[0].snippet);
});
