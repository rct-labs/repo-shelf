import test from 'node:test';
import assert from 'node:assert/strict';
import { readmeUrl } from '../public/readme-urls.js';
const repo = { owner: 'cathrynlavery', name: 'diagram-design', defaultBranch: 'main' };
test('README images resolve to raw resources for relative paths and blob URLs', () => {
  for (const path of ['docs/hero/hero-light.webp', './docs/hero/hero-light.webp', '/docs/hero/hero-light.webp',
    'https://github.com/cathrynlavery/diagram-design/blob/main/docs/hero/hero-light.webp']) {
    assert.equal(readmeUrl(path, repo, true), 'https://raw.githubusercontent.com/cathrynlavery/diagram-design/main/docs/hero/hero-light.webp');
  }
  assert.equal(readmeUrl('https://example.com/a.png?q=1', repo, true), 'https://example.com/a.png?q=1');
  assert.equal(readmeUrl('//example.com/a.png', repo, true), 'https://example.com/a.png');
});
test('README links retain anchors and target repository files; unsafe protocols are rejected', () => {
  assert.equal(readmeUrl('#examples', repo), '#examples');
  assert.equal(readmeUrl('docs/a b.md', repo), 'https://github.com/cathrynlavery/diagram-design/blob/main/docs/a%20b.md');
  assert.equal(readmeUrl('javascript:alert(1)', repo, true), null);
  assert.equal(readmeUrl('file:///secret', repo, true), null);
  assert.ok(readmeUrl('a.png', { ...repo, defaultBranch: 'release/v2' }, true).includes('release%2Fv2/a.png'));
});
