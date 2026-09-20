# Repo Shelf

revision: 1
date: 2026-09-20
status: Requirements only; implementation handoff to Kimi

## Product promise

Save an interesting GitHub repository in seconds and find it later by what it
does, why you saved it, and the project where you might use it.

Repo Shelf is a local-first personal repository library: a small web application
with a Chromium browser extension for capture. The primary user collects tools,
skills and open-source projects but often remembers their purpose rather than
their names. A star alone does not preserve the reason for saving a repository.

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

The essential journey is: save repository -> optionally write why -> search by
purpose -> inspect matching evidence and personal notes -> reopen the repository.
Saving must never require AI credentials or force the user through classification.

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

## Suggested design, not a fixed stack

Use a local service, embedded database/full-text search, browser UI and a thin
Manifest V3 capture extension. Kimi may choose a maintainable TypeScript or
Python-based stack after checking Windows packaging and Chinese retrieval.
Keep import, repository storage, search and capture interfaces separate enough
to test; avoid microservices and premature provider abstractions.

The main view should emphasize search and saved repositories. A detail panel
shows the user's reason and notes alongside README and metadata. A restrained,
readable design with clear states matters more than dashboards or star charts.

## Explicitly deferred

- MCP access, semantic embeddings and AI tagging (AI summaries are implemented:
  optional DeepSeek key, stored in a separate `generated` table, never
  overwriting personal fields).
- Mobile apps, hosted multi-user service, shared collections and cloud sync.
- Full source-code indexing, dependency scanning and automatic repository execution.
- General web history capture or a universal bookmark manager.
- Release monitoring, recommendation feeds, social features and growth automation.
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

Use public or synthetic test fixtures, never the owner's real library or tokens
in committed tests, screenshots or demo data. Unit checks cannot substitute for
the browser smoke test; document any real execution limitation honestly.

## Implementation handoff

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
