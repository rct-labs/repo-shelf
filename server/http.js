// Local HTTP API + static UI server.
//
// Security model (loopback-only service):
// - Every /api/* request (except /api/health) must send the pairing token in
//   the X-RepoShelf-Token header. The web UI receives the token embedded in
//   the served HTML (readable only same-origin, thanks to SOP); the browser
//   extension stores it after manual pairing.
// - Requests carrying an Origin header are only honored when the origin is
//   the app's own loopback origin or a browser-extension scheme; arbitrary
//   websites cannot read or mutate the library (CORS + origin checks).
// - The Host header must be the loopback listener, which blocks DNS-rebinding
//   attempts.

import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';

import { STATUSES } from './db.js';
import { APP_VERSION } from './config.js';
import { ServiceError, saveRepo, getRepo, updateAnnotation, deleteRepo, refreshRepo, listFilterOptions, toRecord, findByFullName } from './repos.js';
import { parseGitHubRepoUrl } from './normalize.js';
import { searchRepos } from './search.js';
import { exportData, importData, BackupError } from './backup.js';
import { getSetting, setSetting } from './db.js';

const JSON_LIMIT = 1_000_000;
const RESTORE_LIMIT = 64 * 1_000_000;

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.ico': 'image/x-icon',
};

function sendJson(res, status, body, extraHeaders = {}) {
  const payload = JSON.stringify(body);
  res.writeHead(status, {
    'content-type': 'application/json; charset=utf-8',
    'cache-control': 'no-store',
    ...extraHeaders,
  });
  res.end(payload);
}

function sendError(res, status, code, message, extra = {}) {
  sendJson(res, status, { ok: false, error: { code, message, ...extra } });
}

function readBody(req, limit) {
  return new Promise((resolve, reject) => {
    const chunks = [];
    let size = 0;
    req.on('data', (chunk) => {
      size += chunk.length;
      if (size > limit) {
        reject(new ServiceError('payload_too_large', 413, 'Request body is too large'));
        req.destroy();
        return;
      }
      chunks.push(chunk);
    });
    req.on('end', () => resolve(Buffer.concat(chunks).toString('utf8')));
    req.on('error', reject);
  });
}

async function readJson(req, limit) {
  const text = await readBody(req, limit);
  if (!text) return {};
  try {
    return JSON.parse(text);
  } catch {
    throw new ServiceError('invalid_json', 400, 'Request body is not valid JSON');
  }
}

function compileRoutes(routes) {
  return routes.map(([method, pattern, handler]) => {
    const keys = [];
    const regex = new RegExp(`^${pattern.replace(/:[^/]+/g, (m) => {
      keys.push(m.slice(1));
      return '([^/]+)';
    })}$`);
    return { method, regex, keys, handler };
  });
}

