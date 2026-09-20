// Local keyword search over FTS5 with mixed Chinese/English tokenization,
// field weighting, filters and highlighted snippets. No network or model
// involved; everything runs against the local SQLite index.

import { tokenizeQuery, isCjkChar } from './tokenize.js';
import { STATUSES } from './db.js';

// Column order matches search_fts: name, owner, description, topics, reason, notes, readme.
// Repository names and personal intent (reason/notes) outrank README text.
const RANK_EXPR = 'bm25(search_fts, 10.0, 4.0, 3.0, 5.0, 8.0, 8.0, 1.0)';
const EXACT_NAME_BOOST = -10;

const FIELD_PRIORITY = ['name', 'reason', 'notes', 'description', 'topics', 'owner', 'readme'];
const SNIPPET_WIDTH = 180;
const README_SNIPPET_SCAN_LIMIT = 400_000;

export function escapeHtml(text) {
  return String(text).replace(/[&<>"']/g, (ch) => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  }[ch]));
}

function escapeRegExp(text) {
  return text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function tokenMatcher(token) {
  if ([...token].some(isCjkChar)) {
    const needle = token;
    return {
      test: (text) => text.includes(needle),
      index: (text) => text.indexOf(needle),
      // Tokens never contain HTML-significant characters, so marking the
      // escaped snippet with the raw token is safe.
      mark: (escaped) => escaped.split(needle).join('<mark>' + needle + '</mark>'),
    };
  }
  const re = new RegExp(`(?<![A-Za-z0-9_])${escapeRegExp(token)}(?![A-Za-z0-9_])`, 'i');
  const reGlobal = new RegExp(`(?<![A-Za-z0-9_])${escapeRegExp(token)}(?![A-Za-z0-9_])`, 'gi');
  return {
    test: (text) => re.test(text),
    index: (text) => {
      const m = re.exec(text);
      return m ? m.index : -1;
    },
    mark: (escaped) => escaped.replace(reGlobal, (m) => `<mark>${m}</mark>`),
  };
}

function buildMatchers(tokens) {
  return tokens.map(tokenMatcher);
}

// READMEs often start with raw HTML blocks (centered headers, badges);
// strip script/style bodies and all tags so snippets show prose only.
function stripMarkup(text) {
  if (!text) return text;
  return text
    .replace(/<(script|style)[^>]*>[\s\S]*?<\/\1>/gi, ' ')
    .replace(/<[^>]+>/g, ' ')
    .replace(/\s{2,}/g, ' ')
    .trim();
}

function fieldValues(row) {
  let topics = [];
  try {
    topics = JSON.parse(row.topics || '[]');
  } catch { topics = []; }
  return {
    name: row.name || '',
    owner: row.owner || '',
    description: row.description || '',
    topics: topics.join(' '),
    reason: row.reason || '',
    notes: row.notes || '',
    readme: stripMarkup((row.readme || '').slice(0, README_SNIPPET_SCAN_LIMIT)),
  };
}

export function highlightRow(row, tokens) {
  const fields = fieldValues(row);
  const matchers = buildMatchers(tokens);
  const matchedFields = [];
  for (const field of FIELD_PRIORITY) {
    const text = fields[field];
    if (text && matchers.some((m) => m.test(text))) matchedFields.push(field);
  }
  let snippet = '';
  let snippetField = null;
  for (const field of FIELD_PRIORITY) {
    if (!matchedFields.includes(field)) continue;
    const text = fields[field];
    let firstIndex = -1;
    for (const m of matchers) {
      const idx = m.index(text);
      if (idx !== -1 && (firstIndex === -1 || idx < firstIndex)) firstIndex = idx;
    }
    if (firstIndex === -1) continue;
    const start = Math.max(0, firstIndex - Math.floor(SNIPPET_WIDTH / 3));
    const end = Math.min(text.length, start + SNIPPET_WIDTH);
    let escaped = (start > 0 ? '…' : '') + escapeHtml(text.slice(start, end)) + (end < text.length ? '…' : '');
    for (const m of matchers) escaped = m.mark(escaped);
    snippet = escaped;
    snippetField = field;
    break;
  }
  return { matchedFields, snippet, snippetField };
}

function ftsMatchQuery(tokens) {
  // Quoted phrases per token avoid FTS5 query-syntax injection; AND requires
  // every token (e.g. every Chinese bigram of the query) to be present.
  // The last Latin token gets a prefix marker so typeahead works:
  // "ar" matches the indexed token "archify".
  const isCjk = (t) => [...t].some((c) => /[\u3400-\u4DBF\u4E00-\u9FFF\uF900-\uFAFF]/.test(c));
  return tokens.map((t, i) => {
    const quoted = `"${t.replace(/"/g, '""')}"`;
    return i === tokens.length - 1 && !isCjk(t) ? `${quoted} *` : quoted;
  }).join(' AND ');
}

function buildFilters(filters) {
  const clauses = [];
  const params = {};
  if (filters.status) {
    if (!STATUSES.includes(filters.status)) throw Object.assign(new Error('invalid status filter'), { code: 'invalid_filter' });
    clauses.push(`COALESCE(a.status, 'inbox') = @status`);
    params.status = filters.status;
  }
  if (filters.tag) {
    clauses.push(`EXISTS (SELECT 1 FROM json_each(COALESCE(a.tags, '[]')) ft WHERE ft.value = @tag)`);
    params.tag = filters.tag;
  }
  if (filters.project) {
    clauses.push(`EXISTS (SELECT 1 FROM json_each(COALESCE(a.projects, '[]')) fp WHERE fp.value = @project)`);
    params.project = filters.project;
  }
  if (filters.language) {
    clauses.push('r.language = @language COLLATE NOCASE');
    params.language = filters.language;
  }
  if (filters.archived === true || filters.archived === false) {
    clauses.push('r.archived = @archived');
    params.archived = filters.archived ? 1 : 0;
  }
  return { clauses, params };
}

const LIST_SELECT = `
  SELECT r.*, a.reason, a.notes,
         a.tags AS a_tags, a.projects AS a_projects, a.status,
         a.created_at AS a_created_at, a.updated_at AS a_updated_at`;

/**
 * Search or list repositories.
 * With a query: FTS5 keyword match (AND over tokens) ranked by weighted bm25
 * plus an exact-name boost. Without a query: filtered listing by save time.
 */
export function searchRepos(db, {
  q = '',
  status = null,
  tag = null,
  project = null,
  language = null,
  archived = null,
  limit = 50,
  offset = 0,
} = {}) {
  limit = Math.min(Math.max(Number(limit) || 50, 1), 200);
  offset = Math.max(Number(offset) || 0, 0);
  const tokens = tokenizeQuery(q);
  const { clauses, params } = buildFilters({ status, tag, project, language, archived });
  const whereExtra = clauses.length ? ` AND ${clauses.join(' AND ')}` : '';

  let rows;
  let total;
  if (tokens.length > 0) {
    const match = ftsMatchQuery(tokens);
    const base = `FROM search_fts
      JOIN repos r ON r.id = search_fts.rowid
      LEFT JOIN annotations a ON a.repo_id = r.id
      WHERE search_fts MATCH @match${whereExtra}`;
    total = db.prepare(`SELECT COUNT(*) AS c ${base}`).get({ match, ...params }).c;
    rows = db.prepare(
      `SELECT r.*, a.reason, a.notes,
              a.tags AS a_tags, a.projects AS a_projects, a.status,
              a.created_at AS a_created_at, a.updated_at AS a_updated_at,
              (${RANK_EXPR}
                 + CASE WHEN lower(r.name) = lower(@rawq) OR lower(r.full_name) = lower(@rawq)
                        THEN @boost ELSE 0 END) AS rank
       ${base}
       ORDER BY rank ASC
       LIMIT @limit OFFSET @offset`,
    ).all({ match, rawq: String(q).trim(), boost: EXACT_NAME_BOOST, ...params, limit, offset });
  } else {
    const base = `FROM repos r LEFT JOIN annotations a ON a.repo_id = r.id WHERE 1 = 1${whereExtra}`;
    total = db.prepare(`SELECT COUNT(*) AS c ${base}`).get(params).c;
    rows = db.prepare(
      `${LIST_SELECT} ${base} ORDER BY r.created_at DESC, r.id DESC LIMIT @limit OFFSET @offset`,
    ).all({ ...params, limit, offset });
  }

  const items = rows.map((row) => {
    const highlights = tokens.length ? highlightRow(row, tokens) : { matchedFields: [], snippet: '', snippetField: null };
    return { row, ...highlights };
  });
  return { total, items, tokens };
}
