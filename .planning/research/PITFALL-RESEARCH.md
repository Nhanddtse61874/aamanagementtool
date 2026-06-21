# Pitfall Research — WPF Timesheet Tool (Shared SQLite over OneDrive/Teams)

**Date:** 2026-06-21
**Spec:** `docs/superpowers/specs/2026-06-21-timesheet-tool-design.md`
**Scope:** 2–5 user WPF (.NET 8) timesheet tool, shared SQLite `.db` synced via OneDrive/Teams, single-writer/last-write-wins, soft-delete, 8h/day cap, smart hour distribution.

Tag legend: `[VERIFIED]` = confirmed from primary/official docs in this research; `[CITED]` = direct source URL; `[ASSUMED]` = reasoned inference not directly verified.

---

## 1. Shared SQLite over OneDrive/Teams — THE primary risk (HIGH)

**Do not soften this:** putting a live SQLite database in a OneDrive/Teams-synced folder is a corruption hazard that the SQLite authors explicitly warn against. It is not a "best-effort" pattern — it is an accepted-risk pattern that *will* produce conflict copies and *can* produce a malformed database. The spec already labels file-level conflict as "accepted risk" (§4), but the mitigations below materially reduce both likelihood and blast radius and should be treated as mandatory, not optional.

### What actually goes wrong

**(a) File-level conflict copies — likelihood HIGH, impact MEDIUM (data divergence, not corruption).**
OneDrive resolves conflicts only at the whole-file level. When two machines edit the `.db` while offline/unsynced, OneDrive cannot merge and produces a duplicate named like `timesheet-DESKTOP-AB12.db` (computer-name suffix). One user's writes silently land in the conflict copy and "disappear" from the canonical file. `[CITED]` Microsoft Support — Duplicate files in OneDrive: https://support.microsoft.com/en-us/office/duplicate-files-in-onedrive-fd47ce5e-8dd0-465e-9e3a-461e1a3cf613 ; `[CITED]` Nate Chamberlain — "we couldn't merge the changes in [filename]" with computer name: https://natechamberlain.com/2017/09/20/onedrive-and-sharepoint-sync-issue-you-now-have-two-copies-of-a-file-we-couldnt-merge-the-changes-in-filename-appended-with-computer-name/
This is the *most likely* failure mode for a 2–5 person tool. It is recoverable (manual merge) but only if someone notices the suffixed file before continuing to work on the divergent canonical copy.

**(b) WAL/-shm sidecar files not syncing atomically with the main `.db` — likelihood MEDIUM, impact HIGH (true corruption).**
In WAL mode SQLite maintains `*.db-wal` (committed-but-not-checkpointed pages) and `*.db-shm` (shared-memory index). These are *separate files*. OneDrive syncs each file independently and on its own schedule, so a remote machine can receive a new `.db` without the matching `-wal`, or vice versa. SQLite's own corruption guide is explicit: "If the previous write transaction failed, then it is important that any rollback journal (the `*-journal` file) or write-ahead log (the `*-wal` file) be copied together with the database file itself" and "SQLite must see the journal files in order to recover... If the [hot journal files] are moved, deleted, or renamed after a crash or power failure, then automatic recovery will not work and the database may go corrupt." `[CITED]` https://sqlite.org/howtocorrupt.html
The `-shm` file additionally uses shared-memory mapping that does not work correctly across hosts. `[VERIFIED]` (sqlite.org howtocorrupt + useovernet).

**(c) Copying/syncing the `.db` mid-transaction — likelihood MEDIUM, impact HIGH (malformed db).**
OneDrive may begin uploading the `.db` while a transaction is in flight, capturing "some old and some new content, and thus be corrupt." `[CITED]` https://sqlite.org/howtocorrupt.html (background-backup-while-writing clause). Short connections (below) shrink the window but do not eliminate it.

