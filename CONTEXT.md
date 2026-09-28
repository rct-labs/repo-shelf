# CONTEXT — repo-shelf

> Rebuilt 2026-09-28. Keep under 200 lines. Rebuild, never append.

## 1. Goal & red lines

- Goal (revision 2, REQUIREMENTS.md §6): the desktop app proposes, weekly, at
  most 5 GitHub repositories per registered local project (e.g. Augur,
  AugurNext, Navo, alltom) that address that project's need statements; the
  owner triages by keyboard. Manual library keeps working unchanged.
- Owner chose plan A: home = per-project Recommendations tab; Library tab =
  existing search; Projects tab.
- Red lines:
  - GitHub quota: hard per-run budget; no implicit refresh (commit 2fb64bf).
  - Privacy: project files leave the machine only on an explicit per-project
    click, whitelist only (REQUIREMENTS §6).
  - AI output never overwrites reason/notes; candidates never enter library
    search/counts/filters (separate table).
  - Discovery is desktop/C# only; the Node reference and `tests/e2e/smoke.mjs`
    must keep passing with discovery hidden.
  - Workers: codex or claude only. Work on `main`.

## 2. Now

- Work package `docs/work/discover/` delivered: DISC-1..9 DONE in run
  20260928-060933 (queue_empty, ~85 min). Package review: pass (fable, 88),
  5 low findings in `REVIEW-NOTES.md` (backlog, none blocking).
- Delivery acceptance: `flow verify --stage delivery` PASS (238 tests);
  `node tests/e2e/smoke.mjs` PASS; `node tests/e2e/discover.mjs` PASS 5/5 after
  a host fix: projects tab re-rendered the add form during an in-flight load,
  so a click could land on a detached button (1 of 3 runs failed before).
- Delivery budget: started 2026-09-28; 12 h allowance; about 1.6 h used.

## 3. Last stable checkpoint

- HEAD after the projects-form race fix; verified by `node scripts/verify.mjs`
  (238 passed) and both e2e scripts.

## 4. Next step

- Owner trial on real projects: publish (`dotnet publish app/RepoShelf -c Release`),
  add Augur/AugurNext/Navo/alltom in the Projects tab, generate profiles,
  review need statements, "Run now". Then optionally batch the 5 low findings.

## 5. Waiting on a human

- Review the privacy whitelist in REQUIREMENTS §6 (which project files may be
  sent to DeepSeek). Default applies until changed.
- Per-run GitHub budget (4 queries/project, 30 results/query, 10 READMEs/project,
  weekly). Default applies until changed.
