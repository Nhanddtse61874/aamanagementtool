# Research Synthesis — WPF Desktop Timesheet Tool

**Date:** 2026-06-21
**Mode:** B (STEP 4 — Research Synthesizer)
**Spec:** `docs/superpowers/specs/2026-06-21-timesheet-tool-design.md`
**Inputs:** STACK-RESEARCH.md · FEATURE-RESEARCH.md · ARCHITECTURE-RESEARCH.md · PITFALL-RESEARCH.md

> Tag legend carried forward from the source reports: `[VERIFIED]` = standard/confident knowledge · `[CITED]` = backed by a source URL · `[ASSUMED]` = design inference, not externally verified.
>
> Where the four agents agreed, the claim is stated once. Disagreements are explicitly resolved below. The OneDrive+SQLite shared-file risk is the dominant architectural concern and leads Section 2.

---

## 1. Consolidated Tech Decisions (Authoritative)

This is the single source of truth. All four agents converged on the stack below; where the spec text and a report diverged (e.g. DI, journal mode), the resolution is noted.

### 1.1 Target framework & project shape

- **`net8.0-windows`** TFM (NOT bare `net8.0`) — required for WPF; `<UseWPF>true</UseWPF>` only works on the Windows TFM. `[VERIFIED]`
- `<OutputType>WinExe</OutputType>` (suppresses console window), `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`. `[VERIFIED]`
- Windows-only is consistent with spec §9 (no Mac/cross-platform). `[VERIFIED]`

### 1.2 NuGet packages (pinned)

| Package | Pinned version | Purpose | Confidence |
|---|---|---|---|
| `CommunityToolkit.Mvvm` | **8.4.2** | MVVM source generators (`[ObservableProperty]`, `[RelayCommand]`, `ObservableObject`) | `[CITED]` nuget.org |
| `Dapper` | **2.1.79** | micro-ORM over ADO.NET `IDbConnection` | `[CITED]` nuget.org |
| `Microsoft.Data.Sqlite` | **8.0.x** (latest 8.0 servicing, e.g. 8.0.10) | SQLite ADO.NET provider, native bundled via SQLitePCLRaw | `[CITED]` nuget.org |
| `ClosedXML` | **0.105.0** | Excel (.xlsx) export | `[CITED]` nuget.org |
| `Microsoft.Extensions.DependencyInjection` | **8.0.x** | DI container (see 1.5 — adopted) | `[VERIFIED]` |
| Markdown export | **no package** | custom `StringBuilder` per spec §6.2 | `[VERIFIED]` |

- **Version-pin discipline:** Microsoft.Data.Sqlite latest overall is 10.0.9 but the 10.x line targets .NET 10. For a .NET 8 app, pin the **8.0.x** band so the package TFM matches the runtime. Same rule for all `Microsoft.Extensions.*`. Avoid pulling 10.x into a net8.0 app (version-skew / runtime mismatch). `[CITED]`/`[VERIFIED]`

### 1.3 SQLite provider, FK enforcement, connection lifetime

- **Provider: Microsoft.Data.Sqlite**, not System.Data.SQLite — Microsoft's actively-maintained ADO.NET provider, bundles the native binary (no separate DLL juggling), designed to back Dapper. `[CITED]`
- **FK enforcement is OFF by default in SQLite.** The schema has real FKs (Tasks→Requests, TimeLogs→Users/Tasks), so enforce per connection: set `Foreign Keys=True` in the connection string OR run `PRAGMA foreign_keys = ON;` immediately after opening every connection. Without this the schema's FKs silently protect nothing. `[VERIFIED]`
- **Connection lifetime — open-short / close-fast (spec §4).** Create a fresh `SqliteConnection` per repository operation (or per unit of work), `using` it, dispose. Never hold a long-lived static/singleton open connection. This releases the OS file lock so OneDrive can sync and minimizes the mid-transaction-copy corruption window. `[VERIFIED]`
- **`Pooling=False` recommended** for the OneDrive scenario — a pooled "closed" connection can keep the OS file handle open in the pool and fight OneDrive's sync; perf cost is negligible at 2–5 users. (`[CITED]` pooling behavior; `[ASSUMED]` that disabling helps OneDrive specifically.)
- **Wrap multi-statement writes** (DefaultTasks→Tasks sync, smart-input batch) in an explicit transaction so a partial write can't leave inconsistent rows. `[VERIFIED]`

### 1.4 Journal mode — `journal_mode=DELETE` (NOT WAL) — KEY DECISION

