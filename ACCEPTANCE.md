# Acceptance evidence

Measured on this machine (Windows 11, AMD Ryzen 9 9950X, 32 GB RAM,
Node v24.19.0, better-sqlite3 / SQLite 3.53.2) on 2026-09-20. All commands are
rerunnable; test fixtures are synthetic or public (`octocat/Hello-World`,
`octocat/Spoon-Knife`) — no real user library or tokens appear anywhere.

## 1. Save via app and extension; single record, preserved annotations

- Integration: `tests/auth.test.js` → *"capture through the HTTP API"* posts
  the same repository twice (second time via a `/pulls` subpage URL) and gets
  one record with the same id.
- Browser: `tests/e2e/smoke.mjs` captures `octocat/Hello-World` through the
  extension popup with reason `E2E smoke capture reason 冒烟`:
  `PASS capture stored exactly once`, `PASS reason preserved`.

## 2. Import with pagination, mid-import failure, repeat-safe retry

`tests/import.test.js` (all against a scripted fake GitHub):

- *imports multiple pages of stars…*: 205 repos over 3 pages → added 205,
  READMEs snapshotted, immediate re-import adds 0 / updates 205.
- *mid-import failure…*: page 2 fails 4× with simulated network errors → job
  fails with `next_page=2`, `processed=100`; resume finishes with 150 records,
  no duplicates.
- *rate limit information aborts the job…*: 403 with `X-RateLimit-Remaining: 0`
  stops the job with the reset time in the message instead of retrying.

## 3. Re-import/refresh preserves personal fields; unstar/archive/rename safe

- `tests/import.test.js` → *re-import preserves annotations; upstream unstar
  keeps the local record*: notes/status survive a re-import; the unstarred
  repo stays with `starredUpstream = false`.
- `tests/identity.test.js` → *rename keeps identity…*, *refresh follows the
  durable id…*, *deleted upstream repository is kept and marked not_found*,
  *refresh updates source fields but never personal fields*.

## 4. Search: Chinese notes, English README, exact names, combined filters

`tests/search.test.js` verifies the tokenizer choice (CJK bigrams) end to end:
Chinese queries match notes/reason (`状态管理`, `增量同步`, `研究`), English
queries match README bodies, exact names rank first, AND semantics hold, and
filters (status/tag/language/archived/project) combine with keywords. Results
carry matched-field names and `<mark>`-highlighted, HTML-escaped snippets.
Browser: `PASS Chinese note search matched` (`冒烟` query in the UI).

## 5. Offline operation

Search and reading are pure SQLite (`server/search.js`, no network in the
request path); `tests/search.test.js` and all retrieval tests run with a fake
`fetch` that never touches the network. README snapshots are stored at save/
import time, so previously saved records keep working with the network down.

## 6. Export / restore without credentials

`tests/export.test.js` → *export → wipe → restore round-trips all durable
fields without credentials*: sets a GitHub token and pairing token, exports,
asserts both secrets are absent from the JSON, restores into an empty
database and compares every durable source field, annotation field and README
snapshot; search works on restored data. Merge/overwrite modes covered.
HTTP level: `tests/auth.test.js` → *export endpoint excludes credentials…*.

## 7. Extension offline capture → pending → retry saves exactly one record

`tests/e2e/smoke.mjs`: the server is killed, a capture is attempted from the
popup (`PASS offline capture queued as pending`), the server restarts, the
popup's explicit retry is clicked → `PASS retry saved exactly one record
after recovery`. The queue is bounded (50) and exposes discard.

## 8. Performance: 5,000 repos, warm search

`pnpm perf` (tests/perf/run-perf.js): synthetic dataset of 5,000 repos with
mixed Chinese/English annotations and ~8 KB READMEs; `perf_hooks` timing
around `searchRepos()`, warm index, 30 iterations per query shape.

```
query                    mean(ms)   p95(ms)   max(ms)
exact-name                  0.3       0.4       1.1
english-readme-term         6.6       6.6      16.9
chinese-notes-term          1.9       2.6       3.0
chinese-phrase              2.0       2.6       2.7
mixed-query                 2.5       2.8       3.4
owner                       0.7       1.5       1.7
rare-term                   9.4      10.3      12.7
no-results                  0.1       0.2       0.3
filter-status               3.1       4.2       4.2
filter-language             3.2       3.4       4.3
filter-tag-archived         1.6       2.0       2.2
empty-query-listing        55.7      61.5      61.9
overall p95: 54.7 ms (target < 500 ms) — MET
```

The slowest shape is the unfiltered 50-row listing (payload building, not FTS);
all keyword searches are ≤ 13 ms p95.

## 9. Unauthorized local API access rejected; hostile READMEs inert

- `tests/auth.test.js`: no token → 401; wrong token → 401; foreign website
  origin → 403; spoofed Host → 403; extension origin + token → 200 with CORS;
  preflight answered only for extension origins.
- `tests/e2e/smoke.mjs` seeds a README containing `<script>`, `onerror=`,
  `javascript:` links and an `<iframe>`: `PASS hostile README scripts did not
  execute`, `PASS no <script>/<iframe>/javascript:/onerror survived`.

## 10. Fresh-checkout setup + automated tests + browser smoke

- Setup: `pnpm install && pnpm start` (README.md), no Docker, no AI account.
- `pnpm test`: **56 tests, 56 pass** (unit + integration; hermetic).
- `pnpm test:e2e`: real browser smoke — 18 checks, all pass. Honest
  limitations: it launches a **visible** Playwright Chromium window (extensions
  need a full build, not headless shell); it uses the real public GitHub API
  so it needs network; branded Chrome/Edge ≥ 137 ignore `--load-extension`
  (Google removed the flag), so the automated run uses Chromium while real
  Chrome/Edge install via developer mode remains fully supported.

## Command reference

```
pnpm install      # dependencies
pnpm start        # serve http://127.0.0.1:4790
pnpm test         # 56 unit/integration tests
pnpm test:e2e     # browser smoke (capture, pending queue, search, sanitization)
pnpm perf         # 5,000-repo latency measurement
```
