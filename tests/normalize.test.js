import test from 'node:test';
import assert from 'node:assert/strict';

import { parseGitHubRepoUrl, NormalizeError } from '../server/normalize.js';

test('accepts plain repository URLs', () => {
  const r = parseGitHubRepoUrl('https://github.com/octocat/Hello-World');
  assert.equal(r.owner, 'octocat');
  assert.equal(r.repo, 'Hello-World');
  assert.equal(r.url, 'https://github.com/octocat/Hello-World');
});

test('normalizes subpages, fragments, query strings and trailing slashes', () => {
  const variants = [
    'https://github.com/octocat/Hello-World/',
    'https://github.com/octocat/Hello-World/tree/main/docs',
    'https://github.com/octocat/Hello-World/issues/3#comment',
    'https://github.com/octocat/Hello-World?tab=readme',
    'https://github.com/octocat/Hello-World.git',
    'https://www.github.com/octocat/Hello-World',
    'http://github.com/octocat/Hello-World',
    'github.com/octocat/Hello-World',
    '  https://github.com/octocat/Hello-World  ',
  ];
  for (const v of variants) {
    const r = parseGitHubRepoUrl(v);
    assert.equal(r.fullName, 'octocat/Hello-World', v);
  }
});

test('rejects non-GitHub hosts', () => {
  for (const bad of [
    'https://gitlab.com/octocat/Hello-World',
    'https://gist.github.com/octocat/12345',
    'https://raw.githubusercontent.com/octocat/Hello-World/main/README.md',
    'https://api.github.com/repos/octocat/Hello-World',
    'https://github.com.evil.example/octocat/Hello-World',
  ]) {
    assert.throws(() => parseGitHubRepoUrl(bad), NormalizeError, bad);
  }
});

test('rejects non-repository GitHub paths', () => {
  for (const bad of [
    'https://github.com',
    'https://github.com/octocat',
    'https://github.com/settings/profile',
    'https://github.com/topics/javascript',
    'https://github.com/trending/js',
    'https://github.com/marketplace/actions',
  ]) {
    assert.throws(() => parseGitHubRepoUrl(bad), NormalizeError, bad);
  }
});

test('rejects malformed input and dangerous schemes', () => {
  for (const bad of [
    '',
    '   ',
    'not a url',
    'javascript:alert(1)',
    'ftp://github.com/octocat/Hello-World',
    'file:///etc/passwd',
    'https://github.com/octo cat/repo',
    'https://github.com/octocat/..',
  ]) {
    assert.throws(() => parseGitHubRepoUrl(bad), NormalizeError, JSON.stringify(bad));
  }
});