export function createApp({ db, gh, jobs, config, pairingToken, publicDir, vendorFiles = {} }) {
  const ownOrigins = new Set([
    `http://127.0.0.1:${config.port}`,
    `http://localhost:${config.port}`,
    `http://[::1]:${config.port}`,
  ]);
  const validHosts = new Set([
    `127.0.0.1:${config.port}`,
    `localhost:${config.port}`,
    `[::1]:${config.port}`,
  ]);

  function authorize(req, pathname) {
    const host = (req.headers.host || '').toLowerCase();
    if (!validHosts.has(host)) {
      return { ok: false, status: 403, code: 'invalid_host', message: 'Requests must address the loopback listener directly' };
    }
    const origin = req.headers.origin;
    let extensionOrigin = null;
    if (origin) {
      if (ownOrigins.has(origin)) {
        // The app's own web UI.
      } else if (origin.startsWith('chrome-extension://') || origin.startsWith('moz-extension://')) {
        extensionOrigin = origin;
      } else {
        return { ok: false, status: 403, code: 'origin_not_allowed', message: `Origin "${origin}" is not allowed` };
      }
    }
    if (pathname === '/api/health') return { ok: true, extensionOrigin };
    if (req.headers['x-reposhelf-token'] !== pairingToken) {
      return { ok: false, status: 401, code: 'unauthorized', message: 'Missing or invalid pairing token' };
    }
    return { ok: true, extensionOrigin };
  }

  function handlePreflight(req, res) {
    const origin = req.headers.origin || '';
    const headers = {
      'access-control-allow-methods': 'GET, POST, PUT, PATCH, DELETE, OPTIONS',
      'access-control-allow-headers': 'content-type, x-reposhelf-token',
      'access-control-max-age': '600',
    };
    if (origin.startsWith('chrome-extension://') || origin.startsWith('moz-extension://')) {
      headers['access-control-allow-origin'] = origin;
    }
    res.writeHead(204, headers);
    res.end();
  }

  async function routeApi(req, res, url) {
    const p = url.pathname;

    if (req.method === 'GET' && p === '/api/health') {
      return sendJson(res, 200, { ok: true, data: { app: 'repo-shelf', version: APP_VERSION } });
    }

    if (req.method === 'POST' && p === '/api/repos') {
      const body = await readJson(req, JSON_LIMIT);
      const result = await saveRepo(db, gh, body);
      return sendJson(res, result.outcome === 'created' ? 201 : 200, { ok: true, data: result });
    }

    if (req.method === 'GET' && p === '/api/repos') {
      const sp = url.searchParams;
      const archivedParam = sp.get('archived');
      const result = searchRepos(db, {
        q: sp.get('q') || '',
        status: sp.get('status') || null,
        tag: sp.get('tag') || null,
        project: sp.get('project') || null,
        language: sp.get('language') || null,
        archived: archivedParam === 'true' ? true : archivedParam === 'false' ? false : null,
        limit: sp.get('limit'),
        offset: sp.get('offset'),
      });
      const items = result.items.map(({ row, matchedFields, snippet, snippetField }) => {
        const record = toRecord(row);
        const hasReadme = Boolean(record.readme);
        record.readme = null; // list payloads stay small; GET /api/repos/:id returns it
        return { repo: { ...record, hasReadme }, matchedFields, snippet, snippetField, rank: row.rank ?? null };
      });
      return sendJson(res, 200, { ok: true, data: { total: result.total, items } });
    }

    if (req.method === 'GET' && p === '/api/repos/lookup') {
      const urlParam = url.searchParams.get('url') || '';
      try {
        const parsed = parseGitHubRepoUrl(urlParam);
        const repo = findByFullName(db, parsed.fullName);
        if (repo) repo.readme = null; // lookup stays light
        return sendJson(res, 200, { ok: true, data: { valid: true, fullName: parsed.fullName, found: Boolean(repo), repo } });
      } catch {
        return sendJson(res, 200, { ok: true, data: { valid: false } });
      }
    }

    const repoMatch = p.match(/^\/api\/repos\/(\d+)$/);
    if (repoMatch && req.method === 'GET') {
      const repo = getRepo(db, Number(repoMatch[1]));
      if (!repo) throw new ServiceError('not_found', 404, 'Repository not found');
      return sendJson(res, 200, { ok: true, data: { repo: { ...repo, hasReadme: Boolean(repo.readme) } } });
    }
    if (repoMatch && req.method === 'DELETE') {
      return sendJson(res, 200, { ok: true, data: deleteRepo(db, Number(repoMatch[1])) });
    }

    const annotationMatch = p.match(/^\/api\/repos\/(\d+)\/annotation$/);
    if (annotationMatch && req.method === 'PATCH') {
      const body = await readJson(req, JSON_LIMIT);
      const annotation = updateAnnotation(db, Number(annotationMatch[1]), body);
      return sendJson(res, 200, { ok: true, data: { annotation } });
    }

    const refreshMatch = p.match(/^\/api\/repos\/(\d+)\/refresh$/);
    if (refreshMatch && req.method === 'POST') {
      const result = await refreshRepo(db, gh, Number(refreshMatch[1]));
      return sendJson(res, 200, { ok: true, data: result });
    }

    if (req.method === 'GET' && p === '/api/filters') {
      return sendJson(res, 200, { ok: true, data: listFilterOptions(db) });
    }

    if (req.method === 'POST' && p === '/api/jobs/import-stars') {
      const body = await readJson(req, JSON_LIMIT);
      const username = typeof body.username === 'string' ? body.username.trim() : '';
      if (!/^[a-z0-9](?:[a-z0-9-]{0,38})$/i.test(username)) {
        throw new ServiceError('invalid_field', 400, 'A valid GitHub username is required');
      }
      const job = jobs.startStarsImport({ username, includeReadme: body.includeReadme !== false });
      return sendJson(res, 202, { ok: true, data: { job } });
    }

    if (req.method === 'POST' && p === '/api/jobs/refresh-all') {
      const job = jobs.startRefreshAll();
      return sendJson(res, 202, { ok: true, data: { job } });
    }

    if (req.method === 'GET' && p === '/api/jobs') {
      return sendJson(res, 200, { ok: true, data: { jobs: jobs.listJobs() } });
    }

    const jobMatch = p.match(/^\/api\/jobs\/([0-9a-f-]+)$/i);
    if (jobMatch && req.method === 'GET') {
      const job = jobs.getJob(jobMatch[1]);
      if (!job) throw new ServiceError('not_found', 404, 'Job not found');
      return sendJson(res, 200, { ok: true, data: { job } });
    }
    const jobAction = p.match(/^\/api\/jobs\/([0-9a-f-]+)\/(cancel|resume)$/i);
    if (jobAction && req.method === 'POST') {
      if (jobAction[2] === 'cancel') {
        const stopped = jobs.cancelJob(jobAction[1]);
        return sendJson(res, 200, { ok: true, data: { cancelled: stopped } });
      }
      const job = jobs.resumeJob(jobAction[1]);
      if (!job) throw new ServiceError('not_found', 404, 'Job not found');
      return sendJson(res, 200, { ok: true, data: { job } });
    }

    if (req.method === 'GET' && p === '/api/export') {
      const data = exportData(db);
      const stamp = new Date().toISOString().slice(0, 10).replaceAll('-', '');
      return sendJson(res, 200, data, {
        'content-disposition': `attachment; filename="repo-shelf-export-${stamp}.json"`,
      });
    }

    if (req.method === 'POST' && p === '/api/restore') {
      const mode = url.searchParams.get('mode') === 'overwrite' ? 'overwrite' : 'merge';
      const payload = await readJson(req, RESTORE_LIMIT);
      const counts = importData(db, payload, { mode });
      return sendJson(res, 200, { ok: true, data: counts });
    }

    if (req.method === 'GET' && p === '/api/settings') {
      return sendJson(res, 200, {
        ok: true,
        data: {
          version: APP_VERSION,
          port: config.port,
          dataDir: config.dataDir,
          pairingToken,
          githubTokenSet: Boolean(getSetting(db, 'github_token')),
          statuses: STATUSES,
        },
      });
    }

    if (req.method === 'PUT' && p === '/api/settings/github-token') {
      const body = await readJson(req, JSON_LIMIT);
      if (typeof body.token !== 'string' || !body.token.trim()) {
        throw new ServiceError('invalid_field', 400, 'token must be a non-empty string');
      }
      setSetting(db, 'github_token', body.token.trim());
      return sendJson(res, 200, { ok: true, data: { githubTokenSet: true } });
    }
    if (req.method === 'DELETE' && p === '/api/settings/github-token') {
      setSetting(db, 'github_token', null);
      return sendJson(res, 200, { ok: true, data: { githubTokenSet: false } });
    }

    return sendError(res, 404, 'not_found', 'Unknown API endpoint');
  }

  function serveStatic(req, res, pathname) {
    if (req.method !== 'GET' && req.method !== 'HEAD') {
      return sendError(res, 405, 'method_not_allowed', 'Method not allowed');
    }
    const csp = [
      "default-src 'self'",
      "script-src 'self'",
      "style-src 'self' 'unsafe-inline'",
      "img-src 'self' data: https:",
      "connect-src 'self'",
      "object-src 'none'",
      "base-uri 'none'",
    ].join('; ');

    if (pathname === '/' || pathname === '/index.html') {
      const html = fs.readFileSync(path.join(publicDir, 'index.html'), 'utf8')
        .split('__RS_TOKEN__').join(pairingToken);
      res.writeHead(200, { 'content-type': MIME['.html'], 'cache-control': 'no-store', 'content-security-policy': csp });
      res.end(req.method === 'HEAD' ? undefined : html);
      return;
    }
    if (Object.hasOwn(vendorFiles, pathname)) {
      const file = vendorFiles[pathname];
      res.writeHead(200, { 'content-type': MIME[path.extname(file)] || 'application/octet-stream', 'cache-control': 'no-cache' });
      res.end(req.method === 'HEAD' ? undefined : fs.readFileSync(file));
      return;
    }
    const resolved = path.normalize(path.join(publicDir, pathname));
    if (!resolved.startsWith(publicDir) || !fs.existsSync(resolved) || !fs.statSync(resolved).isFile()) {
      return sendError(res, 404, 'not_found', 'Not found');
    }
    res.writeHead(200, { 'content-type': MIME[path.extname(resolved)] || 'application/octet-stream', 'cache-control': 'no-cache' });
    res.end(req.method === 'HEAD' ? undefined : fs.readFileSync(resolved));
  }

  const server = http.createServer((req, res) => {
    (async () => {
      const url = new URL(req.url, 'http://localhost');
      if (req.method === 'OPTIONS') return handlePreflight(req, res);
      if (url.pathname.startsWith('/api/')) {
        const auth = authorize(req, url.pathname);
        if (!auth.ok) return sendError(res, auth.status, auth.code, auth.message);
        if (auth.extensionOrigin) res.setHeader('access-control-allow-origin', auth.extensionOrigin);
        return await routeApi(req, res, url);
      }
      return serveStatic(req, res, url.pathname);
    })().catch((err) => {
      if (res.headersSent) {
        res.end();
        return;
      }
      if (err instanceof ServiceError) {
        sendError(res, err.httpStatus, err.code, err.message, {
          ...(err.retryable ? { retryable: true } : {}),
          ...(err.retryAfterMs !== null && err.retryAfterMs !== undefined ? { retryAfterMs: err.retryAfterMs } : {}),
        });
        return;
      }
      if (err instanceof BackupError) {
        sendError(res, 400, err.code, err.message);
        return;
      }
      console.error('Unhandled error:', err);
      sendError(res, 500, 'internal_error', 'Internal server error');
    });
  });

  return server;
}
