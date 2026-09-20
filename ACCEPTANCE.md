# Acceptance evidence

Measured on this machine (Windows 11, AMD Ryzen 9 9950X, 32 GB RAM,
.NET 10.0.11, Node v24.19.0) on 2026-09-20. All commands are rerunnable; test
fixtures are synthetic or public (`octocat/Hello-World`, `octocat/Spoon-Knife`)
— no real user library or tokens appear anywhere.

The desktop app (WPF/.NET) is the primary deliverable; the Node.js
implementation in `server/` is a fully tested reference with identical API and
behavior. Both were verified with the commands below.

## 1. Save via app and extension; single record, preserved annotations

- .NET: `test/RepoShelf.Tests/HttpAuthTests.CaptureAnnotateSearchOverHttp` —
  posts the same repository twice (second time via a `/pulls` subpage URL) and
  gets one record with the same id.
- Browser: `tests/e2e/smoke.mjs` captures `octocat/Hello-World` through the
  extension popup with reason `E2E smoke capture reason 冒烟`:
  `PASS capture stored exactly once`, `PASS reason preserved` — verified
  against **both** backends (`pnpm test:e2e` and `pnpm test:e2e:desktop`).

## 2. Import with pagination, mid-import failure, repeat-safe retry

`test/RepoShelf.Tests/ImportTests.cs` (against a fake GitHub Kestrel server):

- *ImportsMultiplePagesWithNoDuplicates*: 205 repos over 3 pages → added 205;
  immediate re-import adds 0 / updates 205; READMEs snapshotted.
- *MidImportFailurePreservesProgressAndResumeCompletes*: page 2 fails with
  transient 5xx storms → job fails with `nextPage=2`, `processed=100`; resume
  finishes with 150 records, no duplicates.
- *RateLimitAbortsJobCleanly*: 403 with `X-RateLimit-Remaining: 0` stops the
  job with the reset time in the message instead of retrying.

(Node equivalents: `tests/import.test.js`.)

## 3. Re-import/refresh preserves personal fields; unstar/archive/rename safe

- `ImportTests.ReimportPreservesAnnotationsAndUnstarKeepsRecord`: notes/status
  survive a re-import; the unstarred repo stays with `starredUpstream=false`.
- `IdentityTests.cs`: *RenameKeepsIdentityWithoutDuplicates*,
  *RefreshFollowsDurableIdWhenOldName404s*,
  *DeletedUpstreamIsKeptAndMarkedNotFound*,
  *RefreshUpdatesSourceButNeverPersonalFields*.

## 4. Search: Chinese notes, English README, exact names, combined filters

`test/RepoShelf.Tests/SearchTests.cs` verifies the tokenizer choice (CJK
bigrams) end to end: Chinese queries match notes/reason (`状态管理`,
`增量同步`), English queries match README bodies, exact names rank first, AND
semantics hold, filters (status/tag/language/archived/project) combine with
keywords, and snippets are HTML-escaped with `<mark>` highlights plus matched
field names. Browser: `PASS Chinese note search matched` (`冒烟` query).

## 5. Offline operation

Search and reading are pure SQLite; all retrieval tests run against in-memory
databases with no network. README snapshots are stored at save/import time, so
previously saved records keep working with the network down.

## 6. Export / restore without credentials

`BackupTests.ExportWipeRestoreRoundTripsWithoutCredentials`: sets a GitHub
token and pairing token, exports, asserts both secrets are absent, restores
into an empty database and compares source fields, annotation fields, README
snapshots and timestamps; search works on restored data. Merge/overwrite modes
covered. HTTP level: `HttpAuthTests.ExportExcludesCredentialsAndRestoreRoundTrips`.

## 7. Extension offline capture → pending → retry saves exactly one record

`tests/e2e/smoke.mjs` (both backends): the service is killed, a capture is
attempted from the popup (`PASS offline capture queued as pending`), the
service restarts, the popup's explicit retry is clicked →
`PASS retry saved exactly one record after recovery`. The queue is bounded
(50) and exposes discard.

## 8. Performance: 5,000 repos, warm search

.NET (`dotnet run --project tools/RepoShelf.PerfCheck -c Release`), synthetic
dataset of 5,000 repos with mixed Chinese/English annotations and ~8 KB
READMEs; `Stopwatch` around `SearchService.Search()`, warm index, 30
iterations per query shape:

```
query                    mean(ms)   p95(ms)   max(ms)
exact-name                  0.6       0.7       1.7
english-readme-term        39.5      48.1      48.9
chinese-notes-term         12.0      14.7      15.2
chinese-phrase             12.0      15.7      16.2
mixed-query                13.1      16.1      17.1
owner                       1.0       1.5       1.9
rare-term                  40.0      42.1      43.5
no-results                 0.2        0.3       0.6
filter-status              32.6      38.9      42.0
filter-language            34.3      40.4      40.5
filter-tag-archived        13.0      14.1      16.7
empty-query-listing        73.5      92.1      92.6
overall p95: ~80 ms (target < 500 ms) — MET
```

The slowest shape is the unfiltered 50-row listing (payload building, not
FTS); keyword searches are ≤ 48 ms p95. The Node reference implementation
measures 54.7 ms overall p95 on the same dataset shape
(`pnpm perf`).

## 9. Unauthorized local API access rejected; hostile READMEs inert

- `HttpAuthTests.cs`: no token → 401; wrong token → 401; foreign website
  origin → 403; spoofed Host → 403; extension origin + token → 200 with CORS;
  preflight answered only for extension origins.
- `tests/e2e/smoke.mjs` seeds a README containing `<script>`, `onerror=`,
  `javascript:` links and an `<iframe>`: `PASS hostile README scripts did not
  execute`, `PASS no <script>/<iframe>/javascript:/onerror survived`.

## 10. Fresh-checkout setup + automated tests + browser smoke

- Desktop app: `dotnet publish app/RepoShelf -c Release`, double-click
  `RepoShelf.exe` (~4 MB framework-dependent; .NET 10 desktop runtime
  required — already present on this machine). Tray, launch-at-login and
  single instance verified manually: window renders the bilingual UI,
  `HKCU\...\Run\RepoShelf` registered, second launch focuses the running
  instance, port-in-use falls back to attaching to the healthy service.
- `dotnet test app/RepoShelf.slnx`: **69 tests, 69 pass** (hermetic).
- `pnpm test`: **56 tests, 56 pass** (Node reference, hermetic).
- `pnpm test:e2e` and `pnpm test:e2e:desktop`: 18 browser checks each, all
  pass. Honest limitations: they launch a **visible** Playwright Chromium
  window (extensions need a full build, not headless shell); they use the real
  public GitHub API so they need network; branded Chrome/Edge ≥ 137 ignore
  `--load-extension`, so automated runs use Chromium while real Chrome/Edge
  install via developer mode remains fully supported.

## Command reference

```powershell
dotnet build app/RepoShelf.slnx        # build all
dotnet test app/RepoShelf.slnx         # 69 .NET tests
dotnet publish app/RepoShelf -c Release  # desktop exe (~4 MB, FDD)
dotnet run --project tools/RepoShelf.PerfCheck -c Release   # perf

pnpm install && pnpm start             # Node reference web app
pnpm test                              # 56 Node tests
pnpm test:e2e / pnpm test:e2e:desktop  # browser smoke (Node / .NET backend)
pnpm perf                              # Node perf harness
```
