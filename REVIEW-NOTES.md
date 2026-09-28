# Review notes

Defect and observation records, not an executable task list. Batch ordinary
defects at their due checkpoint. Resolve with verification evidence; record
a reason when deferring. Blocking defects cannot be waived by a status label.

## 2026-09-28 07:34 UTC - DISC-1, DISC-2, DISC-3, DISC-4, DISC-5, DISC-6, DISC-7, DISC-8, DISC-9 (fable, at afa5672)

<!-- defect: {"severity": "low", "file": "app/RepoShelf.Core/DiscoveryTriage.cs", "issue": "AcceptAsync (line 91) always writes status=to_investigate, so accepting a candidate for a repository the owner already saved (e.g. captured via the extension after it was proposed) overwrites the owner's chosen status such as adopted. REQUIREMENTS §6 mandates the status on accept and only protects reason/notes, so this is spec-conformant, but status is a personal field; the test AcceptExistingRenamedRepoPreservesPersonalFieldsAndOtherLabelsWithoutRefresh does not assert on status.", "fix": "Only set status when SaveRepoAsync created the repo (or when the current status is the default); add a status assertion to the accept-existing test.", "action": "optional", "id": "BUG-4d70ce39e5d2", "blocking": "none", "due": "backlog", "status": "resolved", "resolution": "Accept sets to_investigate only when status is inbox; DiscoveryTriageTests accept-existing asserts adopted is kept."} -->
### BUG-4d70ce39e5d2

- severity: low | action: optional
  - status: resolved | due: backlog | blocking: none
  - file: app/RepoShelf.Core/DiscoveryTriage.cs
  - issue: AcceptAsync (line 91) always writes status=to_investigate, so accepting a candidate for a repository the owner already saved (e.g. captured via the extension after it was proposed) overwrites the owner's chosen status such as adopted. REQUIREMENTS §6 mandates the status on accept and only protects reason/notes, so this is spec-conformant, but status is a personal field; the test AcceptExistingRenamedRepoPreservesPersonalFieldsAndOtherLabelsWithoutRefresh does not assert on status.
  - fix: Only set status when SaveRepoAsync created the repo (or when the current status is the default); add a status assertion to the accept-existing test.
  - resolution: Accept sets to_investigate only when status is inbox; DiscoveryTriageTests accept-existing asserts adopted is kept.
<!-- defect: {"severity": "low", "file": "public/discover.js", "issue": "onKey handles the dismiss-menu branch (lines 330-341: digits 1-4 and Enter) before the isTyping(e.target) guard at line 342. Trigger: press d on a card, click into the omnibox, type 1 → the keydown capture handler dismisses the candidate as not_relevant before the input event switches to the Library tab.", "fix": "Move the isTyping check above the menu branch (keep Escape working), or close the menu when focus leaves the feed pane.", "action": "optional", "id": "BUG-fdcfe3ddbb2c", "blocking": "none", "due": "backlog", "status": "resolved", "resolution": "discover.js closes the dismiss menu when a key arrives from an input, before the menu branch; e2e discover.mjs passes."} -->
### BUG-fdcfe3ddbb2c

- severity: low | action: optional
  - status: resolved | due: backlog | blocking: none
  - file: public/discover.js
  - issue: onKey handles the dismiss-menu branch (lines 330-341: digits 1-4 and Enter) before the isTyping(e.target) guard at line 342. Trigger: press d on a card, click into the omnibox, type 1 → the keydown capture handler dismisses the candidate as not_relevant before the input event switches to the Library tab.
  - fix: Move the isTyping check above the menu branch (keep Escape working), or close the menu when focus leaves the feed pane.
  - resolution: discover.js closes the dismiss menu when a key arrives from an input, before the menu branch; e2e discover.mjs passes.