**(d) Network/locking unreliability — likelihood LOW for OneDrive specifically, impact HIGH.**
SQLite relies on filesystem locks behaving as advertised; "some filesystems contain bugs in their locking logic... This is especially true of network filesystems and NFS in particular," and broken locks let "two or more... processes... access the same database at the same time, [so] database corruption might result." `[CITED]` https://sqlite.org/howtocorrupt.html ; "SQLite relies on exclusive locks for write operations, and those have been known to operate incorrectly for some network filesystems. This has led to database corruption." `[CITED]` https://sqlite.org/useovernet.html
Note: OneDrive is a *local-folder-then-sync* model (the app opens a real local NTFS file, OneDrive replicates it afterward), so SQLite's locks themselves work on each machine. The danger is the *replication*, not local locking — which is why (a)–(c) dominate. `[ASSUMED]`

**Official bottom line:** "SQLite is designed for situations where the data and application coexist on the same machine," and the recommended sharing path is a client/server DB (e.g. PostgreSQL) or, if SQLite must be shared, "rollback mode with exclusive one-at-a-time access only." `[CITED]` https://sqlite.org/useovernet.html
Community corroboration that Dropbox/OneDrive + SQLite is a known-bad pattern: `[CITED]` https://synopse.info/forum/viewtopic.php?id=5542 ; `[CITED]` https://forums.zotero.org/discussion/66980/dropbox-a-prompt-for-a-solution

### Concrete mitigations that fit a 2–5 person tool

1. **Use rollback-journal mode, NOT WAL — disable WAL entirely.** Set `PRAGMA journal_mode = DELETE;` (the default) so there are no long-lived `-wal`/`-shm` sidecars to sync out of band. After a clean `COMMIT` in DELETE/rollback mode the journal is deleted and the *single* `.db` file is self-contained — exactly what OneDrive can sync safely. WAL is the worst choice here; it is only safe when readers/writers share local memory. `[VERIFIED]` (sqlite.org useovernet recommends rollback mode for one-at-a-time access; WAL requires shared memory which fails cross-host).

2. **Short connections: open → do the smallest unit of work → `COMMIT` → close.** The spec already mandates this (§4). Enforce it in the repository layer: never hold a connection open across UI idle time. This (a) releases the file lock so OneDrive can sync, and (b) minimizes the mid-transaction-copy window of risk (c). `[VERIFIED]` (matches spec §4 + howtocorrupt mid-write clause).

3. **Checkpoint/finalize before yielding the file.** Because mode is DELETE, a successful close already leaves no sidecar. Verify the `-journal` file is gone after each write batch; if present, a transaction was interrupted — surface a warning rather than letting OneDrive sync a half-state. `[ASSUMED]`

4. **Advisory "one editor at a time" lock (application-level, not OS-level).** Implement a tiny sentinel: on entering an edit, write a row/marker (e.g. a `Settings` key `editing_lock = <username>@<timestamp>`) and warn other users who open the app that someone is editing. This is *advisory* — it does not prevent corruption, it prevents the human-level concurrent-edit that *causes* conflict copies. Cheap, fits the trust model of a 2–5 person team. `[ASSUMED]`

5. **Open the canonical filename consistently from every machine** (same configured DB path, no per-machine renames/links). Two processes opening the same file under different names use different journals and break crash recovery. `[CITED]` https://sqlite.org/howtocorrupt.html (multiple-links clause).

6. **Detect conflict copies on startup.** On launch, scan the DB folder for sibling files matching `*-<MACHINE>.db` / "couldn't merge" patterns and alert the user instead of silently ignoring them. Turns a silent data-loss into a visible, recoverable event. `[ASSUMED]`

7. **Cheap backups before risk windows.** Before any bulk write (smart-input apply, template seed), copy the `.db` to a local timestamped backup *while no transaction is open* (safe copy per howtocorrupt requires no in-flight write). Gives a rollback point if a conflict copy wins. `[VERIFIED]` (safe-copy-when-idle is the inverse of the mid-write corruption clause).

8. **Document the operational rule for users:** "let OneDrive finish syncing (green check) before and after editing; don't edit on two machines at once; if you see a `-COMPUTERNAME.db` file, stop and ask before deleting." This is the realistic last line of defense given real-time sync is out of scope. `[CITED]` (conflict-copy mechanism) Microsoft Support URL above.

> **Residual risk statement for the team:** Even with all mitigations, simultaneous offline edits on two machines will still produce a OneDrive conflict copy and lose one side's writes. That is inherent to the chosen architecture and matches the spec's "accepted risk." The mitigations make it *rare and visible* rather than *common and silent*.

