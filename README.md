# Repo Shelf

Save an interesting GitHub repository in seconds, and find it later by what it
does, why you saved it, and the project where you might use it.

Repo Shelf is a **local-first personal repository library**: a small web
application with a Chromium browser extension for capture. Your data lives in a
local SQLite database — no cloud account, no AI service, no telemetry.

## Features

- **One-click capture** from a GitHub repository page (Chrome/Edge extension)
  or by pasting a URL into the app. Renamed/transferred repositories keep a
  single record via the durable GitHub repository id.
- **Personal knowledge**: reason, notes, tags, related-project labels and a
  status (Inbox / To investigate / Tried / Adopted / Dismissed). Personal
  fields are stored separately from source metadata and survive refreshes,
  renames, transfers and upstream unstars.
- **Local full-text search** across name, owner, description, topics, README,
  reason and notes, with verified mixed Chinese/English tokenization, field
  weighting and highlighted snippets. Works fully offline.
- **GitHub Stars import** with pagination, progress, cancellation, resume
  after failures, and partial-failure reporting. A GitHub token is optional
  (raises the API quota).
- **Data ownership**: versioned JSON export/restore of the whole library
  (credentials are never exported). All durable data lives outside the source
  tree.
- **Bilingual UI**: English and Simplified Chinese, following your browser
  preference with a persistent language switch.

## Requirements

- Windows 10/11 (macOS/Linux work too, untested), Node.js **20+**, pnpm.
- Chrome or Edge for the extension.

## Setup

```sh
pnpm install
pnpm start
```

Then open http://127.0.0.1:4790 in your browser.

Durable data is stored outside the source tree:

- Windows: `%APPDATA%\repo-shelf\repo-shelf.db`
- macOS: `~/Library/Application Support/repo-shelf/`
- Linux: `${XDG_DATA_HOME:-~/.local/share}/repo-shelf/`

Environment overrides:

| Variable | Default | Purpose |
| --- | --- | --- |
| `REPO_SHELF_PORT` | `4790` | Loopback HTTP port |
| `REPO_SHELF_DATA_DIR` | per-OS dir above | Data directory |
| `REPO_SHELF_GITHUB_API` | `https://api.github.com` | GitHub API base URL (tests) |
| `REPO_SHELF_TOKEN` | generated | Fix the pairing token for a run (dev/test) |

## Browser extension

1. Start the app (`pnpm start`), open http://127.0.0.1:4790 → **Settings** and
   copy the pairing token.
2. Open `chrome://extensions` (or `edge://extensions`), enable **Developer
   mode**, choose **Load unpacked** and select the `extension/` directory.
3. Right-click the extension → **Options**: keep the server URL
   (`http://127.0.0.1:4790`), paste the pairing token, **Save**, then
   **Test connection**.
4. On any GitHub repository page, click the extension icon, optionally add a
   reason and tags, and save.

If the local service is offline during capture, the capture is queued locally
in the extension (bounded at 50 entries, badge shows the count) and can be
retried or discarded explicitly from the popup. Nothing is silently dropped.

The extension requests only `storage`, `activeTab` and loopback host
permissions. It never executes page content and never talks to GitHub
directly.

## Security model (summary)

- The HTTP service binds to loopback only and requires a per-install pairing
  token (`X-RepoShelf-Token` header) on every API call.
- Requests with a foreign `Origin` are rejected; only the app's own origin and
  browser-extension origins are honored. The `Host` header must be the
  loopback listener (DNS-rebinding protection).
- The optional GitHub token is stored server-side only, never exported,
  logged or exposed to page scripts.
- README markdown is rendered through `marked` + `DOMPurify`; repository text
  is untrusted data and is never executed or treated as instructions.

## Development

```sh
pnpm test         # unit + integration tests (no network needed)
pnpm test:e2e     # real-browser smoke test (see below)
pnpm perf         # 5,000-repo search latency measurement
```

`pnpm test:e2e` launches Playwright's Chromium build, loads the extension,
captures a repository through the popup, exercises the offline pending queue,
and verifies search plus hostile-README sanitization in the UI. It uses the
real public GitHub API, so it needs network access. Branded Chrome/Edge ≥ 137
ignore `--load-extension`, which is why the automated test uses Chromium; this
does not affect manual installation in real Chrome/Edge.

## Project layout

```
server/    local service (Node.js, ESM, no build step)
public/    web UI (vanilla JS, bilingual)
extension/ Manifest V3 capture extension
tests/     node:test unit/integration tests, e2e smoke, perf check
```

See [ARCHITECTURE.md](ARCHITECTURE.md) for the design and
[ACCEPTANCE.md](ACCEPTANCE.md) for measured acceptance evidence.
