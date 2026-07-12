# M8 Backend Foundation — Design

**Date:** 2026-07-12
**Milestone:** M8 — Migrate WPF → Web (ASP.NET Core 8 + Angular)
**Slice:** M8.1 (Core extraction) + M8.2 (API host, DB, concurrency) + M8.3 (Auth)
**Status:** Approved (pending spec review)
**Inputs:** `.planning/M8-FEATURE-INVENTORY.md` (as-built inventory of the WPF app)

---

## 1. Context

TimesheetApp is a WPF (.NET 8, MVVM, Dapper + SQLite) internal tool. It is being migrated to a web app (ASP.NET Core 8 API + Angular SPA) because the WPF UI is buggy and hard to use. The WPF project will be deleted at the end of M8.

Two findings from the codebase survey drive this design:

1. **The business layer is already portable.** Of 50 files in `Services/`, exactly **one** (`ThemeService.cs`) references WPF. `Data/` (29 files) and `Models/` (3 files) are 100% WPF-free. `IConnectionFactory.Create() → IDbConnection` abstracts the database; repositories never see `SqliteConnection`. Packages (Dapper, Microsoft.Data.Sqlite, MS.Extensions.DI, ClosedXML) are all cross-platform.
   → The migration is mostly **wrapping an API around business logic that already exists**, not rewriting it.

2. **The data layer is architected around SQLite-on-OneDrive.** `SqliteConnectionFactory` sets `Pooling=false` and `PRAGMA journal_mode=DELETE`, and there is a whole scaffold of OneDrive defences: conflict-copy detection (XC-08), journal-gone checks (XC-09), backup-before-every-bulk-write (XC-10).
   → On a server these are obsolete. WAL + pooling become available.

## 2. Goals

- `TimesheetApp.Core` (net8.0) exists; WPF and the new API both consume it; **548 existing tests stay green**.
- A running ASP.NET Core 8 API over that Core.
- **Concurrent updates never silently overwrite each other.**
- Users log in with username + password, and stay logged in across browser restarts.
- Three destructive operations are admin-only.

## 3. Non-goals (this slice)

- Any Angular UI beyond what is needed to prove auth works. Screens land in M8.4–M8.8.
- Deleting the WPF project (M8.10).
- Moving export/backup/retention to server-side storage (M8.9).
- Password reset, email, MFA, account lockout, self-registration. **YAGNI** for a 10–50 person internal tool.

## 4. Decisions (locked)

| # | Decision | Rationale |
|---|---|---|
| D1 | **Hosting:** on-prem, internal network, IIS | User requirement |
| D2 | **Scale:** 10–50 users, multi-team | User requirement |
| D3 | **DB: keep SQLite**, server-hosted, WAL + pooling — **explicitly as an interim database** | There is no Azure/AWS subscription available, so a managed DB is not an option today. This is workable rather than merely tolerable: the API is the **only writer process**, so N concurrent users ≠ N concurrent writers, and write contention is solved by architecture rather than by engine. It keeps the 14 repositories, the schema, the migrations and the 548 tests intact. Because the replacement is *anticipated, not hypothetical*, the SQLite-specific surface is enumerated and contained in **§13** rather than left to be rediscovered later. |
| D4 | **Concurrency: optimistic (`row_version` + HTTP 409) + SignalR** | The DB engine does **not** solve lost updates — no engine does. Today there is no `version`/`updated_at` column on any of the 16 tables, and Task List commits every inline edit as a bare `UPDATE`, so two users editing the same card silently overwrite each other. This is fixed in the application layer. |
| D5 | **Auth: username + password, cookie session, on the existing `Users` table** | No Active Directory available, so Windows Auth is off the table. ASP.NET Core **Identity** is rejected: it drags in EF Core (the app is Dapper-only) and creates a second user table (`AspNetUsers`) alongside `Users`. |
| D6 | **Cookie, not JWT** | "Remember me" is one flag (`IsPersistent`) with cookies vs. a refresh-token rotation scheme with JWT. Cookies are `HttpOnly` (immune to XSS token theft); a JWT in `localStorage` is not. Angular is served same-origin, so there is no cross-origin reason to prefer JWT. |
| D7 | **Authorization: a single `is_admin` boolean**, gating exactly 3 endpoints | User explicitly does not want edit-level permissions. But Run-retention / Restore-backup / Deactivate-team are **destructive**, not merely privileged, and today *any* user can trigger them. One column and one policy is near-zero cost. |

