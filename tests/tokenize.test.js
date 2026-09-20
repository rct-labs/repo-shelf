import test from 'node:test';
import assert from 'node:assert/strict';

import { tokenize, tokenizeQuery, tokenizeForIndex } from '../server/tokenize.js';

test('English text becomes lowercase word tokens', () => {
  assert.deepEqual(tokenize('Hello, WORLD! foo_bar42'), ['hello', 'world', 'foo_bar42']);
});

test('Chinese text becomes bigrams', () => {
  assert.deepEqual(tokenize('状态管理'), ['状态', '态管', '管理']);
});

test('a single Chinese character stays a unigram', () => {
  assert.deepEqual(tokenize('云'), ['云']);
});

test('mixed Chinese/English splits at script boundaries', () => {
  assert.deepEqual(tokenize('使用React管理状态'), ['使用', 'react', '管理', '理状', '状态']);
});

test('numbers and separators', () => {
  assert.deepEqual(tokenize('vue3 组合式 api'), ['vue3', '组合', '合式', 'api']);
});

test('query tokens are deduplicated', () => {
  assert.deepEqual(tokenizeQuery('测试测试测试'), ['测试', '试测']);
});

test('index text is space-joined tokens', () => {
  assert.equal(tokenizeForIndex('笔记 tool'), '笔记 tool');
});

test('empty input tokenizes to nothing', () => {
  assert.deepEqual(tokenize(''), []);
  assert.deepEqual(tokenize(null), []);
  assert.deepEqual(tokenize('   --- !!! '), []);
});