All four agents agree, and Stack + Pitfall both flag this as the single most important stack decision. **Set `PRAGMA journal_mode = DELETE;` (the default rollback-journal mode). Do NOT use WAL.**

Rationale `[CITED]`/`[VERIFIED]`:
- **WAL creates persistent sidecar files** (`<db>-wal`, `<db>-shm`) that must stay byte-consistent with the main `.db`. OneDrive/Teams syncs each file independently and on its own schedule, so a remote machine can receive a new `.db` without the matching `-wal`/`-shm` → "database disk image is malformed" / true corruption. The `-shm` file uses shared-memory mapping that does not work correctly across hosts. (sqlite.org/howtocorrupt, sqlite.org/useovernet)
- **DELETE keeps everything in the single `.db` file between transactions** — the `-journal` file is transient and gone after a clean COMMIT when no write is in progress. With short open/close connections, OneDrive almost always sees just the one self-contained `.db` at rest, the safest state to sync.
- sqlite.org/useovernet explicitly recommends **rollback mode with exclusive one-at-a-time access** if SQLite must be shared. WAL is the worst choice here.
- **Honest caveat (carry into plan):** SQLite-over-OneDrive is a *tolerated-risk* architecture (spec §4/§9 already accept file-level conflicts). DELETE + single-writer + short connections is the *least-bad* option, not a corruption guarantee. The real fix (if tolerance proves too low) is a tiny shared host (network share with locking / hosted SQLite) — out of v1 scope. `[ASSUMED]`

### 1.5 DI approach — Microsoft.Extensions.DependencyInjection (RESOLVED)

**Disagreement resolved:** Stack research called a DI container *optional* (`[ASSUMED]`, manual wiring acceptable); Architecture research *recommended* `Microsoft.Extensions.DependencyInjection` (`[VERIFIED]`). **Resolution: adopt `Microsoft.Extensions.DependencyInjection`** — a bare `ServiceCollection` in `App.xaml.cs` (no Generic Host, which is overkill). It gives clean lifetime control and easy test substitution at near-zero cost, and the seam (`IConnectionFactory`) is needed for testability anyway (Section 4). This is a minor, low-risk deviation that both agents consider fine; it does not violate Simplicity-First because the container replaces hand-wiring that would otherwise grow across 6 VMs + 6 repos + 6 services.

Composition (`App.xaml.cs`): register `IConnectionFactory` singleton; repositories + services as **singletons** (stateless — connection opened per method, so no held connection); ViewModels **transient**; run one-time DB bootstrap (`IDatabaseInitializer.Initialize()`) *before* showing `MainWindow`. `[ASSUMED]`

### 1.6 Schema init + migrations — `PRAGMA user_version`

- **Idempotent table creation:** run `CREATE TABLE IF NOT EXISTS …` for every table on startup, wrapped in one transaction. Microsoft.Data.Sqlite auto-creates the `.db` on first connect (`Mode=ReadWriteCreate` default). `[VERIFIED]`
- **Versioned migrations:** use SQLite's built-in **`PRAGMA user_version`** as a lightweight migration counter — no extra table, no migration library for an app this size. Read at startup; if `< target`, run pending step scripts in order inside a transaction, then set `PRAGMA user_version = <target>;`. `[VERIFIED]`
- **Additive columns** (e.g. brainstorm-added `Users.windows_username`) need `ALTER TABLE … ADD COLUMN` — SQLite has no `ADD COLUMN IF NOT EXISTS`, so guard via `PRAGMA table_info(Users)` check or gate on `user_version`. `[VERIFIED]`
- **Keep migrations forward-only and additive** — the shared file means an *old client can open a newer DB*, so additive columns with defaults keep old clients working (backward-compatible). `[ASSUMED]`

### 1.7 DEFAULT request seed (spec §3.3) — idempotent order