## 5. Solution structure

```
src/TimesheetApp.sln
├── TimesheetApp.Core/     net8.0            ← NEW
│     Services/   49 files  (all except ThemeService + IThemeService)
│     Data/       29 files  (14 repositories + IConnectionFactory + DatabaseInitializer)
│     Models/      3 files
│     Config/     IAppConfig (interface only)
├── TimesheetApp.Api/      net8.0            ← NEW (ASP.NET Core 8)
├── TimesheetApp/          net8.0-windows    ← WPF: ViewModels, Views, ThemeService,
│                                               file-based AppConfig → references Core
└── TimesheetApp.Tests/                      → references Core
```

### 5.1 The `IAppConfig` seam

`IAppConfig` currently reads `%APPDATA%\TimesheetApp\appsettings.json`. Core keeps **only the interface**. WPF keeps the file-based implementation unchanged. The API supplies an implementation backed by ASP.NET configuration. No service among the 49 changes.

### 5.2 Connection policy is per-host

Core is shared by WPF (SQLite on OneDrive) and the API (SQLite on a server) — and these need **opposite** settings. `SqliteConnectionFactory` therefore takes options:

| Setting | WPF profile (today) | API profile (server) |
|---|---|---|
| `Pooling` | `false` | **`true`** |
| `journal_mode` | `DELETE` | **`WAL`** |
| `busy_timeout` | — | **`5000`** |
| `synchronous` | default | **`NORMAL`** |
| `foreign_keys` | `ON` | `ON` |

The OneDrive scaffolding (XC-08 conflict copies, XC-09 journal checks, XC-10 backup-before-bulk-write) **stays in Core** while WPF still runs, and is deleted in M8.10.

### 5.3 Acceptance gate for M8.1

`dotnet test` → **548/548 green**, and the WPF app still launches and works. If not, stop; do not proceed to M8.2.

## 6. Schema v10

One migration covers both concurrency and auth.

### 6.1 Optimistic concurrency

Add `row_version INTEGER NOT NULL DEFAULT 1` — **selectively**, not everywhere:

| Table | `row_version`? | Reason |
|---|---|---|
| `Backlogs` | ✅ | Highest risk. Task List lets **multiple people** edit the same card inline (PCT, Type, PCA, both deadlines, progress, tags). |
| `Tasks` | ✅ | Type / assignee / status edited inline. |
| `StandupIssues` | ✅ | **Deliberately collaborative** — issues are not owner-gated (DR-04). |
| `Users`, `Teams`, `Tags`, `PcaContacts` | ✅ | Admin-edited; low frequency but cheap to include. |
| **`TimeLogs`** | ✅ | See §6.1.1 — an earlier draft excluded this, on the argument that the natural key `(user_id, task_id, work_date)` scopes each row to one user and nobody logs hours on another user's behalf. That argument rests on a *current behaviour* (Log Work's team view is read-only), not on an invariant. If a manager or admin ever edits someone else's timesheet, the collision becomes real and the design would have to be reopened after the endpoints and the Angular grid already exist. Include it. |
| `Holidays`, `Settings` | ❌ | Key-value / date-keyed. Overwrite is the correct semantics. |
| `StandupEntries` | ❌ | Owner-gated in the service layer: only the owner can add/update/delete/reorder their own entries. Unlike `TimeLogs`, this is enforced in code (`StandupService`), not merely absent from the UI. |

Mechanism for a plain update:

```sql
UPDATE Backlogs
   SET …, row_version = row_version + 1
 WHERE id = @id AND row_version = @expected;
```

`rowsAffected == 0` → `ConcurrencyConflictException` → middleware → **`409 Conflict`**, with the current server-side state in the response body.

Angular contract on 409: show *"Someone else just changed this."* with **[See their change]** / **[Overwrite with mine]**. Never resolve silently in either direction.

### 6.1.1 `TimeLogs`: concurrency on an upsert

`TimeLogs` is written through an upsert, not a plain update, so the rule needs one more case. A timesheet cell has three states, and the client says which one it believes it is looking at:

