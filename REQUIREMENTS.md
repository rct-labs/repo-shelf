# Repo Shelf

revision: 2
date: 2026-09-28
status: MVP implemented (manual library). Revision 2 adds project-driven
discovery (section 6); work package `docs/work/discover/`.

## Product promise

Save an interesting GitHub repository in seconds and find it later by what it
does, why you saved it, and the project where you might use it.

Repo Shelf is a local-first personal repository library: a small web application
with a Chromium browser extension for capture. The primary user collects tools,
skills and open-source projects but often remembers their purpose rather than
their names. A star alone does not preserve the reason for saving a repository.

Revision 2 changes the primary goal. Manual collection remains, but the main
job is now **automatic discovery**: the app knows the owner's active projects
(for example Augur, AugurNext, Navo, alltom — examples, not a fixed list) and
periodically proposes open-source repositories that address those projects'
current needs. The owner's main action becomes triage — accept, later or
dismiss — rather than capture.

## Repository and current scope

- GitHub owner: rct-labs. Repository: repo-shelf. Visibility: public.
- At handoff, this repository contains only this requirements document and no
  implementation, scaffolding, generated assets, dependency manifests or license.
- The owner explicitly chose no license file for this initial documentation-only
  repository. Do not add a license or copy third-party source during this phase.
  Public visibility alone does not grant a permissive reuse license; do not claim
  MIT, unrestricted reuse or guaranteed popularity. Revisit licensing with the
  owner before publishing reusable implementation if needed.
- When the owner supplies the implementation prompt, implement the MVP below.
  Do not create a PR unless the owner separately requests one.

## Users and experience

The initial target is a single Windows desktop user using Chrome or Edge and
GitHub. Keep core data portable so macOS/Linux support remains feasible.
Provide Simplified Chinese and English UI, defaulting from browser preference
with a persistent language switch. Repository names and source text stay intact.

The essential journey (revision 2) is: register a local project -> review its
generated profile -> receive a weekly batch of recommendations per project ->
triage each one with a single key -> accepted items join the library tagged
with that project. The original manual journey still works unchanged: save
repository -> optionally write why -> search by purpose -> inspect matching
evidence and personal notes -> reopen the repository. Saving and searching must
never require AI credentials or force the user through classification;
discovery does require a DeepSeek key (see section 6).

## MVP requirements

### 1. Capture and repository identity

- Paste a public GitHub repository URL into the app and save it.
- A Chrome/Edge extension provides one-click capture on a repository page, with
  optional reason and tags; show success, already-saved and retryable failure.
- Normalize repository subpage URLs, fragments and trailing slashes. Reject
  non-GitHub URLs clearly in the MVP. Never execute commands from page contents.
- Use the GitHub repository ID as durable identity after fetching metadata;
  handle renamed/transferred repositories without creating duplicate entries.
- Saving is local and does not star, unstar, fork or otherwise modify GitHub.
- If the local service is offline, retain a bounded pending capture locally and
  make retry/discard explicit; never silently lose a capture or claim success.

### 2. Import and refresh

- Import a public user's GitHub Stars with pagination, progress, cancellation,
  partial-failure reporting and repeat-safe retries. Authentication is optional
  for public data and can be supplied to improve available API quota.
- Fetch description, topics, language, license identifier when available,
  archived status, last-push time, stars, default branch, README and fetch time.
- Keep README text locally so search continues offline after initial sync.
- Refresh source fields without overwriting personal notes, tags or status.
- Re-imports are additive by design (owner decision): repositories already in
  the library are skipped (star membership is still reconciled), so the GitHub
  API quota is spent only on repos not yet stored. Source fields update only
  through explicit refresh actions.
- Unstarring upstream must not delete local records. Distinguish missing,
  inaccessible, stale and successfully refreshed records.
- Handle rate limits and transient errors with bounded retries; honor server
  retry/rate-limit information rather than repeatedly hammering the API.
- Private-repository import, GitHub OAuth and multi-account sync are deferred.

### 3. Personal knowledge

- Each saved repository supports a reason, editable notes, tags, optional related
  project labels and status: Inbox, To investigate, Tried, Adopted or Dismissed.
- Keep personal fields separate from source metadata and optional generated text.
- Provide create/edit/delete actions, with confirmation or undo for deletion.
- Allow filtering by tags, status, language, archived state and related project.
- Show saved time, source refresh time and a link to the original repository.
- AI-generated summaries must never silently replace the user's own judgment.

### 4. Search and retrieval

- Search locally across name, owner, description, topics, README, reason and notes.
- Support mixed Chinese/English notes and queries; pick and verify tokenization
  instead of assuming a default English tokenizer handles Chinese adequately.
- Weight exact repository-name matches and personal notes appropriately. Return
  highlighted matching snippets and the matching fields, not just opaque scores.
