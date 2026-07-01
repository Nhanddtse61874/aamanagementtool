# P14 — SharePoint Export (file-sync) — REQUIREMENTS

**Created:** 2026-07-01
**Phase goal:** Let the user reliably export Logs (timesheet) + Daily (standup) + a DB backup to a
SharePoint document library via a **OneDrive-synced local folder**, with **verification** of the
configured destination so files never land in the wrong place.

## Decision context (locked at brainstorm, 2026-07-01)
- **Approach = write to a folder path that reaches SharePoint** (user chose file-based). NOT Microsoft
  Graph / MSAL — user has no Azure AD app registration. `[VERIFIED — user answer]`
- User **cannot run the OneDrive sync client**, but **can already see the SharePoint library as a
  drive/folder in File Explorer** (mapped network drive / "Open in Explorer" / `\\tenant.sharepoint.com@SSL\DavWWWRoot\...`).
  So writing a file into that path uploads it via WebDAV — no OneDrive client, no app registration. `[VERIFIED — user answer]`
- The config takes a **folder/drive path** (Browse or paste), never a web URL. The user "can only provide
  the folder address of SharePoint." `[VERIFIED — user answer]`
- The existing `ExportHubService` **already writes** timesheet (Logs), standup (Daily), and a DB backup
  into each configured export root. The reused field is Settings → "Shared/SharePoint folder"
  (`ExportRoot1Path`). `[VERIFIED — code: ExportHubService.ExportRootAsync + SettingsTab.xaml]`
- ⇒ The genuinely new work is **destination verification + clear status UX**, not a new export pipeline.

## Requirements

### SP-01 — Verify the SharePoint destination folder
The user can verify the configured "Shared/SharePoint folder" before exporting. The **write-probe is
authoritative** (create dir if needed → write + delete a temp file). Verification reports one of:
- **OK (green):** write-probe succeeds AND the path is a SharePoint/network destination — a UNC path
  (`\\...`, incl. `...sharepoint.com@SSL\DavWWWRoot`), a mapped **network** drive (`DriveInfo.DriveType ==
  Network`), or an "OneDrive"/"sharepoint" path segment. → "exports will upload here."
- **Warning (amber):** write-probe succeeds BUT the path looks like a plain local folder (not network /
  not a synced/mapped location) → "files stay local and will NOT reach SharePoint."
- **Error (red):**
  - the value is a web URL (`http://` / `https://`) → guide the user to "Open in Explorer" / map a drive
    and paste the drive/folder path instead; OR
  - the folder cannot be created / is not writable (probe throws).

Testable (pure `Classify` + write-probe over a temp dir): `https://x.sharepoint.com/...` → Error(url);
`\\host\share` / an "OneDrive - Co" segment → OK; a plain temp dir → Warning; an invalid path → Error(write).

### SP-02 — "Export now" delivers Logs + Daily + DB backup to the verified destination
"Export now" writes, into the SharePoint folder: the timesheet (Logs) markdown, the standup (Daily)
markdown, and a full DB backup — per the existing `ExportHubService` behavior. The status line names what
landed (or that a root was skipped). No regression to the existing second (local) root.

Testable: existing ExportHubService tests stay green; a run against a temp root produces the
timesheet/daily/db artifacts.

### SP-03 — Guard export against a bad destination
If the SharePoint folder fails **hard** verification (web-URL or unwritable) at export time, "Export now"
surfaces the error for that root and does not silently swallow it; the other (local) root still runs
(existing best-effort-per-root behavior is preserved).

Testable: a web-URL root yields a "failed: … — <reason>" status line, not a silent success.

## Out of scope (v1)
- Microsoft Graph / MSAL / real API upload (no app registration) — revisit only if file-sync is insufficient.
- Mapping a SharePoint **web URL** to a local synced folder (user provides the folder path directly).
- Per-team SharePoint destinations (single shared folder for now; per-team subfolders already created inside).

## Phase mapping
All three REQs land in a single phase (P14). Coverage: SP-01, SP-02, SP-03 → P14 (100%).
