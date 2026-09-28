# TASK_QUEUE

> The ONLY task list (gate format). gate takes the first non-DONE row.
> Work package and acceptance: `docs/work/discover/spec.md` (+ REQUIREMENTS.md §6, acceptance 11–16).
> Worker pins: codex = mechanical, test-shaped C#; claude = AI prompt/JSON contracts, UI, e2e, docs.

## Discover — project-driven recommendations (`docs/work/discover/`)

| # | ID | name | status | start baseline | end baseline | worker |
|---|---|---|---|---|---|---|
| 1 | DISC-1 | Discovery data + projects API — tables, DiscoveryStore, projects CRUD, features flag, route registry + host feature bag (spec Data/HTTP/Extension points) | `DONE` | 133 passed | 104 passed (.NET local) | codex |
| 2 | DISC-2 | Project profile — whitelist reader, DeepSeek ChatJsonAsync, POST /api/projects/{id}/profile (spec Profile; acc. 11) | `DONE` | 104 passed (.NET local) | 42 passed (gate filter) | claude |
| 3 | DISC-3 | Recall — GitHub search client, budgeted recall, exclusion, stars-per-month shortlist, rate-limit stop (spec Recall; acc. 12) | `TODO` | | | codex |
| 4 | DISC-4 | Scoring + runner — DeepSeek scorer, background run, weekly scheduler, run/candidates API, NewCandidates event (spec Scoring/Runner; acc. 13) | `TODO` | | | claude |
| 5 | DISC-5 | Triage — accept/later/dismiss endpoints, accept via save path, per-repo recommendations endpoint (spec HTTP; acc. 14) | `TODO` | | | codex |
| 6 | DISC-6 | Backup v2 — export/restore projects and candidates, v1 compatibility (spec Backup; acc. 15) | `TODO` | | | codex |
| 7 | DISC-7 | Feed UI — tabs, recommendations feed, keyboard triage, feed window size, projects.js stub + extendStrings (spec UI/Extension points; acc. 16b) | `TODO` | | | claude |
| 8 | DISC-8 | Projects UI + tray — projects tab, profile privacy note, editable needs, tray notification (spec UI) | `TODO` | | | claude |
| 9 | DISC-9 | Delivery — discover e2e with fake GitHub/DeepSeek on desktop service, README/ARCHITECTURE/ACCEPTANCE (acc. 16a) | `TODO` | | | claude |

<!-- task:DISC-1 files: app/RepoShelf.Core/Store.cs, app/RepoShelf.Core/DiscoveryStore.cs, app/RepoShelf.Core/DiscoveryApi.cs, app/RepoShelf.Core/HttpApi.cs, app/RepoShelf.Core/ServiceHost.cs, test/RepoShelf.Tests/DiscoveryStoreTests.cs -->
<!-- task:DISC-1 spec: docs/work/discover/spec.md -->
<!-- task:DISC-1 impact: shared -->
<!-- task:DISC-1 verify: {"cmd": "node scripts/verify.mjs --dotnet-only", "timeout_s": 600} -->

<!-- task:DISC-2 files: app/RepoShelf.Core/ProjectProfiler.cs, app/RepoShelf.Core/DeepSeekClient.cs, test/RepoShelf.Tests/ProjectProfilerTests.cs -->
<!-- task:DISC-2 spec: docs/work/discover/spec.md -->
<!-- task:DISC-2 impact: local -->
<!-- task:DISC-2 verify: {"cmd": "node scripts/verify.mjs --dotnet-only --dotnet-filter \"FullyQualifiedName~Discovery|FullyQualifiedName~ProjectProfiler|FullyQualifiedName~AiAndBookmarks\"", "timeout_s": 600} -->

<!-- task:DISC-3 files: app/RepoShelf.Core/GitHubClient.cs, app/RepoShelf.Core/DiscoveryRecall.cs, test/RepoShelf.Tests/Fakes.cs, test/RepoShelf.Tests/DiscoveryRecallTests.cs -->
<!-- task:DISC-3 spec: docs/work/discover/spec.md -->
<!-- task:DISC-3 impact: shared -->
<!-- task:DISC-3 verify: {"cmd": "node scripts/verify.mjs --dotnet-only", "timeout_s": 600} -->

<!-- task:DISC-4 files: app/RepoShelf.Core/DiscoveryScorer.cs, app/RepoShelf.Core/DiscoveryRunner.cs, test/RepoShelf.Tests/DiscoveryRunTests.cs -->
<!-- task:DISC-4 spec: docs/work/discover/spec.md -->
<!-- task:DISC-4 impact: shared -->
<!-- task:DISC-4 verify: {"cmd": "node scripts/verify.mjs --dotnet-only", "timeout_s": 600} -->

<!-- task:DISC-5 files: app/RepoShelf.Core/DiscoveryTriage.cs, test/RepoShelf.Tests/DiscoveryTriageTests.cs -->
<!-- task:DISC-5 spec: docs/work/discover/spec.md -->
<!-- task:DISC-5 impact: local -->
<!-- task:DISC-5 verify: {"cmd": "node scripts/verify.mjs --dotnet-only", "timeout_s": 600} -->

<!-- task:DISC-6 files: app/RepoShelf.Core/Backup.cs, test/RepoShelf.Tests/BackupTests.cs -->
<!-- task:DISC-6 spec: docs/work/discover/spec.md -->
<!-- task:DISC-6 impact: shared -->
<!-- task:DISC-6 verify: {"cmd": "node scripts/verify.mjs --dotnet-only --dotnet-filter \"FullyQualifiedName~Backup\"", "timeout_s": 600} -->

<!-- task:DISC-7 files: public/index.html, public/app.js, public/discover.js, public/styles.css, public/i18n.js, app/RepoShelf/MainWindow.xaml.cs -->
<!-- task:DISC-7 spec: docs/work/discover/spec.md -->
<!-- task:DISC-7 impact: shared -->
<!-- task:DISC-7 verify: {"cmd": "node tests/e2e/smoke.mjs && node scripts/verify.mjs", "timeout_s": 900} -->

<!-- task:DISC-8 files: public/projects.js, public/projects.css, app/RepoShelf/TrayIcon.cs, app/RepoShelf/App.xaml.cs -->
<!-- task:DISC-8 spec: docs/work/discover/spec.md -->
<!-- task:DISC-8 impact: shared -->
<!-- task:DISC-8 verify: {"cmd": "node tests/e2e/smoke.mjs && node scripts/verify.mjs", "timeout_s": 900} -->

<!-- task:DISC-9 files: tests/e2e/discover.mjs, package.json, README.md, ARCHITECTURE.md, ACCEPTANCE.md -->
<!-- task:DISC-9 spec: docs/work/discover/spec.md -->
<!-- task:DISC-9 impact: shared -->
<!-- task:DISC-9 verify: {"cmd": "node tests/e2e/discover.mjs && node tests/e2e/smoke.mjs && node scripts/verify.mjs", "timeout_s": 1200} -->