| Client sends | Row on server | Result |
|---|---|---|
| `expectedVersion = null` ("cell is empty") | absent | **INSERT**, `row_version = 1` |
| `expectedVersion = null` | **present** | **409** — someone filled this cell while you were looking at it |
| `expectedVersion = N` | present, version `N` | **UPDATE**, `row_version = N + 1` |
| `expectedVersion = N` | present, version `≠ N` | **409** |

`GET /api/timelogs/week` therefore returns each cell's `row_version` (`null` for an empty cell), and `PUT /api/timelogs/cell` echoes it back. Clearing a cell (which is a `DELETE`, not a write of `0` — the codebase treats empty and zero as semantically distinct) is version-checked the same way.

**Smart Fill is a deliberate carve-out.** It is an *explicit bulk overwrite*: the user previews the exact cells and hours, confirms, and the service already re-validates server-side at apply time. Overwriting is the stated intent of the operation, so its writes are not version-checked per cell. What protects it is the re-validation that already exists (`ValidateSmartFillAsync` re-runs against live data inside the apply transaction), which will reject the batch if the day would exceed 8h given whatever other users have since written. Making Smart Fill version-check every cell would mean a preview could go stale between Preview and Confirm and fail for reasons the user cannot act on.

### 6.2 Auth columns

```sql
ALTER TABLE Users RENAME COLUMN windows_username TO username;  -- values preserved
ALTER TABLE Users ADD COLUMN password_hash TEXT;
ALTER TABLE Users ADD COLUMN is_admin INTEGER NOT NULL DEFAULT 0;
```

`windows_username` already holds bare usernames (`nhan`, `chi.le`, …). Renaming the column means **no data migration** and people log in with the name they already use. No account is orphaned.

`password_hash` is nullable: existing rows have no password until an admin sets one (§8.2).

**Blast radius of the rename.** `windows_username` appears in 5 SQL statements in `UserRepository`, plus `IUserRepository.GetByWindowsUsernameAsync` / `SetWindowsUsernameAsync`. Because Core is shared, renaming the column and its repository members updates **both** WPF and the API in one move — WPF keeps working, since its `CurrentUserService` maps `Environment.UserName` to whatever that column is now called. Rename the repository members to `GetByUsernameAsync` / `SetUsernameAsync` in the same change; leaving `Windows` in the name of a method that no longer has anything to do with Windows is how stale vocabulary calcifies (the codebase already carries one such scar: files named `Requests*` that contain classes named `Backlogs*`, three migrations after the rename).

## 7. API

### 7.1 Shape

Thin controllers over the existing Core services. No business logic in controllers — Core already holds it, and it is covered by the 548 tests.

Error contract:

| Condition | Status |
|---|---|
| Validation failure (`TimeLogService` rules, etc.) | `400` + message |
| Not authenticated | `401` |
| Authenticated but not admin (3 endpoints) | `403` |
| Optimistic-concurrency conflict | **`409`** + current server state |

### 7.2 SignalR replaces `DataChangedMessage`

The WPF app uses `WeakReferenceMessenger` to broadcast `DataChangedMessage(DataKind)` — in-process only, so two users never see each other's changes without reloading.

- Hub at `/hubs/data`, clients joined to a **group per team** (this preserves the existing R6 no-leak rule).
- After every successful mutation, broadcast `DataChanged(DataKind, teamId)`.
- `DataKind` keeps all 11 values (`Backlogs, Tasks, Users, Logs, Templates, DefaultTasks, Standup, Tags, PcaContacts, Holidays, Teams`); the listener table in the inventory (§0.3) ports 1:1.
- Angular invalidates the matching queries.

## 8. Auth

### 8.1 Mechanism

Password hashing uses `PasswordHasher<User>` taken **standalone** from `Microsoft.AspNetCore.Identity` (PBKDF2) — the hasher class only, without the Identity stack, its schema, or EF Core.

```csharp
services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(o => {
            o.ExpireTimeSpan    = TimeSpan.FromDays(30);
            o.SlidingExpiration = true;
            o.Cookie.HttpOnly   = true;
            o.Cookie.SameSite   = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;   // → dev must also run HTTPS, see §10
        });
services.AddAuthorization(o => {
    o.FallbackPolicy = o.DefaultPolicy;                       // every endpoint requires auth…
    o.AddPolicy("Admin", p => p.RequireClaim("is_admin", "1"));
});
```

