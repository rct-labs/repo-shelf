# Brief — discover: project-driven repository discovery

> Created 2026-09-28. Raw intent; refined into spec.md when testable.

## Ask

"I hope to automatically find outstanding AI-related projects on GitHub, and
ideally ones that help our existing projects, for example augur, augurNext,
navo, alltom." Follow-up: the goal changed from manual collection to automatic
discovery for current projects, so the UI should lean that way. Owner chose
plan A: the home view becomes a per-project recommendation feed, the existing
search library moves to a second tab.

## Goal

Each week the desktop app proposes at most 5 repositories per registered
project, each with the project need it addresses, an integration cost and a
short reason. The owner triages them by keyboard; accepted ones land in the
library labelled with that project; dismissed ones never come back for it.

## Non-goals

- Node reference implementation of discovery (desktop-only, like AI summaries).
- Trending-page scraping, social sources (HN/Reddit), release monitoring.
- Reading project source code or any file outside the whitelist.
- Automatic project detection; semantic embeddings.
- Changing the existing manual library behaviour.

## Constraints

- GitHub quota is precious (commit 2fb64bf): hard per-run budget, no
  implicit refresh, reuse existing rate-limit handling.
- Privacy: project files leave the machine only on an explicit per-project
  click, and only whitelisted files, truncated.
- AI output never overwrites personal fields (existing invariant).
- Shared `public/` UI must keep working against the Node server.
- Workers: Claude or Codex only.

## Unknowns & assumptions

- Assumed budget: 4 search queries/project/run, 30 results/query, 10 README
  fetches/project/run, weekly cadence. Owner may tune (CONTEXT §5).
- Assumed whitelist in REQUIREMENTS §6. Owner should review (CONTEXT §5).
- Assumed DeepSeek (`deepseek-chat`) is good enough for scoring; model stays
  overridable via `REPO_SHELF_DEEPSEEK_MODEL`.
- "AI-related" is not a hard filter: need statements decide relevance.
