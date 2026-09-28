# Spec — discover: project-driven repository discovery

revision: 1
<!-- Edit THIS file on change and bump revision. Never create spec-v2. -->

Product behaviour is REQUIREMENTS.md section 6 and acceptance criteria 11–16.
This spec adds the technical contract that lets tasks be built independently.

## Behaviour

### Data (Store.cs, additive `CREATE TABLE IF NOT EXISTS` only)

- `projects(id INTEGER PK, name TEXT UNIQUE NOT NULL, path TEXT NOT NULL,
  paused INTEGER NOT NULL DEFAULT 0, needs TEXT NOT NULL DEFAULT '[]',
  queries TEXT NOT NULL DEFAULT '[]', languages TEXT NOT NULL DEFAULT '[]',
  dependencies TEXT NOT NULL DEFAULT '[]', profile_files TEXT NOT NULL DEFAULT '[]',
  profiled_at TEXT, created_at TEXT NOT NULL, updated_at TEXT NOT NULL)`.
  `needs` = JSON array of strings (owner-editable); `queries` = JSON array of
  GitHub search keyword strings (at most 4, owner-editable).
- `discovery_candidates(project_id INTEGER REFERENCES projects(id) ON DELETE CASCADE,
  github_id INTEGER NOT NULL, full_name TEXT, html_url TEXT, description TEXT,
  language TEXT, license_id TEXT, stars INTEGER, stars_per_month REAL,
  pushed_at TEXT, created_at_upstream TEXT, score INTEGER, matched_need TEXT,
  cost TEXT, reason TEXT, reason_lang TEXT, model TEXT,
  state TEXT NOT NULL DEFAULT 'pending', dismiss_reason TEXT,
  run_id TEXT, proposed_at TEXT NOT NULL, decided_at TEXT,
  PRIMARY KEY (project_id, github_id))`.
  `state` ∈ pending | later | accepted | dismissed; `dismiss_reason` ∈
  not_relevant | too_heavy | already_have | low_quality | null;
  `cost` ∈ low | medium | high.
- `discovery_runs(id TEXT PK, status TEXT, trigger TEXT, started_at, finished_at,
  progress TEXT JSON, error TEXT, new_candidates INTEGER DEFAULT 0)`.
  Last successful run time is read from this table for the weekly schedule.
- Nothing is added to `repos`, `annotations`, `search_fts` or `generated`.
  Rationale lives on the candidate row (a repo can be proposed to two projects).

### HTTP API (DiscoveryApi.cs; HttpApi.cs only delegates `/api/projects*`,
`/api/discovery*` and `/api/repos/{id}/recommendations`)

| Method | Path | Behaviour |
|---|---|---|
| GET | /api/settings | adds `features: { discover: true }` (Node lacks it → UI treats as false) |
| GET | /api/projects | list with needs, queries, paused, profiledAt, profileFiles, counts per state |
| POST | /api/projects | `{name, path}`; path must exist; no network |
| PUT | /api/projects/{id} | edit name, path, paused, needs, queries |
| DELETE | /api/projects/{id} | removes project + its candidates; library untouched |
| POST | /api/projects/{id}/profile | reads whitelist, calls DeepSeek, stores needs/queries/languages/deps/profile_files |
| POST | /api/discovery/runs | start manual run → run object; 409 if one is running |
| GET | /api/discovery/runs/latest | status/progress of latest run |
| POST | /api/discovery/runs/{id}/cancel | cancel |
| GET | /api/discovery/candidates?state=pending,later&project=ID | grouped-ready list ordered by score desc |
| POST | /api/discovery/candidates/{projectId}/{githubId}/accept | save via RepoService, add project label, status `to_investigate`; returns repo |
| POST | /api/discovery/candidates/{projectId}/{githubId}/later | state later |
| POST | /api/discovery/candidates/{projectId}/{githubId}/dismiss | `{reason?}` state dismissed |
| GET | /api/repos/{id}/recommendations | candidate rows (all projects) for that repo's github_id |

All routes keep existing auth/origin rules. Errors use existing `ServiceException` codes.

### Extension points (so each task owns its own files)

