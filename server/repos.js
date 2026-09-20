// Repository storage service: identity, capture, refresh and personal
// annotations. Source metadata lives in `repos`, personal knowledge in
// `annotations`; refresh paths only ever write to `repos`.

import { parseGitHubRepoUrl, NormalizeError } from './normalize.js';
import { tokenizeForIndex } from './tokenize.js';
import { GitHubError } from './github.js';
import { STATUSES } from './db.js';

export const STALE_AFTER_MS = 7 * 24 * 60 * 60 * 1000;
const INDEX_TEXT_LIMIT = 200_000;

const now = () => new Date().toISOString();

export class ServiceError extends Error {
  constructor(code, httpStatus, message, { retryable = false, retryAfterMs = null } = {}) {
    super(message);
    this.name = 'ServiceError';
    this.code = code;
    this.httpStatus = httpStatus;
    this.retryable = retryable;
    this.retryAfterMs = retryAfterMs;
  }
}

function mapGitHubError(err, { notFoundMessage } = {}) {
  if (!(err instanceof GitHubError)) {
    return new ServiceError('upstream_unavailable', 502, `Could not reach GitHub: ${err.message}`, { retryable: true });
  }
  switch (err.code) {
    case 'not_found':
      return new ServiceError('repository_not_found', 404, notFoundMessage || err.message);
    case 'rate_limited':
      return new ServiceError('github_rate_limited', 502, err.message, { retryable: true, retryAfterMs: err.retryAfterMs });
    case 'forbidden':
      return new ServiceError('github_forbidden', 502, err.message);
    case 'unauthorized':
      return new ServiceError('github_unauthorized', 502, err.message);
    default:
      return new ServiceError('upstream_unavailable', 502, err.message, { retryable: true });
  }
}

function parseJsonArray(text) {
  try {
    const value = JSON.parse(text || '[]');
    return Array.isArray(value) ? value : [];
  } catch {
    return [];
  }
}

function cut(text, limit) {
  if (!text) return '';
  return text.length > limit ? text.slice(0, limit) : text;
}

/** Rebuild the FTS row for one repository from source + personal fields. */
export function reindexRepo(db, repoId) {
  const row = db.prepare(
    `SELECT r.name, r.owner, r.description, r.topics, r.readme,
            a.reason, a.notes
     FROM repos r LEFT JOIN annotations a ON a.repo_id = r.id
     WHERE r.id = ?`,
  ).get(repoId);
  db.prepare('DELETE FROM search_fts WHERE rowid = ?').run(repoId);
  if (!row) return;
  db.prepare(
    `INSERT INTO search_fts (rowid, name, owner, description, topics, reason, notes, readme)
     VALUES (?, ?, ?, ?, ?, ?, ?, ?)`,
  ).run(
    repoId,
    tokenizeForIndex(row.name),
    tokenizeForIndex(row.owner),
    tokenizeForIndex(row.description),
    tokenizeForIndex(parseJsonArray(row.topics).join(' ')),
    tokenizeForIndex(row.reason),
    tokenizeForIndex(row.notes),
    tokenizeForIndex(cut(row.readme, INDEX_TEXT_LIMIT)),
  );
}

/** Map a joined repos+annotations row (SELECT_JOINED shape) to a record. */
export function toRecord(row) {
  if (!row) return null;
  const fetchedAt = row.fetched_at ? Date.parse(row.fetched_at) : null;
  return {
    id: row.id,
    githubId: row.github_id,
    owner: row.owner,
    name: row.name,
    fullName: row.full_name,
    htmlUrl: row.html_url,
    description: row.description,
    topics: parseJsonArray(row.topics),
    language: row.language,
    licenseId: row.license_id,
    archived: Boolean(row.archived),
    pushedAt: row.pushed_at,
    stars: row.stars,
    defaultBranch: row.default_branch,
    readme: row.readme,
    readmeTruncated: Boolean(row.readme_truncated),
    fetchedAt: row.fetched_at,
    refreshStatus: row.refresh_status,
    stale: fetchedAt === null ? true : Date.now() - fetchedAt > STALE_AFTER_MS,
    starredUpstream: Boolean(row.starred_upstream),
    seenInImport: Boolean(row.seen_in_import),
    createdAt: row.created_at,
    updatedAt: row.updated_at,
    annotation: {
      reason: row.reason ?? '',
      notes: row.notes ?? '',
      tags: parseJsonArray(row.a_tags ?? row.tags),
      projects: parseJsonArray(row.a_projects ?? row.projects),
      status: row.status ?? 'inbox',
      createdAt: row.a_created_at ?? null,
      updatedAt: row.a_updated_at ?? null,
    },
  };
}