---

## 2. Rounding integrity in smart distribution (MEDIUM)

**Risk:** 1-decimal rounding of `total / workingDays` can make the per-day parts not sum to the total (classic `10/3 = 3.333...`).

**Is "remainder to last day" correct?** Yes, the approach is sound *if implemented as integer/decimal arithmetic on tenths*, not by independently rounding each day. Correct algorithm:
- Work in tenths of an hour (integers) to avoid binary-float drift: `totalTenths = round(total * 10)`.
- `base = floor(totalTenths / nDays)` tenths for every day; `remainder = totalTenths - base * nDays` tenths goes to the **last** day.
- Convert back: each day `base/10`, last day `(base + remainder)/10`.
- Example `10h / 3`: `totalTenths=100`, `base=33` (3.3h), `remainder = 100 - 99 = 1` tenth → last day `34` (3.4h). Days = 3.3, 3.3, 3.4, sum = 10.0 exactly. `[VERIFIED]` (matches spec example §5.2 and arithmetic checks out).

This guarantees the parts sum to the total *exactly* (no float residue) because the remainder is computed from the same integer total. `[VERIFIED]`

**Do NOT** round each day with `Math.Round(total/nDays, 1)` independently and add a fudge — that path is where sums drift. `[VERIFIED]`

**Edge cases:**
- **Total that forces a day > 8h:** This is the dangerous one. `40h / 5 days = 8.0` is fine, but the *remainder dump on the last day* can push the last day over 8h even when the average is ≤8 (e.g. `39h / 5` → base 7.8, last day 7.8+0.2... small) — generally safe, but a pathological input like `total` near `8*nDays` plus the remainder can tip the last day to 8.1+. **Smart input must run the same 8h/day validation as manual edit, including the existing hours already logged on each target day.** Reject or clamp before preview. `[ASSUMED]` (the spec validation §5.2 says total/day ≤8h but doesn't state smart-input re-checks it — see Pitfall 3).
- **Zero working days in range:** range entirely on Sat/Sun, or From > To. Must guard `nDays == 0` → no-op with a clear message, not a divide-by-zero. `[VERIFIED]` (divide-by-zero is certain otherwise).
- **Single-day range:** `nDays == 1` → all hours on that one day; remainder logic degenerates to "everything on the last (=only) day." Still subject to the 8h cap. `[VERIFIED]`
- **Non-1-decimal total:** total like `7.25h` should be rejected at input (spec allows max 1 decimal), else `round(total*10)` silently changes the user's number. Validate input precision first. `[ASSUMED]`

---

## 3. 8h/day validation: inline edit vs smart-input bulk fill (MEDIUM–HIGH)

**Risk:** Smart input writes multiple cells at once; if it adds to a day that already has manual hours, the *sum* can exceed 8h even though each contribution looks valid.

**Key questions and answers:**
- **Can smart input push a day over 8h?** Yes, unless validation considers *existing* logged hours on each target day, not just the distributed amount. Mode 2 ("Full 8h") is especially dangerous: it writes 8h per day ignoring whatever is already there → any pre-existing hours on that task-day or other tasks tip the day over 8h. `[ASSUMED]` (spec §5.2 Mode 2 says "fill 8h into all working days" with no mention of summing existing logs).
- **When is validation enforced — per-cell or per-save?** The spec validates "total hours/day/user ≤ 8h → red warning, don't save" (§5.2, §7). Recommendation: enforce at **both** levels — per-cell on inline edit for immediate feedback, AND a **whole-day re-validation at save time** that sums all tasks' hours for that user+date. Smart input must validate against the *post-merge* state in the preview step (§5.2 says preview before save — make the preview the validation gate). `[ASSUMED]`
- **Risk of partial saves:** If smart input writes day-by-day and validation fails on day 4 of 5, you can end up with days 1–3 saved and 4–5 not → inconsistent state and a confusing total. **Mitigation: wrap the entire smart-input apply in a single transaction; validate ALL days first, then commit atomically (all-or-nothing).** Given short-connection + rollback-journal mode (Pitfall 1), a single `BEGIN…COMMIT` around the batch gives atomicity for free. `[VERIFIED]` (SQLite transactions are atomic; the partial-save risk is real only if writes aren't wrapped).

**Concrete rule:** Smart input computes the full distribution → for each target day, `existingDayTotal(user, date) - existingForThisTaskDay + newValue` must be ≤ 8h → if any day fails, reject the whole apply with a per-day breakdown, save nothing. `[ASSUMED]`

---

## 4. Soft-delete integrity (MEDIUM)

**Risk:** Reports/Export must resolve User and Task names even after the row is soft-deleted (`is_active = 0`). If queries filter `WHERE is_active = 1` on the *join*, soft-deleted tasks/users with existing TimeLogs render as blank/orphan rows.

**Specifics:**
- **Two different query intents must not share a filter.** "What can I log against this week?" filters `is_active = 1` (hide deleted from the Timesheet grid and dropdowns — spec §5.3/§5.4). "What did this user log?" (Reports/Export) must **NOT** filter `is_active` on the joined Task/User — it must resolve the name regardless of active state. `[VERIFIED]` (this is the central soft-delete report bug; FK rows still exist).
- **DEFAULT-request tasks soft-deleted but logs exist:** Annual Leave/Meeting seeded as Tasks under the hidden `DEFAULT` request (spec §3.3). If a DefaultTask is later hidden, historical `### DEFAULT — Annual Leave` rows must still export with the correct name. Same rule: Export joins by `task_id` without an `is_active` filter on the task. `[VERIFIED]` (matches spec §3.3 + §6.2 export format).
- **Orphan-display mitigation:** Always `INNER JOIN Tasks/Users` by id (every TimeLog has a valid FK by schema — `task_id`/`user_id` NOT NULL with FK REFERENCES), so names always resolve; never `LEFT JOIN` and never add `AND is_active=1` to report joins. `[VERIFIED]` (schema §3.2 enforces NOT NULL FKs).
- **Referential safety:** Soft-delete is set-`is_active=0`, never `DELETE`, so FK targets never vanish — but the team must resist ever hard-deleting a User/Task/Request that has TimeLogs (would orphan logs / break the NOT NULL FK). Enforce "soft-delete only" in the service layer. `[VERIFIED]` (spec §7 rule).
- **Edge: soft-deleting a Request** — spec only mentions soft-deleting tasks/users, not Requests. If a Request can be hidden, its Tasks' logs still need the request_code/project for export grouping. Resolve Request fields the same way (no `is_active` filter on report joins). `[ASSUMED]` (Requests table has no `is_active` column in §3.1 — confirm whether requests are deletable at all).

---

## 5. Date / timezone & week boundary (LOW–MEDIUM)

**Risk:** Mixed cultures across 2–5 machines; week-start ambiguity; locale-dependent date parsing producing wrong dates or `dd/MM` vs `MM/dd` swaps.

**Specifics & mitigations:**
- **Store dates as `'YYYY-MM-DD'` text (ISO-8601), always.** Schema already does (`work_date TEXT`, `created_at TEXT`). This is sortable as text and culture-neutral. `[VERIFIED]` (spec §3.2).
- **Parse/format with `CultureInfo.InvariantCulture` and an explicit format string** (`DateTime.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture)` / `ToString("yyyy-MM-dd", InvariantCulture)`). Never use `DateTime.Parse(s)` (machine-locale dependent → silent wrong dates when a `vi-VN` and `en-US` machine share the file). `[VERIFIED]` (.NET locale-sensitivity of Parse is well-established).
- **No time-of-day, no timezone component.** `work_date` is a calendar date, not an instant — do not store as UTC datetime or apply timezone conversion (would shift dates across DST/offset boundaries). Use `DateOnly` (.NET) or the date component only. `[VERIFIED]`
- **Week start consistency (Mon–Fri):** Do NOT rely on `CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek` (it's `Sunday` for `en-US`, `Monday` for many EU/`vi-VN`). Hard-code Monday as week start in week-navigation math so every machine computes the same Mon–Fri window. Compute `monday = date.AddDays(-((int)date.DayOfWeek + 6) % 7)`. `[VERIFIED]` (FirstDayOfWeek is culture-dependent; hard-coding removes the variance).
- **`created_at` for sync (see Pitfall 6):** store as ISO-8601 UTC (`yyyy-MM-ddTHH:mm:ssZ`) since it IS an instant and machines may differ — but `work_date` stays a plain date. `[ASSUMED]`

---

## 6. Concurrency on AUTOINCREMENT ids / created_at across offline machines (MEDIUM)

**Risk:** Two machines insert rows offline, then sync. Both allocate ids from their *local* copy's `sqlite_sequence`, so both may pick the same next id → on file-merge (or conflict copy), id collisions / lost rows / a `UNIQUE(user_id, task_id, work_date)` clash.

**Reality for THIS architecture:**
- Because OneDrive does **whole-file** sync (not row merge), two offline inserts don't actually merge into one table — one file *wins* and the other becomes a conflict copy (Pitfall 1a). So you don't get silent id-collision *within one file*; you get **lost inserts** in the losing file. `[VERIFIED]` (follows from OneDrive file-level conflict model).
- The `UNIQUE(user_id, task_id, work_date)` constraint is actually protective: the spec's "1 cell = 1 log, inline edit = upsert" (§3.2) means the natural key is the business identity, not the autoincrement id. Two people editing the *same* cell is a genuine last-write-wins on that natural key; two people editing *different* cells only collide if their whole-file edits race in OneDrive. `[VERIFIED]` (schema §3.2).

**Mitigations:**
- **Lean on the natural key, not the surrogate id.** Upserts keyed on `(user_id, task_id, work_date)` are idempotent and merge-friendly conceptually; the surrogate `id` is just a local handle. Never expose/compare ids across machines. `[VERIFIED]`
- **`created_at` from UTC, not local wall clock**, so ordering/audit is consistent when two machines' clocks differ (Pitfall 5). `[ASSUMED]`
- **Don't fight the architecture:** true offline-multi-writer id reconciliation needs GUID/ULID primary keys + a sync engine — explicitly out of scope (spec §9 "real-time sync"). The single-writer advisory lock (Pitfall 1, mitigation 4) is the right-sized answer: it keeps writes effectively one-at-a-time so id allocation never races. `[VERIFIED]` (spec §4, §9).
- If future-proofing is cheap: storing a `client_uuid TEXT` natural key per TimeLog row would make any eventual merge tooling possible without schema migration pain — optional, flag as YAGNI for v1. `[ASSUMED]`

---

## Priority summary (risk × likelihood × impact)

| # | Pitfall | Likelihood | Impact | Priority |
|---|---------|-----------|--------|----------|
| 1 | Shared SQLite over OneDrive (conflict copies + WAL sidecar + mid-write copy) | High | High | **CRITICAL** |
| 3 | Smart-input bulk fill bypassing 8h/day + partial saves | Medium | High | **HIGH** |
| 4 | Soft-delete report/export orphan names | Medium | Medium | MEDIUM |
| 2 | Rounding sum-integrity / day>8h / zero-day range | Medium | Medium | MEDIUM |
| 6 | Offline id/created_at concurrency | Medium | Medium | MEDIUM |
| 5 | Date/locale/week-start parsing | Low–Med | Medium | MEDIUM |

## Sources

- SQLite — How To Corrupt An SQLite Database File: https://sqlite.org/howtocorrupt.html
- SQLite — Over a Network, Caveats and Considerations: https://sqlite.org/useovernet.html
- Microsoft Support — Duplicate files in OneDrive: https://support.microsoft.com/en-us/office/duplicate-files-in-onedrive-fd47ce5e-8dd0-465e-9e3a-461e1a3cf613
- Nate Chamberlain — OneDrive "couldn't merge the changes" + computer name: https://natechamberlain.com/2017/09/20/onedrive-and-sharepoint-sync-issue-you-now-have-two-copies-of-a-file-we-couldnt-merge-the-changes-in-filename-appended-with-computer-name/
- mORMot forum — Occasional issues with SQLite dbs in OneDrive/Dropbox: https://synopse.info/forum/viewtopic.php?id=5542
- Zotero forums — Dropbox + SQLite corruption: https://forums.zotero.org/discussion/66980/dropbox-a-prompt-for-a-solution