- `DiscoveryApi.cs` (DISC-1) holds the dispatcher and a static registry:
  `DiscoveryRoutes.Register(Func<ServiceHost, IDiscoveryRoutes> factory)` and
  `DiscoveryRoutes.RegisterStartHook(Action<ServiceHost> hook)`.
  `IDiscoveryRoutes.TryHandleAsync(HttpContext ctx, string method, string path)
  → Task<bool>`. Handlers are instantiated once per `ServiceHost`.
- Later backend tasks register from their own file with a
  `[System.Runtime.CompilerServices.ModuleInitializer]` method: DISC-2 in
  `ProjectProfiler.cs`, DISC-4 in `DiscoveryRunner.cs` (routes + scheduler start
  hook), DISC-5 in `DiscoveryTriage.cs`. They never edit `HttpApi.cs`,
  `ServiceHost.cs` or `DiscoveryApi.cs`.
- `ServiceHost` (DISC-1) gets `T Feature<T>(Func<ServiceHost, T> create)` — a
  per-host singleton bag — and runs registered start hooks after the web app
  starts, so the runner/scheduler lives without editing `ServiceHost` again.
- UI: DISC-7 owns `i18n.js` and `styles.css` and adds
  `extendStrings(lang, dict)` to `i18n.js`; DISC-8 keeps its strings inside
  `projects.js` via `extendStrings` and its styles in `public/projects.css`,
  which DISC-7 links from `index.html`. DISC-7 ships a `projects.js` stub
  exporting `mount(el)`; DISC-8 replaces it.

### Profile generation (ProjectProfiler.cs)

- Whitelist exactly as REQUIREMENTS §6; each file truncated to 8 000 chars,
  total prompt input ≤ 30 000 chars. `TASK_QUEUE.md`: only table rows whose
  status cell is not `DONE`. Manifests: dependency names only.
- Never opens a path matching `.env*`, `memory/**`, `data/**`, `config/**`,
  `.git/**`, `node_modules/**`; does not recurse beyond project root except
  `app/*/*.csproj`, `src/*/*.csproj` one level for csproj discovery.
- DeepSeek returns JSON `{needs: string[5..12], queries: string[1..4],
  languages: string[], dependencies: string[]}` in the UI language;
  `DeepSeekClient.ChatJsonAsync(system, user, cancel)` is the generic call
  (uses `response_format: json_object`); invalid JSON → `ai_invalid_response` 502.

### Recall (DiscoveryRecall.cs + GitHubClient.SearchReposAsync)

- `GET /search/repositories?q=<query> language:<lang> pushed:>=<today-180d> archived:false fork:false&sort=stars&order=desc&per_page=30`.
  Language qualifier: the project's first language when present.
- Budget constants (single place, overridable in tests): 4 queries/project,
  30 results/query, 10 shortlist/project.
- Drop: already in `repos` (by github_id), any candidate row for this project
  (any state — pending ones are not re-proposed, dismissed never), no license,
  pushed > 180 days. Rank by `stars_per_month` = stars / max(1, months since
  creation); shortlist top 10. Pure ranking function is unit-tested.
- Rate limit: `GitHubException` `rate_limited` stops the run with the reset
  time; partial candidates already scored are kept.

### Scoring (DiscoveryScorer.cs)

- One DeepSeek call per shortlisted repo: input = project needs, last 10
  accepted and last 10 dismissed (with reason) for that project, repo
  metadata and README truncated to 6 000 chars inside a fenced block labelled
  untrusted data. Output JSON `{score:int 0-100, matchedNeed:string,
  cost:"low"|"medium"|"high", reason:string}`; `matchedNeed` must be one of the
  needs verbatim, else item rejected. Invalid JSON rejects that item only.
- Keep score ≥ 60, top 5 per project per run, insert as `pending`.

### Runner (DiscoveryRunner.cs)

- Background run like JobManager jobs: progress persisted after each project
  and each scored item; cancellable between items; one run at a time.
- Scheduler: on service start and hourly, if discovery is available (DeepSeek
  key set and ≥1 active profiled project) and last `done` run is older than
  7 days → start `trigger: schedule`. Clock injectable for tests.
