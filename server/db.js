import fs from 'node:fs';
import path from 'node:path';
import Database from 'better-sqlite3';

export const STATUSES = ['inbox', 'to_investigate', 'tried', 'adopted', 'dismissed'];
export const REFRESH_STATUSES = ['ok', 'not_found', 'inaccessible', 'error'];

const SCHEMA = `
CREATE TABLE IF NOT EXISTS repos (
  id INTEGER PRIMARY KEY,
  github_id INTEGER NOT NULL UNIQUE,
  owner TEXT NOT NULL,
  name TEXT NOT NULL,
  full_name TEXT NOT NULL,
  html_url TEXT NOT NULL,
  description TEXT NOT NULL DEFAULT '',
  topics TEXT NOT NULL DEFAULT '[]',
  language TEXT,
  license_id TEXT,
  archived INTEGER NOT NULL DEFAULT 0,
  pushed_at TEXT,
  stars INTEGER,
  default_branch TEXT,
  readme TEXT,
  readme_truncated INTEGER NOT NULL DEFAULT 0,
  fetched_at TEXT,
  refresh_status TEXT NOT NULL DEFAULT 'ok',
  starred_upstream INTEGER NOT NULL DEFAULT 0,
  seen_in_import INTEGER NOT NULL DEFAULT 0,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_repos_full_name ON repos (full_name COLLATE NOCASE);
CREATE INDEX IF NOT EXISTS idx_repos_fetched_at ON repos (fetched_at);

-- Personal knowledge is kept in a separate table so source refreshes can
-- never overwrite it (and vice versa).
CREATE TABLE IF NOT EXISTS annotations (
  repo_id INTEGER PRIMARY KEY REFERENCES repos(id) ON DELETE CASCADE,
  reason TEXT NOT NULL DEFAULT '',
  notes TEXT NOT NULL DEFAULT '',
  tags TEXT NOT NULL DEFAULT '[]',
  projects TEXT NOT NULL DEFAULT '[]',
  status TEXT NOT NULL DEFAULT 'inbox',
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);

-- FTS5 index over pre-tokenized text (see server/tokenize.js). The rowid is
-- the repos.id. Maintained explicitly (delete + insert) by reindexRepo().
CREATE VIRTUAL TABLE IF NOT EXISTS search_fts USING fts5(
  name, owner, description, topics, reason, notes, readme
);

CREATE TABLE IF NOT EXISTS settings (
  key TEXT PRIMARY KEY,
  value TEXT
);

CREATE TABLE IF NOT EXISTS import_jobs (
  id TEXT PRIMARY KEY,
  type TEXT NOT NULL,
  username TEXT,
  status TEXT NOT NULL,
  options TEXT NOT NULL DEFAULT '{}',
  next_page INTEGER NOT NULL DEFAULT 1,
  processed INTEGER NOT NULL DEFAULT 0,
  added INTEGER NOT NULL DEFAULT 0,
  updated INTEGER NOT NULL DEFAULT 0,
  failed INTEGER NOT NULL DEFAULT 0,
  failures TEXT NOT NULL DEFAULT '[]',
  error TEXT,
  created_at TEXT NOT NULL,
  finished_at TEXT
);

-- GitHub ids seen during a completed stars import, used to reconcile upstream
-- unstars without deleting local records.
CREATE TABLE IF NOT EXISTS job_seen (
  job_id TEXT NOT NULL,
  github_id INTEGER NOT NULL,
  PRIMARY KEY (job_id, github_id)
);
`;

export function openDatabase(file) {
  if (file !== ':memory:') {
    fs.mkdirSync(path.dirname(file), { recursive: true });
  }
  const db = new Database(file);
  db.pragma('journal_mode = WAL');
  db.pragma('foreign_keys = ON');
  db.exec(SCHEMA);
  return db;
}

export function getSetting(db, key) {
  const row = db.prepare('SELECT value FROM settings WHERE key = ?').get(key);
  return row ? row.value : null;
}

export function setSetting(db, key, value) {
  if (value === null || value === undefined) {
    db.prepare('DELETE FROM settings WHERE key = ?').run(key);
  } else {
    db.prepare('INSERT INTO settings (key, value) VALUES (?, ?) ON CONFLICT(key) DO UPDATE SET value = excluded.value').run(key, String(value));
  }
}