<!-- defect: {"severity": "low", "file": "app/RepoShelf.Core/DiscoveryRunner.cs", "issue": "Start() (line 99) falls back to \"zh\" for reason_lang when no manual run has stored discovery_lang, so the first scheduled run on a fresh English-UI install produces Chinese rationales; spec Scoring says the reason is in the UI language.", "fix": "Fall back to the stored UI language setting (or the language used by the last profile call) instead of a hard-coded \"zh\".", "action": "optional", "id": "BUG-5906e9206b20", "blocking": "none", "due": "backlog", "status": "resolved", "resolution": "DiscoveryRunner.ResolveLang: saved UI language (manual run or profile call) else OS UI language; DiscoveryRunTests asserts OS language."} -->
### BUG-5906e9206b20

- severity: low | action: optional
  - status: resolved | due: backlog | blocking: none
  - file: app/RepoShelf.Core/DiscoveryRunner.cs
  - issue: Start() (line 99) falls back to "zh" for reason_lang when no manual run has stored discovery_lang, so the first scheduled run on a fresh English-UI install produces Chinese rationales; spec Scoring says the reason is in the UI language.
  - fix: Fall back to the stored UI language setting (or the language used by the last profile call) instead of a hard-coded "zh".
  - resolution: DiscoveryRunner.ResolveLang: saved UI language (manual run or profile call) else OS UI language; DiscoveryRunTests asserts OS language.
<!-- defect: {"severity": "low", "file": "app/RepoShelf.Core/Backup.cs", "issue": "Import (line 380) applies ImportDiscovery in its own transaction after the repos loop has already committed per item, so a v2 file with a bad discovery section (e.g. candidate referencing a missing project) leaves repos restored but projects/candidates rolled back; BackupTests.InvalidDiscoveryReferenceRollsBackDiscoveryChanges asserts only the discovery half. Consistent with the existing per-item repo import, so not a regression.", "fix": "Validate the projects/discoveryCandidates arrays (ids, projectId references, array-of-strings columns) before importing any repos, so a malformed file is rejected as a whole.", "action": "optional", "id": "BUG-76932c697402", "blocking": "none", "due": "backlog", "status": "resolved", "resolution": "Backup.Import dry-runs the discovery section before any repo is written; BackupTests asserts no repo is restored from a rejected file."} -->
### BUG-76932c697402

- severity: low | action: optional
  - status: resolved | due: backlog | blocking: none
  - file: app/RepoShelf.Core/Backup.cs
  - issue: Import (line 380) applies ImportDiscovery in its own transaction after the repos loop has already committed per item, so a v2 file with a bad discovery section (e.g. candidate referencing a missing project) leaves repos restored but projects/candidates rolled back; BackupTests.InvalidDiscoveryReferenceRollsBackDiscoveryChanges asserts only the discovery half. Consistent with the existing per-item repo import, so not a regression.
  - fix: Validate the projects/discoveryCandidates arrays (ids, projectId references, array-of-strings columns) before importing any repos, so a malformed file is rejected as a whole.
  - resolution: Backup.Import dry-runs the discovery section before any repo is written; BackupTests asserts no repo is restored from a rejected file.
<!-- defect: {"severity": "low", "file": "app/RepoShelf/TrayIcon.cs", "issue": "ShowNewCandidates (line 57) hard-codes the English balloon text \"N new recommendations\" while the web UI is bilingual; the existing tray menu is also English-only and the spec scopes strings to i18n.js, so this is an observation.", "fix": "Pass the UI language from the web view (or a settings value) and add a zh-CN variant when the tray is localized.", "action": "none", "id": "BUG-a2a2d85740f1", "blocking": "none", "due": "backlog", "status": "resolved", "resolution": "Tray balloon localized (zh/en) via DiscoveryRunner.ResolveLang."} -->
### BUG-a2a2d85740f1

- severity: low | action: none
  - status: resolved | due: backlog | blocking: none
  - file: app/RepoShelf/TrayIcon.cs
  - issue: ShowNewCandidates (line 57) hard-codes the English balloon text "N new recommendations" while the web UI is bilingual; the existing tray menu is also English-only and the spec scopes strings to i18n.js, so this is an observation.
  - fix: Pass the UI language from the web view (or a settings value) and add a zh-CN variant when the tray is localized.
  - resolution: Tray balloon localized (zh/en) via DiscoveryRunner.ResolveLang.