`POST /api/auth/login { username, password, rememberMe }` → verify hash → `SignInAsync` with `IsPersistent = rememberMe`. That flag **is** the "stay logged in" mechanism; nothing else is needed.

`POST /api/auth/logout`, `GET /api/auth/me` complete the surface.

`CurrentUserService` is **not modified**. It already takes its identity through a `Func<string>` seam:

```csharp
// WPF  (unchanged)
new CurrentUserService(users, () => Environment.UserName)
// API
new CurrentUserService(users, () => ctx.HttpContext!.User.Identity!.Name!)
```

### 8.2 Setting initial passwords

The existing Users screen already has *Add user*. Extend it with a password field, and add a *Set password* action for existing rows. An admin sets the initial password and passes it to the person. No email, no reset flow, no self-registration.

**Bootstrap (closes a chicken-and-egg).** Migration v10 promotes the first user (lowest `id`) to `is_admin = 1`, so the system is never left with zero admins. But that user has no `password_hash` either, so nobody could log in and nobody could set one.

Resolution: on startup, if the designated admin's `password_hash IS NULL`, the API hashes a bootstrap password read from configuration (`Bootstrap:AdminPassword`, supplied via environment variable or `appsettings.Production.json` by whoever deploys) and writes it in. The admin logs in with it, changes it, then sets everyone else's password.

Constraints:
- The bootstrap password is applied **only** when `password_hash IS NULL` — it can never overwrite a real password, so leaving the setting in place is harmless but pointless.
- If `Bootstrap:AdminPassword` is absent and no user has a password, the API **fails to start** with an explicit message. It must never fall back to a hardcoded default, and it must never start in a state where nobody can log in.
- Users other than the bootstrap admin get their password from the admin. There is deliberately **no** "first login claims the account" flow: on a trusted-but-shared internal network, that would let anyone claim a colleague's account before they do.

### 8.3 Admin-only endpoints

Exactly three, chosen because they are **destructive**, not merely privileged:

| Endpoint | Damage if mis-clicked |
|---|---|
| `POST /api/ops/retention/run` | Permanently deletes all timesheet/standup/task data older than N months |
| `POST /api/ops/backup/restore` | Overwrites the entire database — everyone loses work since the backup |
| `PATCH /api/teams/{id}/deactivate` | The team disappears from every screen |

Everything else — logging hours, editing backlogs, standup, task list, reports — is open to any authenticated user, matching today's behaviour.

## 9. Testing

- **548 existing tests** move to Core and must stay **100% green**. This is the real safety net for the extraction, not a formality.
- New: optimistic-concurrency tests (two concurrent updates → exactly one gets a conflict), WAL behaviour under concurrent writers, auth (login / bad password / persistent cookie / admin policy denies non-admin), API integration tests via `WebApplicationFactory`.

## 10. Known friction — the dev-time cookie trap

Two settings from §8.1 will silently break local development unless both are handled up front. Both fail the same way: a 401 with no error, which reads like a bug in the auth code rather than a transport problem.

1. **Cross-origin.** Angular's dev server runs on `:4200` and the API on its own port. The browser will not attach the auth cookie across origins. → Use Angular CLI `proxy.conf.json` to proxy `/api` and `/hubs` to the backend, so the browser sees one origin.
2. **`SecurePolicy = Always`** means the cookie is only ever sent over HTTPS. Serving the dev API over plain HTTP would therefore drop it on every request. → Run the dev API over **HTTPS** (`dotnet dev-certs https --trust`), the ASP.NET default. Do **not** weaken the cookie policy per-environment to work around this: an insecure-cookie path that exists only in dev is exactly the kind of thing that leaks into production config.

Also: SignalR must be proxied alongside `/api` (WebSocket upgrade), or the hub will silently fall back to long-polling — or fail outright.

## 11. Bugs found during survey — fix in this slice, do not port

All three are fixed **in Core**, not at their current call sites. That distinction is the whole point: two of them exist *because* business logic leaked into a WPF ViewModel, where the web client cannot reuse it and would be free to reinvent the same mistake.