1. Ensure the hidden request exists: `INSERT INTO Requests(request_code, project, created_at) SELECT 'DEFAULT','DEFAULT',@now WHERE NOT EXISTS (SELECT 1 FROM Requests WHERE request_code='DEFAULT');` `[VERIFIED]`
2. Seed `DefaultTasks` rows **only if the table is empty** (so a user who renamed/hid them isn't overwritten on next launch). `[ASSUMED]`
3. Sync each active `DefaultTasks` row into a `Task` under the DEFAULT request (insert-if-missing keyed by `task_name` + DEFAULT request id). Also runs as the ongoing sync when Settings edits DefaultTasks. `[VERIFIED]`

---

## 2. Cross-Cutting Risks

### 2.1 OneDrive + shared SQLite — CRITICAL (leads everything)

Putting a live SQLite database in a OneDrive/Teams-synced folder is a corruption hazard the SQLite authors explicitly warn against. It is an *accepted-risk* pattern (spec §4/§9), but the mitigations below are **mandatory, not optional** — they make failure *rare and visible* rather than *common and silent*. `[CITED]`

**Failure modes:**
- **(a) File-level conflict copies — likelihood HIGH, impact MEDIUM.** OneDrive merges only at whole-file level; two offline edits produce a duplicate like `timesheet-DESKTOP-AB12.db`. One user's writes silently land in the conflict copy and "disappear." Most likely failure mode; recoverable only if noticed. `[CITED]`
- **(b) WAL/-shm sidecars not syncing atomically — likelihood MEDIUM, impact HIGH (true corruption).** Mitigated by choosing DELETE over WAL (§1.4). `[CITED]`
- **(c) Copying the `.db` mid-transaction — likelihood MEDIUM, impact HIGH.** OneDrive may upload while a transaction is in flight → "some old and some new content" → malformed. Short connections shrink but don't eliminate the window. `[CITED]`
- **(d) Network/locking unreliability — likelihood LOW for OneDrive, impact HIGH.** OneDrive is local-folder-then-sync, so local NTFS locks work; the danger is *replication*, not local locking — which is why (a)–(c) dominate. `[ASSUMED]`

**Agreed mitigations (mandatory):**
1. **`journal_mode=DELETE`, never WAL** (§1.4). `[VERIFIED]`
2. **Short connections** — open → smallest unit of work → COMMIT → close; enforced in the repository layer via `IConnectionFactory.Create()`-per-call. `[VERIFIED]`
3. **Verify `-journal` is gone after each write batch**; if present, a transaction was interrupted — warn rather than let OneDrive sync a half-state. `[ASSUMED]`
4. **Advisory "one editor at a time" lock** (application-level): write a `Settings` key `editing_lock = <username>@<timestamp>` on entering an edit; warn other users on open. Prevents the *human-level* concurrent edit that causes conflict copies. Advisory only. `[ASSUMED]`
5. **Consistent canonical DB path from every machine** (no per-machine renames/links — different journals break crash recovery). `[CITED]`
6. **Detect conflict copies on startup** — scan the DB folder for `*-<MACHINE>.db` siblings and alert. Turns silent data-loss into a visible event. `[ASSUMED]`
7. **Cheap backup before bulk writes** (smart-input apply, template seed) — copy the `.db` while no transaction is open. `[VERIFIED]`
8. **Document the operational rule for users:** wait for OneDrive green-check before/after editing; don't edit on two machines at once; if you see a `-COMPUTERNAME.db`, stop and ask. `[CITED]`

> **Residual-risk statement for the team:** even with all mitigations, simultaneous offline edits on two machines still produce a conflict copy and lose one side's writes. Inherent to the chosen architecture; matches the spec's accepted risk.

### 2.2 Smart-input bulk fill bypassing the 8h/day cap — HIGH

Smart input writes multiple cells at once; if it adds to a day that already has manual hours, the *sum* can exceed 8h. Mode 2 ("Full 8h") is especially dangerous — it writes 8h/day ignoring pre-existing logs. `[ASSUMED]`
- **Mitigation:** smart input must validate against the *post-merge* state in the **preview** step (spec §5.2 makes the preview the validation gate). For each target day: `existingDayTotal(user,date) − existingForThisTaskDay + newValue ≤ 8h`; if any day fails, reject the whole apply with a per-day breakdown, save nothing.
- **Atomicity:** wrap the entire smart-input apply in a single `BEGIN…COMMIT` (all-or-nothing) to avoid partial saves (days 1–3 saved, 4–5 rejected). Free with DELETE + short connections. `[VERIFIED]`

### 2.3 Soft-delete report/export orphan names — MEDIUM

Two query intents must **not** share an `is_active` filter:
- "What can I log this week?" → filters `is_active = 1` (hide deleted from grid/dropdowns). `[VERIFIED]`
- "What did this user log?" (Reports/Export) → must **NOT** filter `is_active` on the joined Task/User, or soft-deleted rows render blank. Always `INNER JOIN` by id (every TimeLog has a NOT NULL FK so names always resolve); never `LEFT JOIN`, never add `AND is_active=1` to report joins. `[VERIFIED]`
- Applies to DEFAULT-request tasks too (hidden Annual Leave must still export with its name). `[VERIFIED]`
- **Service-layer rule: soft-delete only, never hard-DELETE** a User/Task/Request that has TimeLogs (would orphan logs / break NOT NULL FK). `[VERIFIED]`
- **Flag:** Requests have no `is_active` column in §3.1 — confirm whether Requests are deletable at all (see Open Question 6). `[ASSUMED]`

### 2.4 Rounding sum-integrity & zero-day range — MEDIUM

- Use **integer-tenths arithmetic** for hour distribution (§4.1) — guarantees parts sum to total exactly, no float drift. Do NOT independently `Math.Round` each day. `[VERIFIED]`
- **Guard `nDays == 0`** (range entirely Sat/Sun, or From > To) → no-op with a clear message, never divide-by-zero. `[VERIFIED]`
- **Validate input precision** — reject totals with >1 decimal before `round(total*10)` silently changes the user's number. `[ASSUMED]`

### 2.5 Date / locale / week-start — MEDIUM (mixed cultures across machines)

- **Store dates as `'YYYY-MM-DD'` TEXT (ISO-8601), always** — sortable as text, culture-neutral; lexicographic compare == chronological. `[VERIFIED]`
- **Parse/format with `CultureInfo.InvariantCulture` + explicit format** (`ParseExact`/`ToString("yyyy-MM-dd", InvariantCulture)`). Never `DateTime.Parse(s)` (machine-locale dependent → silent wrong dates when a `vi-VN` and `en-US` machine share the file). `[VERIFIED]`
- **No time-of-day / timezone on `work_date`** — it's a calendar date; use `DateOnly`. `[VERIFIED]`
- **Hard-code Monday as week start** — do NOT use `CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek` (Sunday for en-US, Monday elsewhere). `monday = date.AddDays(-((int)date.DayOfWeek + 6) % 7)`. `[VERIFIED]`
- **`created_at` as ISO-8601 UTC** (it IS an instant); `work_date` stays a plain date. `[ASSUMED]`

### 2.6 Offline id / created_at concurrency — MEDIUM

- OneDrive whole-file sync means offline inserts don't merge into one table — one file wins, the other becomes a conflict copy (so **lost inserts**, not silent id-collision within one file). `[VERIFIED]`
- **Lean on the natural key** `UNIQUE(user_id, task_id, work_date)`, not the surrogate `id`. Upserts on the natural key are idempotent and merge-friendly; never expose/compare surrogate ids across machines. `[VERIFIED]`
- The single-writer advisory lock (§2.1 mitigation 4) keeps writes one-at-a-time so id allocation never races. True offline-multi-writer reconciliation (GUID/ULID + sync engine) is explicitly out of scope (spec §9). `[VERIFIED]`

---

## 3. Open Questions / Decisions Needed From User

Every flagged item across the four reports, each with a recommended default. These need a user call before/at spec lock.

| # | Question | Recommended default |
|---|---|---|
| 1 | **DEFAULT header label conflict.** Markdown export example shows `### DEFAULT — Annual Leave`, but the §3.3 seed sets `project='DEFAULT'`, which would render `### DEFAULT — DEFAULT`. The example implies a per-task-name label (DEFAULT entries sub-grouped by task name). | Sub-group DEFAULT entries by **task name** so the header right-hand side reads the task ("Annual Leave"), matching the spec example. (Real requests keep `### {code} — {project}`.) |
| 2 | **8h validation scope: per-cell vs per-save.** Spec says "total hours/day/user ≤ 8h." Is the cap a per-**column-total** (all task rows that day) check, or per-cell? | **Per-day column total**, enforced at **both** levels — per-cell immediate red feedback AND a whole-day re-validation at save that sums all tasks for that user+date. Owner VM owns the aggregate check. |
| 3 | **Smart-input: overwrite vs accumulate existing hours.** Does applying smart input *set/overwrite* a target cell or *add to* its existing value? | **Overwrite (set)**, per `UNIQUE(user_id,task_id,work_date)` = "1 cell = 1 log, inline edit = upsert" (spec §3.2/§5.2). Validate the post-merge day total against 8h in preview. |
| 4 | **Does "today" count in the N-working-day "chưa log" window?** `LastNWorkingDays(today, n)` currently includes today; policy could be "logged by yesterday." | **Include today** in the window (start at `today`). Simpler and matches the literal algorithm; if the team prefers "by yesterday," start at `today.AddDays(-1)`. |
| 5 | **"x ngày" banner number.** Report the configured window **N**, or the *actual* gap since the user's last log? | Report **N** (configured window) when zero logs in-window — simplest. Optionally compute the real gap via `MAX(work_date)` per user if UX wants precision. |
| 6 | **Are Requests deletable?** §3.1 Requests has no `is_active` column; spec mentions soft-deleting only Tasks/Users. | **Requests are not soft-deletable in v1** (no `is_active` on Requests). If they must be, add `is_active` and apply the same "no `is_active` filter on report joins" rule. |
| 7 | **DefaultTask rename ambiguity.** Renaming a DefaultTask is indistinguishable from delete-old + add-new under the name-match sync. | v1 treats an unmatched old name as **soft-delete + new insert** (TimeLogs preserved). Acceptable; flag for UAT. |
| 8 | **Hours storage precision (REAL vs INTEGER tenths).** DB stores `hours REAL`; Dapper `decimal↔REAL` mapping can drift. | Store **REAL**, but **round to 1 decimal in `TimeLogService` before upsert** and compare with tolerance in tests. Switch to INTEGER tenths only if exactness bites. |

---

## 4. Implementation-Ready Notes

### 4.1 Key algorithms (pure, no WPF — high unit-test ROI)

**Integer-tenths even distribution (spec `10h/3 → 3.3, 3.3, 3.4`).** Lives in `SmartInputService`. `[VERIFIED]` arithmetic.
```
totalTenths = round(total * 10)            // 10.0 -> 100
baseTenths  = totalTenths / nDays          // floor: 100/3 = 33  (3.3h)
remainder   = totalTenths % nDays          // 1
each day    = baseTenths / 10.0            // 3.3
last day   += remainder / 10.0             // 3.3 + 0.1 = 3.4
```
Guarantees parts sum to total exactly (no float residue) — assert as a property-based test. Edge cases: exact divide (`9/3 → 3.0,3.0,3.0`), single day (`5.5/1 → [5.5]`), large remainder (`10/7 → six×1.4 + last 1.6`). Guard `nDays==0` and >1-decimal input. Remainder always lands on the last *working* day (weekends pre-excluded).

**Working-day enumeration** — culture-independent `DayOfWeek.Saturday/Sunday` checks:
```
WorkingDays(from,to): iterate from→to, skip Sat/Sun.
Full8h(nDays): Enumerable.Repeat(8.0, nDays).
```

**LastNWorkingDays(today, n)** — walk backward from `today`, skip weekends, collect until `count==n` (e.g. today=Mon, n=3 → [Mon, Fri, Thu]). The window is contiguous earliest→today, so detection is a single range query:
```sql
SELECT DISTINCT user_id FROM TimeLogs
WHERE work_date >= @earliestWorkingDay AND work_date <= @today;
```
Active users not in that set are flagged. (Whether `today` is included = Open Question 4.)

**Upsert (natural key `UNIQUE(user_id, task_id, work_date)`):**
```sql
INSERT INTO TimeLogs(user_id, task_id, work_date, hours, created_at)
VALUES(@u,@t,@d,@h,@now)
ON CONFLICT(user_id, task_id, work_date) DO UPDATE SET hours = excluded.hours;
```
Supported by SQLite 3.24+ (bundled). Empty cell → `DeleteAsync(user,task,date)`. `[VERIFIED]`

**Report projection** — one flat SQL join, `GroupBy` in C# (cleaner than 4 round-trips). ISO date string `>=`/`<=`/`BETWEEN` compare is valid:
```sql
SELECT r.project, r.request_code, t.task_name, l.work_date, l.hours
FROM TimeLogs l
JOIN Tasks t    ON t.id = l.task_id
JOIN Requests r ON r.id = t.request_id
WHERE l.user_id = @userId AND l.work_date >= @from AND l.work_date <= @to
ORDER BY r.project, r.request_code, t.order_index, l.work_date;
```
(No `is_active` filter on these report joins — §2.3.)

**Markdown export** — `# Timesheet — {yyyy}/{MM}` → `## {user}` → `### {request_code} — {project}` → `| Date | Task | Hours |`. Format hours as integer when whole (`4` not `4.0`), else 1 decimal (`3.5`). Escape `|` in task names (`| → \|`). Pure `StringBuilder`. `[VERIFIED]`/`[CITED]`

### 4.2 Layer seams (condensed)

| Layer | Owns | Must NOT touch |
|---|---|---|
| **Repository** (Dapper) | SQL strings, Dapper calls, `IDbConnection` lifetime (short, via `IConnectionFactory`), row↔model mapping, upsert/soft-delete SQL | business rules, validation, WPF types |
| **Service** | validation (8h cap, >0, ≤1 decimal, weekday-only), distribution math, seeding/sync, export build, current-user *policy* | `IDbConnection`/Dapper, `MessageBox`/`SaveFileDialog`, `DataGrid` |
| **ViewModel** | `INotifyPropertyChanged`, `ICommand`, week-grid shaping, calling services, surfacing errors, owning dialogs (`SelectUserDialog`, `SaveFileDialog`) | SQL, file-format details, business math |
| **View (XAML)** | layout, bindings, dialogs | logic of any kind |

**Core boundary rule:** services return results/throw typed exceptions; ViewModels translate to UI state. No service references `System.Windows.*`.

**Critical seams:**
- `IConnectionFactory.Create()` per call — the seam that *enforces* short connections AND enables integration tests against a temp/`:memory:` SQLite file. `SqliteConnectionFactory` depends on `IAppConfig.DatabasePath`.
- **`ExportService` returns `byte[]`/`string`**, never opens `SaveFileDialog` — keeps it WPF-free and unit-testable (`XLWorkbook.SaveAs(MemoryStream).ToArray()`).
- **`SaveCellAsync` must read the day's other logs before validating** the 8h cap — validation is not purely in-memory.
- **`CurrentUserService` returns an outcome enum** (`Resolved` / `NeedsSelection`), never opens the dialog — the VM owns `SelectUserDialog` and calls back to `SetWindowsUsernameAsync` to persist the mapping.
- **Two responsibilities for DEFAULT:** `DatabaseInitializer` (Data layer, schema + seed, once at startup) vs `DefaultTaskSyncService` (Service layer, ongoing reconcile from Settings edits — match by `task_name`, add/soft-delete/rename+reorder).
- **Settings locality:** DB **path** → app-local config (`%APPDATA%\TimesheetApp\appsettings.json`, chicken-and-egg); N-days warning + TaskTemplates + DefaultTasks + the windows_username *identity* → shared DB.
- **Inject `Func<DateOnly>`/`IClock`** into `TimeLogService` and `SmartInputService` for deterministic "today"/week-window tests.

### 4.3 UI specifics (condensed)

- **Weekly grid:** fixed Mon–Fri row VM (`TimesheetRowVm : ObservableObject`, 5 nullable `double?` day properties, `null = empty = 0h`), static XAML columns — NOT dynamic columns. Sat/Sun simply don't exist as columns (cleanest "Chỉ Mon–Fri" enforcement). `Math.Round(v, 1, MidpointRounding.AwayFromZero)` for the 1-decimal rule. `[ASSUMED]`/`[VERIFIED]`
- **Column headers** bind via `RelativeSource` to the DataGrid's DataContext (columns aren't in the row DataContext) — show concrete dates (`"Tue 17/06"`), recomputed on Prev/Next-week nav.
- **Footer totals:** native WPF DataGrid has no summary row — use a separate single-row `Grid`/`UniformGrid` below, bound to `MonTotal..FriTotal` on an INPC source.
- **8h block:** `SaveCommand.CanExecute` returns false when any column total > 8; call `NotifyCanExecuteChanged()` in the `DayChanged` handler. Add `INotifyDataErrorInfo` for red cell borders.
- **Reports drill-down:** `TreeView` + `HierarchicalDataTemplate` keyed by `DataType` (Project→Request→Task→Date), record-based VM nodes. Grouped DataGrid (`CollectionViewSource`) for the flat weekly/monthly summary tables.

### 4.4 Test strategy (condensed)

- **Unit (no I/O):** `SmartInputService` (distribution, weekend-skip, sum-back invariant), `TimeLogService` validation (8h cap with mocked repos), `ExportService` formatting (markdown text / .xlsx bytes), `DefaultTaskSyncService` reconcile (mock repos).
- **Integration (temp SQLite):** each repository's CRUD/upsert, `DefaultTaskSyncService` end-to-end, `DatabaseInitializer` idempotency (run twice, assert no dup DEFAULT request/tasks).
- **Manual/UI:** `SelectUserDialog`, week-grid inline-edit red warning, conflict-copy startup detection.
