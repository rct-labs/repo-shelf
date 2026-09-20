# Repo Shelf architecture

## Shape

```
Chromium extension (MV3)                Browser tab
  popup / options        ──────────►   ┌────────────────────────────┐
  (capture, pending queue)  HTTPS? no ─┤ Local service (Node http)  │
                                       │  loopback 127.0.0.1:4790   │
                 Web UI (public/) ◄─── │  static files + JSON API   │
                                       │  better-sqlite3 (FTS5)     │
                                       └──────────┬─────────────────┘
                                                  │ read-only REST
                                           api.github.com (optional token)
```

Single process, single user, no build step. The web UI, the extension and the
local service ship in one repository; the database lives outside it.

## Stack choice

- **Node.js (ESM, plain JavaScript)**: the extension is JavaScript anyway;
  skipping TypeScript removes the whole compile/tooling layer on a fresh
  Windows checkout. Types are documented with JSDoc where they matter.
- **better-sqlite3**: embedded database with FTS5 built in; ships prebuilt
  Windows binaries, synchronous API keeps the service code small.
- **marked + DOMPurify** served from `node_modules` (`/vendor/*` routes) so
  the UI works offline with no CDN and no bundler.
- **node:test** for unit/integration tests, **Playwright** for the real
  browser smoke test. No other runtime dependencies.

## Identity and storage

- The **GitHub repository id** is the durable identity (`repos.github_id`,
  unique). Renames/transfers resolve through `GET /repositories/{id}` when the
  owner/name path 404s, so a rename never creates a duplicate.
- Source metadata (`repos`) and personal knowledge (`annotations`) are
  separate tables with a 1:1 relation. Every refresh path writes only to
  `repos`, which makes "refresh never overwrites personal fields" structural
  rather than incidental.
- README snapshots are stored in `repos.readme` (capped at 1 MB) so search and
  reading work offline.
- `refresh_status` ∈ `ok | not_found | inaccessible | error` plus `fetched_at`
  distinguish missing, inaccessible, failed and fresh records; the UI adds a
  derived "stale" badge when `fetched_at` is older than 7 days.
- `starred_upstream` tracks Stars membership. Completed imports reconcile it
  (`job_seen` table): unstarred repos are flagged, never deleted.

## Search

- `server/tokenize.js` segments text into Latin word tokens and CJK bigrams
  (single CJK chars stay unigrams). The same tokenizer runs at index time and
  query time; tokens are space-joined into `search_fts` (FTS5, unicode61 sees
  our tokens as terms). This is verified by `tests/search.test.js` and avoids
  assuming an English tokenizer handles Chinese.
- Queries AND all tokens (a Chinese phrase = all its bigrams). Ranking uses
  `bm25` with column weights: name 10, owner 4, description 3, topics 5,
  reason 8, notes 8, README 1 — exact-name matches get an extra boost.
- Highlights are generated server-side: snippets are escaped first, then
  matched tokens are wrapped in `<mark>` (tokens contain no HTML-significant
  characters). Responses include the matched field names, not opaque scores.
- Filters (status, tag, language, archived, related project) combine with
  keyword search in the same SQL query.

## GitHub access

`server/github.js` is a small read-only REST client:

- Transient network errors and 5xx are retried with bounded exponential
  backoff (3 retries), then a retryable error is surfaced.
- 403/429 responses are classified as `rate_limited` when
  `X-RateLimit-Remaining: 0` or `Retry-After` says so; the error carries the
  server's reset/wait time. Import jobs wait only when the reported wait is
  under a bounded threshold (default 90 s), otherwise the job stops with the
  reset time in its message. The API is never hammered.
- An optional personal access token (Settings page) raises the quota; it is
  stored in the local `settings` table only and never exported or logged.

## Jobs

Stars import and refresh-all run in-process with progress persisted in
`import_jobs` after every page/batch. A failed or cancelled import resumes
from its last committed page (`POST /api/jobs/:id/resume`); upserts are keyed
by GitHub id, so retries are repeat-safe. Cancellation is checked between
items.

## HTTP security

- Loopback bind only (`127.0.0.1`); no external exposure by default.
- Pairing token generated on first run, stored in the data dir, required in
  `X-RepoShelf-Token` for all `/api/*` except a minimal `/api/health`.
- The web UI gets the token via a `<meta>` tag in the served HTML (SOP keeps
  it unreadable to other origins); the extension stores it in
  `chrome.storage.local` after manual pairing.
- Origin allowlist: the app's own loopback origin and browser-extension
  schemes. Host header must be the loopback listener. CORS answers preflights
  only for extension origins.
- Remaining local trust boundary (by design for a loopback app): any process
  on the machine can read `/` and therefore the token. This protects against
  *websites*, not against malicious local software.

## Extension

Thin MV3 capture client: `storage` + `activeTab` + loopback host permissions.
The popup normalizes the current tab URL locally (a mirror of
`server/normalize.js`, deliberately not shared code to keep both
dependency-free), posts captures with the pairing token, and on connection
failure queues a bounded pending list (50 entries) with explicit
retry/discard. A background service worker only keeps the badge count in sync.
The manifest pins a `key` so the extension id is stable across reinstalls and
test runs.

## Testing strategy

- Unit/integration (`node --test`): URL normalization, tokenizer, FTS search
  (Chinese notes, English READMEs, exact-name ranking, filters, snippet
  escaping), identity (rename/transfer/unstar/delete-upstream), annotation
  preservation, Stars import (pagination, mid-import failure + resume, rate
  limits, unstar reconciliation), export/restore round-trip without
  credentials, HTTP auth boundaries (token, origin, host, preflight), GitHub
  client retry/rate-limit behavior. All hermetic via a fake `fetch`.
- Browser smoke (`tests/e2e/smoke.mjs`): real extension in Playwright
  Chromium, real local server, capture through the popup, offline pending +
  retry after recovery, Chinese search in the UI, hostile README fixture
  rendered inertly, language switch.
- Performance (`tests/perf/run-perf.js`): 5,000-repo synthetic dataset, warm
  search latency measured with `perf_hooks`.