- Combine keyword search with filters. Display useful empty/loading/error states.
- Keyword search is required and must work without any model or network access.
- Natural-language cross-language semantic search is a later enhancement, not a
  claim of the MVP. A Chinese query need not find an English-only README unless
  matching Chinese metadata/notes or an explicitly implemented semantic layer exists.

### 5. Data ownership and local runtime

- Use an embedded database such as SQLite and a local full-text index. The app
  should not require Docker, a hosted backend or a paid AI service for core use.
- Keep all durable user data in a documented location outside the source tree.
- Support versioned JSON export/import of repository records, personal fields
  and stored README snapshots, excluding credentials. Restore without data loss.
- Bind the local service to loopback. Authenticate extension requests with a
  local pairing token; validate origins and do not allow arbitrary websites to
  read or mutate the library. Request minimal extension permissions.
- Store optional GitHub credentials server-side/local secure storage; do not
  expose them to page scripts, exports, logs or public repository files.
- Sanitize rendered Markdown/HTML. Repository text is untrusted data, never an
  instruction to execute shell commands, install packages or change configuration.
- No telemetry or cloud synchronization by default.

### 6. Project-driven discovery (revision 2)

Discovery is desktop-only, like AI summaries. The Node reference service does
not implement it; the shared UI hides discovery when the backend reports that
the feature is unavailable (a `features` flag on `/api/settings`).

**Projects**

- The owner registers a project by name and local directory path. Projects can
  be paused, edited and removed; removal never deletes library records.
- A project profile is a short list of editable need statements (about 5–12),
  plus detected languages and main dependencies. Profile generation runs only
  when the owner explicitly asks for it, per project; adding a project never
  sends anything off the machine.
- Profile generation reads only a whitelist, each file truncated: `README*`,
  `AGENTS.md`, `CONTEXT.md`, open (non-DONE) rows of `TASK_QUEUE.md`, and the
  dependency names (not versions or scripts) from `package.json`,
  `pyproject.toml`, `requirements*.txt`, `*.csproj`, `go.mod`, `Cargo.toml`.
  It never reads `.env*`, `memory/**`, `data/**`, `config/**`, `.git/**`,
  secrets, databases or source files. The UI shows exactly which files were
  read. The owner can edit or replace every generated need statement.

**Candidate recall and budget**

- Recall uses the GitHub search API with queries derived from need statements
  and project languages, restricted to recently pushed, non-archived, non-fork
  repositories. Trending-page scraping is out of scope.
- Every run has a hard budget: at most 4 search queries per active project,
  at most 30 results per query, and README fetches only for the shortlist sent
  to scoring (at most 10 per project). Repositories already in the library or
  already dismissed for that project are dropped before any further request.
- Rate limits reuse the existing `rate_limited` handling; a run that hits the
  limit stops, keeps what it has and reports the reset time. No hammering.
- Pre-filter before AI scoring: license present, pushed within 180 days,
  recent star growth (stars per month since creation) favored over total stars.

**Scoring**

- DeepSeek scores each shortlisted repository against one project's need
  statements and returns structured JSON: relevance 0–100, the matched need,
  integration cost (low / medium / high) and a one- or two-sentence reason in
  the UI language. Invalid or partial JSON rejects that item only.
- Repository text is untrusted input to the prompt: the prompt states that it
  is data, never instructions; the rationale is rendered through the existing
  sanitizer.
- Recent triage decisions for that project (accepted, and dismissed with a
  reason) are given to the scorer as preference examples.
- Only candidates scoring at least 60 are shown; at most 5 per project per run.

**Schedule**

- A run happens at most once per 7 days automatically: checked at startup and
  hourly while the app runs, so a missed week catches up once. The owner can
  also start a run manually. One run at a time; runs are cancellable and their
  progress is visible like import jobs.
- When a run produces new candidates, the tray shows one notification.

**Candidates and triage**

- Candidates are stored separately from the library (not as a status of saved
  repositories), keyed by project and GitHub repository id, so pending
  recommendations never enter library search, counts or filters.
- Triage states: pending, later, accepted, dismissed. Dismiss takes an optional
  reason: not relevant, too heavy, already have one, low quality.
- Accept saves the repository through the normal save path, adds the project
  as a related-project label, sets status `To investigate`, and keeps the
  recommendation rationale visible in the detail view as AI-generated text,
  never written into the owner's reason or notes.
- A dismissed repository is never recommended again for that project.
- Projects, need statements and triage decisions are personal data: they are
  included in the versioned export and restored without loss. Credentials
  remain excluded.

**UI**

- The home view has three tabs: Recommendations (default when discovery is
  available), Library (the existing search and list, unchanged) and Projects.
- Recommendations are grouped by project with per-project counts. Each card
  shows name, description, language, stars and growth, the matched need,
  integration cost and reason, with actions Accept / Later / Dismiss.
- Keyboard triage: `j`/`k` move, `a` accept, `s` later, `d` dismiss.
- The window uses a feed size between the compact and detail sizes while the
  Recommendations tab is shown.

