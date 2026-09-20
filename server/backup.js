// Versioned JSON export/import of the whole library: repository records,
// personal fields and README snapshots. Credentials (pairing token, GitHub
// token) live in the settings table and are never exported.

import { upsertSource, reindexRepo } from './repos.js';

export const BACKUP_VERSION = 1;

const now = () => new Date().toISOString();

export class BackupError extends Error {
  constructor(code, message) {
    super(message);
    this.name = 'BackupError';
    this.code = code;
  }
}

export function exportData(db) {
  const rows = db.prepare(
    `SELECT r.*, a.reason, a.notes,
            a.tags AS a_tags, a.projects AS a_projects, a.status,
            a.created_at AS a_created_at, a.updated_at AS a_updated_at
     FROM repos r LEFT JOIN annotations a ON a.repo_id = r.id
     ORDER BY r.id`,
  ).all();
  return {
    app: 'repo-shelf',
    version: BACKUP_VERSION,
    exportedAt: now(),
    repos: rows.map((row) => ({
      githubId: row.github_id,
      owner: row.owner,
      name: row.name,
      fullName: row.full_name,
      htmlUrl: row.html_url,
      description: row.description,
      topics: JSON.parse(row.topics || '[]'),
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
      starredUpstream: Boolean(row.starred_upstream),
      seenInImport: Boolean(row.seen_in_import),
      createdAt: row.created_at,
      annotation: row.a_created_at ? {
        reason: row.reason || '',
        notes: row.notes || '',
        tags: JSON.parse(row.a_tags || '[]'),
        projects: JSON.parse(row.a_projects || '[]'),
        status: row.status || 'inbox',
        createdAt: row.a_created_at,
        updatedAt: row.a_updated_at,
      } : null,
    })),
  };
}

function validateItem(item, index) {
  if (!item || typeof item !== 'object') throw new BackupError('invalid_backup', `repos[${index}] is not an object`);
  if (!Number.isInteger(item.githubId)) throw new BackupError('invalid_backup', `repos[${index}].githubId must be an integer`);
  if (typeof item.fullName !== 'string' || !item.fullName.includes('/')) {
    throw new BackupError('invalid_backup', `repos[${index}].fullName must look like "owner/repo"`);
  }
}

/**
 * Restore a backup. mode 'merge' keeps existing records untouched;
 * mode 'overwrite' replaces both source and personal fields of existing ones.
 */
export function importData(db, payload, { mode = 'merge' } = {}) {
  if (!payload || payload.app !== 'repo-shelf' || typeof payload.version !== 'number') {
    throw new BackupError('invalid_backup', 'Not a Repo Shelf export file');
  }
  if (payload.version > BACKUP_VERSION) {
    throw new BackupError('unsupported_version', `Backup version ${payload.version} is newer than supported version ${BACKUP_VERSION}`);
  }
  if (!Array.isArray(payload.repos)) throw new BackupError('invalid_backup', 'Export file has no repos array');
  if (mode !== 'merge' && mode !== 'overwrite') throw new BackupError('invalid_mode', `Unknown restore mode "${mode}"`);

  const counts = { added: 0, skipped: 0, overwritten: 0 };
  const ts = now();

  const run = db.transaction((items) => {
    items.forEach((item, index) => {
      validateItem(item, index);
      const existing = db.prepare('SELECT id FROM repos WHERE github_id = ?').get(item.githubId);
      if (existing && mode === 'merge') {
        counts.skipped += 1;
        return;
      }
      const meta = {
        githubId: item.githubId,
        owner: item.owner ?? item.fullName.split('/')[0],
        name: item.name ?? item.fullName.split('/')[1],
        fullName: item.fullName,
        htmlUrl: item.htmlUrl ?? `https://github.com/${item.fullName}`,
        description: item.description ?? '',
        topics: Array.isArray(item.topics) ? item.topics : [],
        language: item.language ?? null,
        licenseId: item.licenseId ?? null,
        archived: Boolean(item.archived),
        pushedAt: item.pushedAt ?? null,
        stars: typeof item.stars === 'number' ? item.stars : null,
        defaultBranch: item.defaultBranch ?? null,
      };
      const { repoId } = upsertSource(db, meta, {
        readme: item.readme ?? null,
        readmeTruncated: item.readmeTruncated ? 1 : 0,
        starred: item.starredUpstream ? 1 : 0,
        refreshStatus: item.refreshStatus || 'ok',
      });
      if (existing) {
        counts.overwritten += 1;
      } else {
        counts.added += 1;
      }
      // Restore original timestamps and import-history flag.
      if (item.createdAt) db.prepare('UPDATE repos SET created_at = ? WHERE id = ?').run(item.createdAt, repoId);
      if (item.fetchedAt) db.prepare('UPDATE repos SET fetched_at = ? WHERE id = ?').run(item.fetchedAt, repoId);
      if (item.seenInImport) db.prepare('UPDATE repos SET seen_in_import = 1 WHERE id = ?').run(repoId);
      if (item.annotation) {
        const a = item.annotation;
        db.prepare(
          `UPDATE annotations SET reason = ?, notes = ?, tags = ?, projects = ?, status = ?, updated_at = ?
           WHERE repo_id = ?`,
        ).run(
          typeof a.reason === 'string' ? a.reason : '',
          typeof a.notes === 'string' ? a.notes : '',
          JSON.stringify(Array.isArray(a.tags) ? a.tags.filter((t) => typeof t === 'string') : []),
          JSON.stringify(Array.isArray(a.projects) ? a.projects.filter((p) => typeof p === 'string') : []),
          typeof a.status === 'string' ? a.status : 'inbox',
          ts,
          repoId,
        );
        reindexRepo(db, repoId);
      }
    });
  });
  run(payload.repos);
  return counts;
}
