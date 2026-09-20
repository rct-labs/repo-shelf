// Performance check: seed 5,000 representative repositories, then measure
// warm local search latency. Reports measured p95/mean per query and overall.
// Usage: pnpm perf

import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { performance } from 'node:perf_hooks';

import { openDatabase } from '../../server/db.js';
import { upsertSource, updateAnnotation } from '../../server/repos.js';
import { searchRepos } from '../../server/search.js';

const DATASET_SIZE = 5000;
const ITERATIONS = 30;
const P95_TARGET_MS = 500;

// Deterministic PRNG so the dataset is reproducible.
function mulberry32(seed) {
  return () => {
    seed |= 0;
    seed = (seed + 0x6D2B79F5) | 0;
    let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}
const rand = mulberry32(20260920);
const pick = (arr) => arr[Math.floor(rand() * arr.length)];

const EN_WORDS = ['fast', 'modular', 'async', 'reactive', 'typed', 'minimal', 'distributed', 'embedded', 'secure', 'portable', 'declarative', 'streaming', 'compiler', 'runtime', 'schema', 'router', 'cache', 'queue', 'worker', 'parser'];
const CN_PHRASES = ['状态管理', '增量同步', '本地优先', '全文检索', '配置生成', '部署脚本', '数据可视化', '权限控制', '离线缓存', '命令行工具', '静态分析', '任务调度', ' Markdown 渲染', '端到端加密', '双语界面'];
const LANGUAGES = ['TypeScript', 'JavaScript', 'Python', 'Go', 'Rust', null];
const TOPICS = ['cli', 'web', 'database', 'devops', 'testing', 'ui', 'api', 'search', 'sync', 'security'];
const STATUSES = ['inbox', 'inbox', 'inbox', 'to_investigate', 'tried', 'adopted', 'dismissed'];
const PROJECTS = ['repo-shelf', 'home-lab', 'work-infra', 'side-app'];

function sentence(words, n) {
  return Array.from({ length: n }, () => pick(words)).join(' ');
}

function buildDataset(n) {
  const items = [];
  for (let i = 1; i <= n; i += 1) {
    const name = `${pick(EN_WORDS)}-${pick(EN_WORDS)}-${i}`;
    const cn = rand() < 0.45;
    items.push({
      meta: {
        githubId: i,
        owner: `user${i % 137}`,
        name,
        fullName: `user${i % 137}/${name}`,
        htmlUrl: `https://github.com/user${i % 137}/${name}`,
        description: `${sentence(EN_WORDS, 6)}${cn ? `，${pick(CN_PHRASES)}的工具` : ''}`,
        topics: [pick(TOPICS), pick(TOPICS)],
        language: pick(LANGUAGES),
        licenseId: pick(['MIT', 'Apache-2.0', 'GPL-3.0', null]),
        archived: rand() < 0.08,
        pushedAt: '2026-09-01T00:00:00Z',
        stars: Math.floor(rand() * 20000),
        defaultBranch: 'main',
      },
      readme: `# ${name}\n\n${sentence(EN_WORDS, 400)}.\n\n## Usage\n\n${sentence(EN_WORDS, 300)}.\n`,
      annotation: {
        reason: cn ? `因为${pick(CN_PHRASES)}很方便` : `useful for ${sentence(EN_WORDS, 4)}`,
        notes: cn ? `这个库关于${pick(CN_PHRASES)}，${pick(CN_PHRASES)}做得不错，star 数还行。` : `tried it for ${sentence(EN_WORDS, 8)}`,
        tags: [pick(TOPICS), cn ? '中文笔记' : 'english-note'],
        projects: rand() < 0.4 ? [pick(PROJECTS)] : [],
        status: pick(STATUSES),
      },
    });
  }
  return items;
}

const QUERIES = [
  ['exact-name', (ds) => ds[1234].meta.name],
  ['english-readme-term', () => 'compiler'],
  ['chinese-notes-term', () => '状态管理'],
  ['chinese-phrase', () => '增量同步'],
  ['mixed-query', () => 'cache 离线缓存'],
  ['owner', () => 'user42'],
  ['rare-term', () => 'declarative streaming'],
  ['no-results', () => 'definitely-not-present-xyz'],
  ['filter-status', () => 'parser', { status: 'adopted' }],
  ['filter-language', () => 'queue', { language: 'Rust' }],
  ['filter-tag-archived', () => 'web', { tag: 'sync', archived: false }],
  ['empty-query-listing', () => ''],
];

function percentile(sorted, p) {
  const idx = Math.min(sorted.length - 1, Math.ceil((p / 100) * sorted.length) - 1);
  return sorted[idx];
}

async function main() {
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'repo-shelf-perf-'));
  const db = openDatabase(path.join(tmp, 'perf.db'));

  console.log(`Seeding ${DATASET_SIZE} repositories…`);
  const dataset = buildDataset(DATASET_SIZE);
  const seedStart = performance.now();
  db.transaction(() => {
    for (const { meta, readme, annotation } of dataset) {
      const { repoId } = upsertSource(db, meta, { readme, readmeTruncated: 0 });
      updateAnnotation(db, repoId, annotation);
    }
  })();
  console.log(`Seeded in ${((performance.now() - seedStart) / 1000).toFixed(1)}s`);

  // Warm-up.
  for (const [, gen, filters] of QUERIES) {
    for (let i = 0; i < 3; i += 1) searchRepos(db, { q: gen(dataset), ...(filters || {}) });
  }

  console.log('\nMeasuring (each query x30, warm)…');
  const allSamples = [];
  const rows = [];
  for (const [label, gen, filters] of QUERIES) {
    const samples = [];
    for (let i = 0; i < ITERATIONS; i += 1) {
      const q = gen(dataset);
      const start = performance.now();
      searchRepos(db, { q, ...(filters || {}) });
      samples.push(performance.now() - start);
    }
    samples.sort((a, b) => a - b);
    const mean = samples.reduce((a, b) => a + b, 0) / samples.length;
    const p95 = percentile(samples, 95);
    rows.push({ label, mean, p95, max: samples[samples.length - 1] });
    allSamples.push(...samples);
  }
  allSamples.sort((a, b) => a - b);
  const overallP95 = percentile(allSamples, 95);

  console.log('\nquery                    mean(ms)   p95(ms)   max(ms)');
  for (const r of rows) {
    console.log(`${r.label.padEnd(22)} ${r.mean.toFixed(1).padStart(8)} ${r.p95.toFixed(1).padStart(9)} ${r.max.toFixed(1).padStart(9)}`);
  }

  const cpu = os.cpus()[0]?.model || 'unknown';
  console.log('\n--- measurement context ---');
  console.log(`dataset: ${DATASET_SIZE} repos, synthetic mixed zh/en text, README ~8KB each`);
  console.log(`machine: ${cpu}; cores=${os.cpus().length}; ram=${Math.round(os.totalmem() / 2 ** 30)}GB; platform=${os.platform()} ${os.release()}`);
  console.log(`runtime: node ${process.version}; better-sqlite3 (sqlite ${db.prepare('select sqlite_version() v').get().v})`);
  console.log(`method: perf_hooks.performance.now() around searchRepos(), warm index, ${ITERATIONS} iterations per query`);
  console.log(`\noverall p95: ${overallP95.toFixed(1)} ms (target < ${P95_TARGET_MS} ms)`);

  db.close();
  fs.rmSync(tmp, { recursive: true, force: true });

  if (overallP95 >= P95_TARGET_MS) {
    console.error('PERF TARGET MISSED');
    process.exit(1);
  }
  console.log('PERF TARGET MET');
}

main();