const SELECT_JOINED = `
  SELECT r.*, a.reason, a.notes,
         a.tags AS a_tags, a.projects AS a_projects, a.status,
         a.created_at AS a_created_at, a.updated_at AS a_updated_at
  FROM repos r LEFT JOIN annotations a ON a.repo_id = r.id`;

export function getRepo(db, id) {
  const row = db.prepare(`${SELECT_JOINED} WHERE r.id = ?`).get(id);
  return toRecord(row);
}

export function findByFullName(db, fullName) {
  const row = db.prepare(`${SELECT_JOINED} WHERE r.full_name = ? COLLATE NOCASE ORDER BY r.fetched_at DESC LIMIT 1`).get(fullName);
  return toRecord(row);
}

function ensureAnnotation(db, repoId) {
  const ts = now();
  db.prepare(
    `INSERT INTO annotations (repo_id, created_at, updated_at) VALUES (?, ?, ?)
     ON CONFLICT(repo_id) DO NOTHING`,
  ).run(repoId, ts, ts);
}

/**
 * Insert or update source metadata keyed by the durable GitHub repository id.
 * Never touches annotation fields. `readme === undefined` keeps the snapshot.
 */
export function upsertSource(db, meta, { readme, readmeTruncated = 0, starred = null, refreshStatus = 'ok' } = {}) {
  const existing = db.prepare('SELECT id, full_name FROM repos WHERE github_id = ?').get(meta.githubId);
  const ts = now();
  let repoId;
  let created = false;
  if (existing) {
    repoId = existing.id;
    db.prepare(
      `UPDATE repos SET owner = ?, name = ?, full_name = ?, html_url = ?, description = ?,
         topics = ?, language = ?, license_id = ?, archived = ?, pushed_at = ?, stars = ?,
         default_branch = ?, fetched_at = ?, refresh_status = ?, updated_at = ?
         ${starred === null ? '' : ', starred_upstream = ?'}
         ${starred === 1 || starred === true ? ', seen_in_import = 1' : ''}
         ${readme === undefined ? '' : ', readme = ?, readme_truncated = ?'}
       WHERE id = ?`,
    ).run(...[
      meta.owner, meta.name, meta.fullName, meta.htmlUrl, meta.description || '',
      JSON.stringify(meta.topics || []), meta.language, meta.licenseId, meta.archived ? 1 : 0,
      meta.pushedAt, meta.stars, meta.defaultBranch, ts, refreshStatus, ts,
    ].concat(starred === null ? [] : [starred ? 1 : 0])
      .concat(readme === undefined ? [] : [readme, readmeTruncated ? 1 : 0])
      .concat([repoId]));
  } else {
    const info = db.prepare(
      `INSERT INTO repos (github_id, owner, name, full_name, html_url, description, topics,
         language, license_id, archived, pushed_at, stars, default_branch, readme,
         readme_truncated, fetched_at, refresh_status, starred_upstream, seen_in_import, created_at, updated_at)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
    ).run(
      meta.githubId, meta.owner, meta.name, meta.fullName, meta.htmlUrl, meta.description || '',
      JSON.stringify(meta.topics || []), meta.language, meta.licenseId, meta.archived ? 1 : 0,
      meta.pushedAt, meta.stars, meta.defaultBranch,
      readme === undefined ? null : readme, readmeTruncated ? 1 : 0,
      ts, refreshStatus, starred ? 1 : 0, starred ? 1 : 0, ts, ts,
    );
    repoId = Number(info.lastInsertRowid);
    created = true;
  }
  ensureAnnotation(db, repoId);
  reindexRepo(db, repoId);
  return { repoId, created, renamed: existing ? existing.full_name !== meta.fullName : false };
}

function mergeAnnotationOnSave(db, repoId, { reason = '', tags = [] } = {}) {
  ensureAnnotation(db, repoId);
  const current = db.prepare('SELECT reason, tags FROM annotations WHERE repo_id = ?').get(repoId);
  const cleanReason = typeof reason === 'string' ? reason.trim().slice(0, 2000) : '';
  const cleanTags = Array.isArray(tags) ? tags.filter((t) => typeof t === 'string').map((t) => t.trim()).filter(Boolean).slice(0, 30) : [];
  let changed = false;
  if (cleanReason && !current.reason.trim()) {
    db.prepare('UPDATE annotations SET reason = ? WHERE repo_id = ?').run(cleanReason, repoId);
    changed = true;
  }
  if (cleanTags.length) {
    const merged = [...new Set([...parseJsonArray(current.tags), ...cleanTags])];
    db.prepare('UPDATE annotations SET tags = ? WHERE repo_id = ?').run(JSON.stringify(merged), repoId);
    changed = true;
  }
  if (changed) {
    db.prepare('UPDATE annotations SET updated_at = ? WHERE repo_id = ?').run(now(), repoId);
    reindexRepo(db, repoId);
  }
  return changed;
}

/**
 * Save a repository by URL. Outcomes:
 *  - created: fetched metadata from GitHub and stored a new record
 *  - already_exists: URL is already in the library (personal fields preserved,
 *    optional reason/tags merged in non-destructively)
 * Throws ServiceError for invalid URLs and retryable upstream failures.
 */
export async function saveRepo(db, gh, { url, reason = '', tags = [] } = {}) {
  let parsed;
  try {
    parsed = parseGitHubRepoUrl(url);
  } catch (err) {
    if (err instanceof NormalizeError) throw new ServiceError('invalid_url', 400, err.message);
    throw err;
  }

  const existing = findByFullName(db, parsed.fullName);
  if (existing) {
    // Already in the library: never auto-refresh. GitHub quota is a budget;
    // the user refreshes explicitly from the detail view.
    const annotationChanged = mergeAnnotationOnSave(db, existing.id, { reason, tags });
    return { outcome: 'already_exists', repo: getRepo(db, existing.id), refreshed: false, refreshError: null, annotationChanged };
  }

  let meta;
  try {
    meta = await gh.fetchRepo(parsed.owner, parsed.repo);
  } catch (err) {
    throw mapGitHubError(err, { notFoundMessage: `Repository ${parsed.fullName} was not found on GitHub` });
  }
  let readme;
  let readmeTruncated = 0;
  let readmeError = null;
  try {
    const result = await gh.fetchReadme(meta.owner, meta.name);
    if (result) {
      readme = result.text;
      readmeTruncated = result.truncated ? 1 : 0;
    }
  } catch (err) {
    readmeError = err.code || 'error';
  }
  const { repoId } = upsertSource(db, meta, { readme, readmeTruncated });
  mergeAnnotationOnSave(db, repoId, { reason, tags });
  return { outcome: 'created', repo: getRepo(db, repoId), readmeError };
}

function markRefreshStatus(db, repoId, status) {
  db.prepare('UPDATE repos SET refresh_status = ?, updated_at = ? WHERE id = ?').run(status, now(), repoId);
}

/** Refresh source metadata/README from GitHub. Personal fields are untouched. */
export async function refreshRepo(db, gh, repoId) {
  const repo = getRepo(db, repoId);
  if (!repo) throw new ServiceError('not_found', 404, `No repository with id ${repoId}`);
  let meta;
  try {
    meta = await gh.fetchRepo(repo.owner, repo.name);
  } catch (err) {
    if (err instanceof GitHubError && err.code === 'not_found') {
      // Possibly renamed or transferred: resolve the durable id instead.
      try {
        meta = await gh.fetchRepoById(repo.githubId);
      } catch (err2) {
        const status = err2 instanceof GitHubError && err2.code === 'not_found' ? 'not_found'
          : err2 instanceof GitHubError && (err2.code === 'forbidden' || err2.code === 'unauthorized') ? 'inaccessible'
            : 'error';
        markRefreshStatus(db, repoId, status);
        throw mapGitHubError(err2, { notFoundMessage: `${repo.fullName} no longer exists on GitHub (deleted or made private)` });
      }
    } else {
      const status = err instanceof GitHubError && (err.code === 'forbidden' || err.code === 'unauthorized') ? 'inaccessible' : 'error';
      markRefreshStatus(db, repoId, status);
      throw mapGitHubError(err);
    }
  }
  let readme;
  let readmeTruncated = 0;
  try {
    const result = await gh.fetchReadme(meta.owner, meta.name);
    if (result) {
      readme = result.text;
      readmeTruncated = result.truncated ? 1 : 0;
    }
  } catch {
    readme = undefined; // keep the previous snapshot when the README fetch fails
  }
  const { renamed } = upsertSource(db, meta, { readme, readmeTruncated });
  return { repo: getRepo(db, repoId), renamed };
}

export function updateAnnotation(db, repoId, patch = {}) {
  const repo = db.prepare('SELECT id FROM repos WHERE id = ?').get(repoId);
  if (!repo) throw new ServiceError('not_found', 404, `No repository with id ${repoId}`);
  ensureAnnotation(db, repoId);

  const updates = {};
  if ('reason' in patch) {
    if (typeof patch.reason !== 'string' || patch.reason.length > 2000) throw new ServiceError('invalid_field', 400, 'reason must be a string of at most 2000 characters');
    updates.reason = patch.reason;
  }
  if ('notes' in patch) {
    if (typeof patch.notes !== 'string' || patch.notes.length > 100_000) throw new ServiceError('invalid_field', 400, 'notes must be a string of at most 100000 characters');
    updates.notes = patch.notes;
  }
  if ('status' in patch) {
    if (!STATUSES.includes(patch.status)) throw new ServiceError('invalid_field', 400, `status must be one of: ${STATUSES.join(', ')}`);
    updates.status = patch.status;
  }
  for (const field of ['tags', 'projects']) {
    if (field in patch) {
      const value = patch[field];
      if (!Array.isArray(value) || value.length > 50 || value.some((v) => typeof v !== 'string' || v.length > 100)) {
        throw new ServiceError('invalid_field', 400, `${field} must be an array of up to 50 strings of at most 100 characters`);
      }
      updates[field] = JSON.stringify([...new Set(value.map((v) => v.trim()).filter(Boolean))]);
    }
  }
  const keys = Object.keys(updates);
  if (keys.length === 0) throw new ServiceError('invalid_field', 400, 'No updatable fields supplied');
  const assignments = keys.map((k) => `${k} = ?`).join(', ');
  db.prepare(`UPDATE annotations SET ${assignments}, updated_at = ? WHERE repo_id = ?`)
    .run(...keys.map((k) => updates[k]), now(), repoId);
  reindexRepo(db, repoId);
  return getRepo(db, repoId).annotation;
}

export function deleteRepo(db, repoId) {
  const info = db.prepare('DELETE FROM repos WHERE id = ?').run(repoId);
  db.prepare('DELETE FROM search_fts WHERE rowid = ?').run(repoId);
  if (info.changes === 0) throw new ServiceError('not_found', 404, `No repository with id ${repoId}`);
  return { deleted: true };
}

/** Distinct values used to populate filter dropdowns in the UI. */
export function listFilterOptions(db) {
  const languages = db.prepare('SELECT DISTINCT language FROM repos WHERE language IS NOT NULL ORDER BY language COLLATE NOCASE').all().map((r) => r.language);
  const tags = db.prepare(
    `SELECT DISTINCT je.value AS tag FROM annotations a, json_each(a.tags) je WHERE a.tags != '[]' ORDER BY tag`,
  ).all().map((r) => r.tag);
  const projects = db.prepare(
    `SELECT DISTINCT je.value AS project FROM annotations a, json_each(a.projects) je WHERE a.projects != '[]' ORDER BY project`,
  ).all().map((r) => r.project);
  const total = db.prepare('SELECT COUNT(*) AS c FROM repos').get().c;
  return { languages, tags, projects, total };
}
