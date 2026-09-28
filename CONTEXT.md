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

- Work package `docs/work/discover/` (brief + spec rev 1) and queue DISC-1..9
  written; nothing implemented yet. Stage: development.
- Tree: REQUIREMENTS.md rev 2, new `scripts/verify.mjs`, `.gate/`, CONTEXT.md,
  TASK_QUEUE.md, docs/work/discover/ — to be committed before the run.

## 3. Last stable checkpoint

- 2fb64bf — 76 .NET + 57 Node tests green (`node scripts/verify.mjs` →
  `133 passed`); `node tests/e2e/smoke.mjs` → ALL CHECKS PASSED.

## 4. Next step

- Run the queue from DISC-1 (flow-run). Module checkpoint after DISC-5
  (`node scripts/verify.mjs --dotnet-only`); delivery after DISC-9 (full
  verify + `delivery.integration_cmd`).

## 5. Waiting on a human

- Review the privacy whitelist in REQUIREMENTS §6 (which project files may be
  sent to DeepSeek). Default applies until changed.
- Per-run GitHub budget (4 queries/project, 30 results/query, 10 READMEs/project,
  weekly). Default applies until changed.