## Suggested design, not a fixed stack

Use a local service, embedded database/full-text search, browser UI and a thin
Manifest V3 capture extension. Kimi may choose a maintainable TypeScript or
Python-based stack after checking Windows packaging and Chinese retrieval.
Keep import, repository storage, search and capture interfaces separate enough
to test; avoid microservices and premature provider abstractions.

The main view (revision 2) opens on the Recommendations tab when discovery is
available and on the Library tab otherwise. A detail panel
shows the user's reason and notes alongside README and metadata. A restrained,
readable design with clear states matters more than dashboards or star charts.

## Explicitly deferred

- MCP access, semantic embeddings and AI tagging (AI summaries and project
  discovery are implemented:
  optional DeepSeek key, stored in a separate `generated` table, never
  overwriting personal fields).
- Mobile apps, hosted multi-user service, shared collections and cloud sync.
- Full source-code indexing, dependency scanning and automatic repository execution.
- General web history capture or a universal bookmark manager.
- Release monitoring, social features and growth automation. (Project-driven
  recommendations moved into scope in revision 2; generic trending feeds did not.)
- Automatic project detection, scanning project source code, and reading any
  project file outside the section 6 whitelist.
- Browser store publication, custom domain, paid infrastructure and PR creation.

## Acceptance criteria

1. Save one repository through the app and through the extension; it appears once
   with the same persistent identity and preserved personal annotations.
2. Import a fixture with more than one page of Stars and a simulated mid-import
   failure; retry completes without duplicates or lost annotations.
3. Re-import or refresh changed source metadata; personal notes and status remain.
   An upstream unstar/archive/rename does not erase the local knowledge record.
4. Search Chinese notes, English README content, exact names and combined filters.
   Results show a matching excerpt and can open the repository/detail view.
5. With the network disabled, previously saved records and README search work.
6. Export, restore into an empty database and compare all durable records and
   personal fields; credentials are absent from the export.
7. Disconnect the service during extension capture; the UI reports pending/failure
   and a retry saves exactly one record after recovery.
8. With 5,000 representative stored repositories, target warm local search p95
   below 500 ms, excluding rendering/network. Record dataset, machine and timing
   method; report measured results rather than claiming an unmeasured target.
9. Reject unauthorized local API access and render hostile README fixtures inertly.
10. A documented Windows setup works from a fresh checkout. Core workflows need
    no AI account. Provide automated tests for import, identity, annotation
    preservation, retrieval, export/restore and local API boundaries, plus an
    actual browser smoke test covering capture and search.

Revision 2 (discovery), verified with a fake GitHub API and a fake DeepSeek
endpoint via `REPO_SHELF_GITHUB_API` / `REPO_SHELF_DEEPSEEK_API`:

11. Adding a project sends no network request. Generating its profile reads
    only whitelisted files (a fixture directory containing `.env`, `memory/`
    and `data/` proves these are never read) and yields editable need statements.
12. A discovery run respects the per-run budget: the fake GitHub API records at
    most 4 search requests per project and README requests only for the
    shortlist; repositories already saved or dismissed trigger no request.
13. Scoring stores at most 5 candidates per project with score >= 60; malformed
    AI JSON for one item does not fail the run; a rate-limited run stops with
    the reset time and keeps partial results.
14. Accept creates exactly one library record with the project label, status
    `To investigate` and untouched reason/notes; dismiss excludes the repository
    from the next run for that project; pending candidates never appear in
    library search results.
15. Export then restore into an empty database preserves projects, need
    statements and triage decisions.
16. A browser test against the desktop service triages candidates by keyboard;
    the existing Node smoke test still passes with the discovery tab hidden.

Use public or synthetic test fixtures, never the owner's real library or tokens
in committed tests, screenshots or demo data. Unit checks cannot substitute for
the browser smoke test; document any real execution limitation honestly.

## Implementation handoff

Revision 2 is delivered through the work package `docs/work/discover/`
(`brief.md`, `spec.md`) and the task queue `TASK_QUEUE.md`.

Implement capture/storage first, then import/search, then the extension and
end-to-end validation. Deliver a runnable MVP, setup instructions, a concise
architecture note and measured acceptance evidence. Update this document if an
implementation decision changes an observable requirement. Make routine design
choices autonomously; ask only for material product changes or external publishing.

## Research references

- https://github.com/asciimoo/hister : personal full-text search and GitHub content
  extraction; AGPL-3.0-or-later. Inspiration only, not an approved code dependency.
- https://astralapp.com/ : GitHub Stars organization with tags and notes.
- https://github.com/sidoshi/karakeep-sync : an existing Stars import integration.
- https://docs.github.com/en/rest/activity/starring : official Stars API.

The differentiator is searchable personal intent and evaluation history tied to
repositories, rather than another display of star counts. Validate that promise
with a small real collection before investing in deferred features.