| Bug | Detail | Where the fix lands |
|---|---|---|
| **Two Smart Fill implementations** | `SmartInputService` is DI-registered and injected into `TimesheetViewModel` — and then **never used**. The live logic is `SmartInputPanelVm.BuildPlan`, in a ViewModel. The live one does **not** exclude holidays when building the preview, while `ValidateSmartFillAsync` **does** — so a range containing a holiday renders a cell that then fails validation and blocks Apply, with no way for the user to act on the error. | Delete `BuildPlan`; make the already-holiday-aware `SmartInputService` **the** implementation, and have both WPF and the API call it. One source of truth for the arithmetic, covered by tests. |
| **`DAYS LOGGED` is always `N / N`** | `span = WeeklyRows.Count`, but `WeeklyRows` only contains days that *have* logs — so numerator and denominator move together and the stat can never read `3 / 5`. The denominator should be the number of **working days in the week** (excluding weekends and holidays). | The computation lives in `ReportsViewModel` today. **Move it into `ReportAggregator`** (Core) — it is business arithmetic, not presentation — and fix it there, so the Angular Reports screen (M8.7) inherits the correct version instead of re-deriving it. |
| **`LastNWorkingDays` ignores holidays** | It excludes weekends but not holidays, contradicting `WorkingDayCalculator`, which excludes both. So the "hasn't logged in N days" banner counts a public holiday against people. | `TimeLogService` (already Core) — delegate to `IWorkingDayCalculator` instead of re-implementing the day walk. |

## 12. Dead code to drop during extraction

| Item | Status |
|---|---|
| `SelectUserDialog` | Wired in `App.xaml.cs` but `selectUser` is never invoked (auto-provision replaced it). The dialog can never appear. |
| `ISmartInputService` / `SmartInputService` | See §11 — resolve by making this the single implementation, not by deleting it. |
| `TimesheetViewModel.SaveCommand` | No Save button exists (auto-save replaced it). Retained only for tests. |

## 13. Porting surface — when SQLite is replaced

SQLite is an **interim** database (D3), so the cost of leaving it is a number we should know, not discover. Surveyed rather than guessed; the whole surface is 5 constructs across ~15 call sites:

| SQLite-specific | Where | SQL Server equivalent |
|---|---|---|
| `ON CONFLICT (…) DO UPDATE` | `TimeLogRepository:132`, `HolidayRepository:52` | `MERGE` (Postgres keeps `ON CONFLICT`) |
| `INSERT OR REPLACE` | `SettingsRepository:23` | `MERGE` |
| `INSERT OR IGNORE` | `TeamRepository:112` | `IF NOT EXISTS` / `MERGE` |
| `last_insert_rowid()` | 10 call sites across 9 repositories | `SCOPE_IDENTITY()` (Postgres: `RETURNING id`) |
| `PRAGMA user_version` | `DatabaseInitializer` (3 uses) | a `SchemaVersion` table, seeded once from the current value |
| `INTEGER PRIMARY KEY AUTOINCREMENT`, `REAL` | DDL in `DatabaseInitializer` | `INT IDENTITY`, `FLOAT`/`DECIMAL` |

**What is *not* on this list matters more than what is.** There are **no** SQLite date functions anywhere — no `strftime`, no `julianday`, no `datetime()`. All dates are computed in C# and stored as ISO `TEXT` (`yyyy-MM-dd` / `yyyy-MM-ddTHH:mm:ssZ`). Dialect-specific date arithmetic is normally the single most expensive thing to port, and this codebase never took the dependency. There is also no `LIMIT`/`TOP` (no paging exists — a separate problem, but a portable one).

**Deliberately not abstracted now.** No `ISqlDialect` layer, no query builder. Fifteen enumerated call sites behind an already-abstract `IConnectionFactory` is a bounded piece of work when the day comes; building the abstraction today would be speculative generality, and we would be guessing at the target engine's shape before knowing what it is. What this slice owes the future is (a) this list stays accurate, and (b) **no new SQLite-only construct is introduced** in the v10 work without being added to it.

## 15. Sequencing after this slice

| Slice | Content | Needs UI design? |
|---|---|---|
| M8.4 | Angular shell + Log Work (week grid, Smart fill) | ✅ |
| M8.5 | Backlog + Task List (cards, tags, holidays, Gantt, continue) | ✅ |
| M8.6 | Daily Report (Input + Board) | ✅ |
| M8.7 | Reports | ✅ |
| M8.8 | Admin (Users + Settings) | ✅ |
| M8.9 | Export / Backup / Retention → server-side | ⚠️ minimal |
| M8.10 | Delete the WPF project; drop the OneDrive scaffolding from Core | ❌ |
