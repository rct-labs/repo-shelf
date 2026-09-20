# Repo Shelf

Save an interesting GitHub repository in seconds, and find it later by what it
does, why you saved it, and the project where you might use it.

Repo Shelf is a **local-first personal repository library**: a native Windows
tray app (WPF + WebView2) with a built-in local service, plus a Chromium
browser extension for capture. Your data lives in a local SQLite database —
no cloud account, no AI service, no telemetry.

## Features

- **One-click capture** from a GitHub repository page (Chrome/Edge extension),
  or by pasting a URL into the app's single input box — the same box searches
  and saves. Renamed/transferred repositories keep a single record via the
  durable GitHub repository id.
- **Launcher-style window**: a small borderless window that stays out of the
  way; `Ctrl+Alt+K` summons it from anywhere, Esc dismisses; it expands only
  when you open a repository's details.
- **AI summaries (optional)**: a stored DeepSeek key lets the app summarize a
  repository from its metadata and README. Summaries live in a separate
  `generated` table — they never overwrite your own reason and notes.
- **Import from Chrome bookmarks**: reads the Chromium bookmarks file
  (read-only) and imports the GitHub repositories found there.
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

- Windows 10/11, **.NET 10 desktop runtime** (already present if the .NET SDK
  is installed), WebView2 runtime (preinstalled on Windows 11).
- Chrome or Edge for the capture extension.
- Development: .NET 10 SDK; Node.js 20+ with pnpm only for the optional
  reference implementation and the browser smoke test.

## Desktop app (primary)

```powershell
dotnet publish app/RepoShelf -c Release
```

The single-file executable lands in
`app/RepoShelf/bin/Release/net10.0-windows/win-x64/publish/RepoShelf.exe`
(~4 MB, framework-dependent — it uses the .NET 10 runtime installed on the
machine). Double-click to run:

- The local service starts in-process (loopback only, port 4790 by default).
- A WebView2 window shows the library UI; closing the window keeps the app in
  the **system tray** (double-click to reopen, right-click for the menu).
- **Launch at login** is registered on first run (HKCU Run key) and can be
  toggled from the tray menu.
- A second launch simply focuses the running instance (named-mutex single
  instance).

Modes: `RepoShelf.exe --background` (service + tray, no window — used by the
login entry), `RepoShelf.exe --service` (headless service only).

For distribution to machines without .NET 10, publish self-contained instead
(larger exe, zero prerequisites):

```powershell
dotnet publish app/RepoShelf -c Release -p:SelfContained=true
```

### Web UI / reference implementation (Node.js)

The same UI and API also run as a plain web app (useful for development and
for verifying the browser smoke test without the desktop shell):

```sh
pnpm install
pnpm start          # http://127.0.0.1:4790
```

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
# .NET app (primary)
dotnet build app/RepoShelf.slnx          # build
dotnet test app/RepoShelf.slnx           # 69 unit/integration tests (hermetic)
dotnet run --project tools/RepoShelf.PerfCheck -c Release   # 5,000-repo latency

# Node reference implementation + browser smoke
pnpm test                                # 56 unit/integration tests (hermetic)
pnpm test:e2e                            # browser smoke against the Node service
pnpm test:e2e:desktop                    # browser smoke against the published .NET exe
```

`test:e2e*` launches Playwright's Chromium build, loads the extension,
captures a repository through the popup, exercises the offline pending queue,
and verifies search plus hostile-README sanitization in the UI. It uses the
real public GitHub API, so it needs network access. Branded Chrome/Edge ≥ 137
ignore `--load-extension`, which is why the automated test uses Chromium; this
does not affect manual installation in real Chrome/Edge.

## Project layout

```
app/RepoShelf.Core/   .NET backend: SQLite/FTS5, search, jobs, HTTP API (Kestrel)
app/RepoShelf/        WPF shell: WebView2 window, tray, autostart, single instance
test/RepoShelf.Tests/ xunit suite (mirrors the Node test suite)
tools/                perf harness, icon generator
server/               Node.js reference implementation (same API)
public/               web UI (vanilla JS, bilingual; embedded into the exe)
extension/            Manifest V3 capture extension
tests/                Node test suites + browser smoke + perf (reference)
```

See [ARCHITECTURE.md](ARCHITECTURE.md) for the design and
[ACCEPTANCE.md](ACCEPTANCE.md) for measured acceptance evidence.