- Exposes `event Action<int> NewCandidates` raised when a run inserts > 0.

### UI

- `public/discover.js` (feed) and `public/projects.js` (projects tab) are ES
  modules imported by `app.js`. Tabs: Recommendations / Library / Projects;
  the Recommendations and Projects tabs exist only when
  `settings.features.discover` is true; default tab Recommendations then.
- Feed: project filter chips with pending counts, cards as REQUIREMENTS §6,
  keys `j`/`k`/`a`/`s`/`d` (inactive while typing in an input); dismiss opens
  a 4-option reason menu (Enter = not relevant). "Later" list toggle.
  Run status line with "Run now" and last run time.
- Accepted → toast with "Open" linking to library detail; library detail
  shows a "Recommended for <project>" AI-badged block from
  `/api/repos/{id}/recommendations`.
- Projects tab: add (name + path), paused toggle, "Generate profile" button
  with a privacy note listing files that will be read, editable needs and
  queries (one per line), delete with confirm.
- Window: new host message `mode: "feed"` → 900×640 in `MainWindow.xaml.cs`.
- Tray: one balloon "N new recommendations" on `NewCandidates`; click opens window.
- All strings in `i18n.js` for en and zh-CN.

### Backup

- `BackupVersion` = 2; export adds `projects` and `discoveryCandidates`
  (all states). Restore accepts v1 (no discovery data) and v2.

## Acceptance

- `node scripts/verify.mjs` green (all .NET + Node tests), plus new tests:
  - `DiscoveryStoreTests` — schema, project CRUD, no network on add (acc. 11a).
  - `ProjectProfilerTests` — fixture dir with `.env`, `memory/x.md`,
    `data/y.db`, `config/z.yaml`, `src/a.py` → none read; `profile_files`
    lists exactly the whitelisted files present; non-DONE queue rows only (acc. 11).
  - `DiscoveryRecallTests` — FakeGitHubServer counts ≤4 search requests per
    project, 0 requests for saved/dismissed repos, README only for shortlist,
    rate-limit stop with reset time (acc. 12, 13c).
  - `DiscoveryRunTests` — fake AI: ≤5 per project, ≥60 only, one malformed
    reply does not fail the run, schedule honours 7 days with injected clock (acc. 13).
  - `DiscoveryTriageTests` — accept creates one repo with label/status and
    empty reason/notes; dismissed excluded next run; pending never in `/api/repos`
    search (acc. 14).
  - `BackupTests` — v2 round-trip incl. discovery; v1 file still restores (acc. 15).
- `node tests/e2e/smoke.mjs` passes (Node backend, discovery hidden) (acc. 16b).
- `node tests/e2e/discover.mjs` passes: desktop service `--service` with fake
  GitHub + fake DeepSeek, add project, generate profile, run, keyboard triage
  a/s/d, accepted repo visible in Library tab (acc. 16a).

## Checkpoints and impact

- Stage: development. Module checkpoint after DISC-5 (backend complete:
  `node scripts/verify.mjs --dotnet-only`). Integration after DISC-8.
  Delivery after DISC-9: full `node scripts/verify.mjs` + both e2e scripts.
- Impact: backend tasks are `shared` where they touch `HttpApi.cs`,
  `ServiceHost.cs`, `Store.cs`, `GitHubClient.cs`, `Backup.cs` (callers: all
  HTTP routes, jobs, import); new files are `local`. UI tasks are `shared`
  (`app.js`, `index.html` affect every screen) → smoke test in task check.
- Budget: about 1.5 working days of worker time; ordinary defects are grouped
  at the module and delivery checkpoints, one repair batch.

## Decisions

- Candidates in a separate table, not a new library status — keeps library
  search, counts, filters and exports pure; gives per-project dismissal memory.
  (Supersedes the earlier chat suggestion of a "Candidate" status.)
- Discovery is desktop/C# only; Node reference exposes no `features.discover`.
- Search queries come from the stored profile (AI once, owner-editable), so
  recall itself is deterministic and costs no AI calls.
- Rationale stored on the candidate row, not in `generated` (PK repo_id+kind
  would collide across projects).
- Stars-per-month since creation as the growth proxy: no history snapshots needed.
