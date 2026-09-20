// Background jobs: GitHub Stars import and refresh-all. Jobs run in-process
// (single-user local app), persist progress in `import_jobs` for resume, and
// support cancellation between units of work.

import crypto from 'node:crypto';
import { GitHubError } from './github.js';
import { upsertSource, refreshRepo } from './repos.js';

const now = () => new Date().toISOString();
const FAILURE_LIST_CAP = 100;
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

export function createJobManager({ db, gh, maxRateLimitWaitMs = 90_000, sleepImpl = sleep } = {}) {
  const running = new Map(); // job id -> { cancel: boolean }

  const getRow = (id) => db.prepare('SELECT * FROM import_jobs WHERE id = ?').get(id);

  function toPublic(row) {
    if (!row) return null;
    return {
      id: row.id,
      type: row.type,
      username: row.username,
      status: row.status,
      options: JSON.parse(row.options || '{}'),
      nextPage: row.next_page,
      processed: row.processed,
      added: row.added,
      updated: row.updated,
      failed: row.failed,
      failures: JSON.parse(row.failures || '[]'),
      error: row.error,
      createdAt: row.created_at,
      finishedAt: row.finished_at,
    };
  }

  function update(id, fields) {
    const keys = Object.keys(fields);
    const sql = `UPDATE import_jobs SET ${keys.map((k) => `${k} = ?`).join(', ')} WHERE id = ?`;
    db.prepare(sql).run(...keys.map((k) => (typeof fields[k] === 'object' ? JSON.stringify(fields[k]) : fields[k])), id);
  }

  function pushFailure(failures, subject, code) {
    if (failures.length < FAILURE_LIST_CAP) failures.push({ subject, code });
  }

  // Honor server rate-limit information: wait only when the reported wait is
  // bounded and reasonable; otherwise stop the job with a clear error instead
  // of hammering the API.
  async function withRateLimitGate(err) {
    if (!(err instanceof GitHubError) || err.code !== 'rate_limited') return false;
    const waitMs = err.retryAfterMs;
    if (waitMs !== null && waitMs <= maxRateLimitWaitMs) {
      await sleepImpl(waitMs + 250);
      return true;
    }
    return false;
  }

  async function runStarsImport(id) {
    const state = running.get(id);
    const job = getRow(id);
    const { includeReadme = true } = JSON.parse(job.options || '{}');
    let { username, next_page: page, processed, added, updated, failed } = job;
    let failures = JSON.parse(job.failures || '[]');
    let readmeRateLimited = false;

    const flush = (extra = {}) => update(id, {
      next_page: page, processed, added, updated, failed, failures, ...extra,
    });

    try {
      while (true) {
        if (state.cancel) {
          flush({ status: 'cancelled', finished_at: now() });
          return;
        }
        let pageData;
        try {
          pageData = await gh.fetchStarredPage(username, page);
        } catch (err) {
          if (await withRateLimitGate(err)) {
            pageData = await gh.fetchStarredPage(username, page);
          } else {
            throw err;
          }
        }
        if (pageData.items.length === 0) break;

        for (const meta of pageData.items) {
          if (state.cancel) {
            flush({ status: 'cancelled', finished_at: now() });
            return;
          }
          try {
            let readme;
            let readmeTruncated = 0;
            if (includeReadme && !readmeRateLimited) {
              try {
                const result = await gh.fetchReadme(meta.owner, meta.name);
                if (result) {
                  readme = result.text;
                  readmeTruncated = result.truncated ? 1 : 0;
                }
              } catch (err) {
                failed += 1;
                pushFailure(failures, meta.fullName, `readme: ${err.code || 'error'}`);
                if (err instanceof GitHubError && err.code === 'rate_limited') {
                  readmeRateLimited = true;
                  pushFailure(failures, meta.fullName, 'readme: remaining README fetches skipped (rate limit)');
                }
              }
            }
            const result = upsertSource(db, meta, { readme, readmeTruncated, starred: 1 });
            db.prepare('INSERT OR IGNORE INTO job_seen (job_id, github_id) VALUES (?, ?)').run(id, meta.githubId);
            if (result.created) added += 1; else updated += 1;
          } catch (err) {
            failed += 1;
            pushFailure(failures, meta.fullName, err.code || 'error');
          }
          processed += 1;
        }
        flush();
        if (!pageData.nextPage) break;
        page = pageData.nextPage;
      }

      // The listing completed: anything still flagged as starred but absent
      // from this run was unstarred upstream. Flag it, never delete it.
      db.prepare(
        `UPDATE repos SET starred_upstream = 0
         WHERE starred_upstream = 1
           AND github_id NOT IN (SELECT github_id FROM job_seen WHERE job_id = ?)`,
      ).run(id);
      flush({ status: 'done', finished_at: now() });
    } catch (err) {
      flush({ status: 'failed', error: err.message, finished_at: now() });
    } finally {
      running.delete(id);
    }
  }

  async function runRefreshAll(id) {
    const state = running.get(id);
    const job = getRow(id);
    let { processed, failed } = job;
    let updated = job.updated;
    let failures = JSON.parse(job.failures || '[]');
    const ids = db.prepare('SELECT id FROM repos ORDER BY id').all().map((r) => r.id);

    try {
      for (const repoId of ids) {
        if (state.cancel) {
          update(id, { status: 'cancelled', processed, updated, failed, failures, finished_at: now() });
          return;
        }
        try {
          await refreshRepo(db, gh, repoId);
          updated += 1;
        } catch (err) {
          failed += 1;
          pushFailure(failures, `#${repoId}`, err.code || 'error');
        }
        processed += 1;
        if (processed % 10 === 0) update(id, { processed, updated, failed, failures });
      }
      update(id, { status: 'done', processed, updated, failed, failures, finished_at: now() });
    } catch (err) {
      update(id, { status: 'failed', processed, updated, failed, failures, error: err.message, finished_at: now() });
    } finally {
      running.delete(id);
    }
  }

  function insertJob(type, { username = null, options = {} } = {}) {
    const id = crypto.randomUUID();
    db.prepare(
      `INSERT INTO import_jobs (id, type, username, status, options, created_at)
       VALUES (?, ?, ?, 'running', ?, ?)`,
    ).run(id, type, username, JSON.stringify(options), now());
    return id;
  }

  return {
    startStarsImport({ username, includeReadme = true } = {}) {
      const id = insertJob('stars', { username, options: { includeReadme } });
      running.set(id, { cancel: false });
      setImmediate(() => runStarsImport(id));
      return toPublic(getRow(id));
    },

    startRefreshAll() {
      const id = insertJob('refresh_all');
      running.set(id, { cancel: false });
      setImmediate(() => runRefreshAll(id));
      return toPublic(getRow(id));
    },

    /** Resume a failed or cancelled stars import from its last committed page. */
    resumeJob(id) {
      const job = getRow(id);
      if (!job) return null;
      if (job.status === 'running') return toPublic(job);
      if (job.type !== 'stars') return toPublic(job);
      update(id, { status: 'running', error: null, finished_at: null });
      running.set(id, { cancel: false });
      setImmediate(() => runStarsImport(id));
      return toPublic(getRow(id));
    },

    cancelJob(id) {
      const state = running.get(id);
      if (state) {
        state.cancel = true;
        return true;
      }
      const job = getRow(id);
      if (job && job.status === 'running') {
        update(id, { status: 'cancelled', finished_at: now() });
        return true;
      }
      return false;
    },

    getJob: (id) => toPublic(getRow(id)),
    listJobs: () => db.prepare('SELECT * FROM import_jobs ORDER BY created_at DESC LIMIT 20').all().map(toPublic),
  };
}
