# B1-auth — Evidence: password_hash NULL on every migrated user

Scope: gather facts only. No proposal, no recommendation-as-decision. All claims carry `file:line` and a
`[VERIFIED]`/`[ASSUMED]` tag. Read-only pass — no build/test/app run, no database touched.

---

## 1. Who can log into the WEB app TODAY, and who cannot

### 1a. The login mechanism (web)

`POST /api/auth/login` (`src/TimesheetApp.Api/Auth/AuthSetup.cs:157-211`, `MapAuthMechanism`) requires, in order:

1. `GetCredentialsAsync(username)` finds a row by the `username` column — unknown username → 401
   (`AuthSetup.cs:162-167`). **[VERIFIED]**
2. `creds.IsActive` must be true — a soft-deleted user is refused even if a hash exists
   (`AuthSetup.cs:170-171`). **[VERIFIED]**
3. `creds.PasswordHash` must be **non-null/non-empty** — `AuthSetup.cs:176-177`:
   > "A NULL password_hash means 'has never had a password set' => CANNOT LOG IN. Never treat it as 'any
   > password matches': that is an authentication bypass, and it is exactly the state every user is in on a
   > freshly migrated database (v10 added the column empty)."
   **[VERIFIED]**
4. Only then is the candidate password verified via `PasswordHasher.VerifyHashedPassword`
   (`AuthSetup.cs:179-181`). **[VERIFIED]**

There is no "first login sets your password" flow, no magic link, no email-based reset, no SSO — password
only. **[VERIFIED — absence]** (no other `MapPost("/api/auth/...")` route exists besides `login`, `logout`
in `AuthSetup.cs` and `set-password` / `users/{id}/set-password` in `AuthEndpoints.cs`; confirmed by reading
both files in full.)

### 1b. What every migrated user's row looks like

Schema migration v10 (`src/TimesheetApp.Core/Data/DatabaseInitializer.cs:320-337`) is the step that:
- Adds `password_hash TEXT` (no default → **NULL** on every existing row) (`DatabaseInitializer.cs:331`).
- Adds `is_admin INTEGER NOT NULL DEFAULT 0`, then immediately runs
  `UPDATE Users SET is_admin = 1 WHERE id = (SELECT MIN(id) FROM Users);` (`DatabaseInitializer.cs:336-337`)
  — **exactly one** user (the lowest `id`) becomes admin; the hash column is untouched by this statement,
  so that admin is *also* `password_hash IS NULL` immediately after migration.
- Renames `windows_username` → `username` (values preserved) (`DatabaseInitializer.cs:330`).

**[VERIFIED]** Net effect of v10 alone: every single user row, admin or not, has `password_hash = NULL`.
Per `AuthSetup.cs:176-177`'s fail-closed check, **that means literally nobody can log into the web app**
the moment the API first starts against a migrated database — until something writes a hash.

Note: the schema is not frozen at v10. `SchemaVersion = 11` (`DatabaseInitializer.cs:15`), and step v11
(`DatabaseInitializer.cs:353-356`) adds a `UNIQUE ... COLLATE NOCASE` index on `username` (case-insensitive
uniqueness, closing a duplicate-username hole). This does not touch `password_hash`. **[VERIFIED]**

### 1c. The one exception — `AdminBootstrap`, and its actual reach

`AdminBootstrap.EnsureAdminPasswordAsync` (`src/TimesheetApp.Api/Auth/AdminBootstrap.cs:41-127`) runs at
every API startup (`src/TimesheetApp.Api/Program.cs:181-184`, inside the one-time startup block). For a
**non-empty** database (the real, migrated-from-WPF case) it takes the `admins.Count > 0` branch
(`AdminBootstrap.cs:49`, `87-124`):

- It loops over every user with `is_admin = 1` (v10 guarantees exactly one, unless someone manually
  edited the column since).
- For that one admin, if `password_hash` is empty, it generates a random 24-char password
  (`AdminBootstrap.cs:203-209`, CSPRNG, ~142 bits), atomically claims the NULL slot
  (`UserRepository.TryBootstrapAdminPasswordAsync`, `WHERE password_hash IS NULL`,
  `src/TimesheetApp.Core/Data/Repositories/UserRepository.cs:235-243`), and **logs it once** at
  `LogWarning` level (`AdminBootstrap.cs:116-123`):
  > "ADMIN PASSWORD GENERATED (shown ONCE — copy it now) ... Log in and change it immediately."
- The class header states explicitly: **"There is no secret to leak, because it exists only in that one
  log line."** (`AdminBootstrap.cs:16`) — it is not persisted anywhere, not in config, not recoverable.
  If the operator does not capture that specific startup's log output, the only documented recovery is to
  null the `password_hash` column directly and let bootstrap re-run (`AdminBootstrap.cs:115`, "re-run
  bootstrap by nulling the column").

**[VERIFIED]** This path touches **only the one admin row**. Every non-admin user — i.e., the entire rest
of a real company's user base — is untouched by `AdminBootstrap` and stays at `password_hash = NULL`
indefinitely. Confirmed by reading the full loop (`AdminBootstrap.cs:87-124`): it iterates `admins`, a list
filtered to `u.IsAdmin` (`AdminBootstrap.cs:47`), never the full user list.

Test evidence that this exact scenario is covered end-to-end (existing admin, no hash, gets a working
single-use console password):
`src/TimesheetApp.ApiTests/AdminBootstrapTests.cs:22-49`
(`The_generated_admin_password_is_announced_once_and_actually_logs_in`), and idempotence/no-overwrite-on-
restart at `AdminBootstrapTests.cs:55-82`. **[VERIFIED]**

There is **no equivalent bootstrap for non-admin users** anywhere in the codebase. **[VERIFIED — absence,
searched `AdminBootstrap.cs`, `Program.cs`, and grepped the whole `TimesheetApp.Api` tree for
`SeedFirstAdmin`/bootstrap-shaped code; nothing else touches `password_hash` at startup.]**

### 1d. Bottom line — today, on a real migrated company database

- **Can log in via web:** at most one person — the lowest-`id` user (promoted to admin by v10) — and only
  if whoever was watching the API's console/log output the moment it first started against the migrated DB
  actually copied the one-time generated password. If that was missed, **zero people** can log in via any
  documented path short of direct database surgery.
- **Cannot log in via web:** every other user — the entire rest of the company, unconditionally, until an
  admin (the one person above, once they are in) manually sets a password for each of them one at a time
  (§2).

### 1e. Contrast — who can log in via WPF today (for scale of the gap)

WPF does not use `password_hash` or any password at all. `CurrentUserService.ResolveAsync`
(`src/TimesheetApp.Core/Services/CurrentUserService.cs:25-37`) maps `Environment.UserName` (the OS login
name) straight to a `Users.username` row via `GetByUsernameAsync` — no credential check of any kind.

If no row matches, `MainViewModel.ResolveCurrentUserAsync`
(`src/TimesheetApp/ViewModels/MainViewModel.cs:268-287`) **auto-provisions a brand-new user** named after
the Windows account, with no admin approval and no picker (comment at `MainViewModel.cs:277-280`:
"self-service onboarding"), then `InitializeActiveTeamAsync` auto-joins them to the lowest-id team
(`MainViewModel.cs:224-240`, `AddMemberAsync`). **[VERIFIED]**

So today: **anyone who can run the WPF exe under any Windows account is in, immediately, auto-provisioned
if needed.** The web requires a password that (per §1d) essentially nobody currently has. This is the size
of the gap M10 (deleting WPF) closes off — WPF is currently the *only* practically-working front door for
the whole user base, and the org's frictionless self-service onboarding path (auto-provision-on-launch)
does not exist on the web at all — the web's only user-creation path is the admin-only three-step flow
(§3b).

---

## 2. The admin-side password-setting path — what exists, and what is tested

### 2a. Backend route

`POST /api/auth/users/{id:int}/set-password` (`src/TimesheetApp.Api/Endpoints/AuthEndpoints.cs:116-138`):
- `RequireAuthorization(AuthSetup.AdminPolicy)` — admin-only (`AuthEndpoints.cs:131`).
- Takes **no current password** — deliberate, an admin resetting someone else's password does not know it
  (`AuthEndpoints.cs:20-24`, class doc).
- 404s on an unknown target id before writing, so a no-op (zero rows touched) is distinguishable from
  success (`AuthEndpoints.cs:110-115,124-126`).
- Bump-only write, `IUserRepository.SetPasswordHashAsync` (`UserRepository.cs:202-208`) — no
  `expectedVersion`, cannot 409.
- **[VERIFIED]**

Sibling self-service route `POST /api/auth/set-password` (`AuthEndpoints.cs:53-108`) exists and requires
the caller's **current** password — which, per §1b, no migrated user has, so self-service is structurally
incapable of getting anyone past the NULL-hash state. The code itself says so
(`AuthEndpoints.cs:87-93`, 400 "No password is set for this account. Ask an administrator to set one.").
Confirmed the same way by test doc (`AuthEndpointsTests.cs:296-298`): "The admin reset is the ONLY way out
of the NULL-hash state." **[VERIFIED]**

### 2b. Test coverage — backend

`src/TimesheetApp.ApiTests/AuthEndpointsTests.cs` (389 lines) covers, with real HTTP round-trips through a
live `ApiFactory` host: self set-password happy path + wrong current password + missing current password +
NULL-hash rejection + IDOR-smuggling resistance (lines 88-267); admin set-password happy path
(`AuthEndpointsTests.cs:275-294`), **admin reset of a never-logged-in (NULL hash) user**
(`AuthEndpointsTests.cs:300-327` — this is the exact production scenario), non-admin 403
(`AuthEndpointsTests.cs:330-347`), anonymous 401 (`AuthEndpointsTests.cs:350-360`), unknown-id 404
(`AuthEndpointsTests.cs:363-372`), empty-password 400 (`AuthEndpointsTests.cs:375-387`). **[VERIFIED —
thoroughly tested at the API layer.]**

### 2c. Frontend UI — what is actually wired

The Angular admin Users screen (`src/timesheet-web/src/app/pages/users/`) has a per-row **"Password"**
button (`users.component.html:114`, `171-183`) that calls `adminSetPassword` with no current-password
prompt (`users.component.ts:273-284`, `openPassword`/`savePassword`). This is a genuine, tested UI path:
`src/timesheet-web/src/app/pages/users/users.component.spec.ts:265-273` drives the exact button and asserts
the underlying API call. **[VERIFIED]**

`createUserFully` (`src/timesheet-web/src/app/pages/users/user-create.ts:97-123`) chains **create → set
username → set password** as one flow for brand-new accounts, with an explicit, persistent on-screen
warning if any step fails partway (`user-create.ts:65-74`, `STEP_MESSAGE`), because
`POST /api/users` alone produces a login-incapable "ghost" account (`user-create.ts:6-21` doc,
`SettingsEndpoints.cs:459-460` server-side comment: "A created user cannot log in until an admin also sets
a username ... and a password"). Tested at `user-create.spec.ts` and `users.component.spec.ts:50-150`
(create-flow assertions). **[VERIFIED]**

**Gap found:** The self-service route (`POST /api/auth/set-password`) has a generated TypeScript client
function (`src/timesheet-web/src/app/api/fn/auth/auth-set-password.ts`), but grepping the entire
`timesheet-web/src/app` tree for any component or service that calls it found **nothing** — no "change my
password" screen exists anywhere in the SPA (checked `worklog.service.ts`, every `pages/*`, and the login
page). A user who has been given a password by an admin has no UI path to change it themselves afterward;
only another admin reset can change it. **[VERIFIED — absence.]** This does not block the NULL-hash
bootstrap problem itself (self-service could never have solved that — §2a), but it is a real, currently
un-wired capability gap worth the human's awareness for the "everyone logged in" sequence design.

**Second gap found — no way to SEE who still needs a password:** `UserDto`
(`src/TimesheetApp.Api/Contracts/Dtos.cs:61-62`) is built from the `User` model
(`src/TimesheetApp.Core/Models/Entities.cs:21`), which carries `Id, Name, WindowsUsername, IsActive,
RowVersion, IsAdmin` — **no password-set indicator**. `password_hash` lives only on the separate
`UserCredentials` record (`Entities.cs:33`), returned only by `GetCredentialsAsync(username)`
(`UserRepository.cs:190-199`), which is never called by any `/api/users*` route. So `GET /api/users/all`
(the admin Users screen's data source) cannot tell the admin which of N migrated users still have a NULL
hash — every row looks the same regardless of password state. The Angular component's own `canLogIn`
helper (`users.component.ts:121-122`) checks only `username !== ''`, **not** password state — so on a
freshly migrated database, every existing user (who already has a `username` from the v10 rename) is shown
by this UI logic as though they *can* log in, when in fact none of them can (§1). **[VERIFIED — this is a
real mismatch between the UI's own "can log in" signal and actual login capability post-migration; not an
[ASSUMED] inference, the code paths were read directly.]**

---

## 3. `SeedFirstAdmin` — what it does and when it fires

### 3a. The gate

`TimesheetApp:SeedFirstAdmin` (config key, read via `IConfiguration`, `AdminBootstrap.cs:36-37`):
`!bool.TryParse(...) || enabled` — **defaults to TRUE** when unset or unparseable. Set it to the literal
string `"false"` to suppress first-admin seeding entirely. **[VERIFIED]**

No committed `appsettings.json`/`appsettings.Production.json` exists inside
`src/TimesheetApp.Api/` in this repo (searched; none found) — so the effective value in any real deployment
comes from whatever config source is layered in at runtime (environment variable, command line, or a
deployed-but-gitignored `appsettings.json`); this evidence pass could not determine what the *actual
running* production instance currently has this set to, and per the task's hard rule the real
`%APPDATA%\TimesheetApp\appsettings.json` was not opened. **[ASSUMED default = true unless someone
deliberately set it otherwise; not independently confirmed for the live deployment.]**

### 3b. What it does, precisely — two different branches depending on database state

Invoked once per API process start, `Program.cs:181-184`, **after** `IDatabaseInitializer.InitializeAsync()`
(migrations) and **after** the first `ITeamBootstrapService.EnsureBootstrappedAsync()` call
(`Program.cs:176-179`).

- **Empty database (`Users` has zero rows):** `SeedFirstAdminAsync` (`AdminBootstrap.cs:155-201`) creates
  ONE user (default `admin`/`admin`, overridable via `TimesheetApp:BootstrapAdminUsername` /
  `:BootstrapAdminPassword`), makes them admin, claims the password slot, and logs the credentials once
  with a "THIS IS THE DEFAULT PASSWORD, CHANGE IT" warning if defaults were used
  (`AdminBootstrap.cs:184-198`). This is **not** the real company scenario (a real deployment migrating
  from WPF already has users) but is what a brand-new/dev database gets. **[VERIFIED]**
- **Non-empty database with ≥1 admin already (the real migrated-from-WPF case):** takes the loop described
  in §1c — generates and logs a one-time password **only for admins whose hash is still NULL**, skips
  anyone who already has a hash (`AdminBootstrap.cs:97-99`, "already has a password — nothing to do, and we
  must never overwrite it"). **[VERIFIED]**
- **Non-empty database with users but ZERO admins** (should not happen given v10's `MIN(id)` promotion,
  but the code guards it anyway): logs a warning and does nothing —
  `"NONE has is_admin = 1, so nobody can reach the admin-only routes. Set is_admin = 1 on the intended
  administrator's row."` (`AdminBootstrap.cs:76-82`) — no automatic recovery path; this is a stated
  manual-DB-edit case. **[VERIFIED]**
- **If `TimesheetApp:SeedFirstAdmin=false` AND the database is empty:** logs a warning that nobody can log
  in and returns `false` with no seeding (`AdminBootstrap.cs:65-72`). This flag has **no effect** on the
  non-empty-database branches above — it only gates the empty-database first-user creation
  (`AdminBootstrap.cs:31-35` doc: "Set ... to suppress SeedFirstAdminAsync entirely" — that method only,
  not the whole class). **[VERIFIED]** So on a real migrated company DB, `SeedFirstAdmin=false` would
  **not** prevent the one existing admin from getting a one-time generated password — it only matters for a
  from-scratch install.

### 3c. Ordering hazard this class exists to close

If the seed just created a brand-new admin (empty-DB branch only), `Program.cs:186-208` explicitly re-runs
`teamBootstrap.EnsureBootstrappedAsync()` a second time, because the first team-bootstrap pass ran while
`Users` was still empty and could not have joined the not-yet-existing admin to any team
(`Program.cs:188-206`, full inline comment explaining the hazard). This second call is **only reached when
`seededFirstAdmin == true`** (`Program.cs:186`) — i.e., only in the empty-database first-run case, never in
the "existing admin, generate a password" branch, because that branch's return value is always `false`
(`AdminBootstrap.cs:126`, the `foreach` loop's fall-through). **[VERIFIED]** This matters for §4: it means
the self-healing re-join exists for the synthetic empty-DB admin, but not for anyone touched by the
"existing admin gets a password" branch — though that is moot for them specifically, since (§4 below) any
user who existed at first migration was already sepped into a team by that same first pass.

---

## 4. Bulk / scripted provisioning path

Searched the whole `src` tree (case-insensitive) for `bulk`, `csv`, `import`, `provision` in any
user/auth-adjacent context, and for any `scripts/` directory with tooling relevance. Findings:
- The word "bulk" appears only in unrelated contexts — bulk **database writes** (backup-before-bulk-write,
  `DbBackupHelper.cs`), never bulk **user/credential** provisioning.
- No CLI tool, no admin script, no CSV-import endpoint, no `scripts/` directory contains anything
  provisioning-related.
- The only two ways `password_hash` is ever written anywhere in the codebase are:
  `UserRepository.SetPasswordHashAsync` (one user, one call, admin-route or self-route) and
  `UserRepository.TryBootstrapAdminPasswordAsync` (admin-bootstrap only, one row, `WHERE ... IS NULL`
  atomic claim). Both are single-row. **[VERIFIED — absence, exhaustive grep across `src/TimesheetApp.Api`
  and `src/TimesheetApp.Core` for `password_hash` write sites confirms exactly these two methods.]**

**Conclusion: there is no bulk or scripted provisioning path today.** Getting N users from NULL to a
working password is, today, N individual `POST /api/auth/users/{id}/set-password` calls — in practice, N
clicks through the Users admin screen's "Password" button, one user at a time, by whoever holds the one
admin account.

---

## 5. A user in zero teams

`ITeamBootstrapService.EnsureBootstrappedAsync` (`src/TimesheetApp.Core/Services/TeamBootstrapService.cs`)
runs on every API startup (`Program.cs:178-179`) and is what puts users into a team at all:
- First run on a DB with existing business data (the real migrated-from-WPF case):
  `MigrateExistingDbAsync` → creates team **"Architect Improvement"** → `BackfillTeamAsync` joins **every
  user that exists in the `Users` table at that moment** via
  `INSERT OR IGNORE INTO UserTeams(user_id, team_id) SELECT id, @t FROM Users;`
  (`TeamBootstrapService.cs:66-70,101-113`). **[VERIFIED]** So every user who existed in the database
  *before* this first post-M10 API startup is already a team member, independent of whether they have a
  password yet — team membership and password are orthogonal, unrelated states.
- The backfill is idempotent and self-healing (`WHERE team_id IS NULL` / `INSERT OR IGNORE`) and re-runs on
  every subsequent startup, so it also catches anyone who slipped through an interrupted prior run
  (`TeamBootstrapService.cs:44-57`, `85-127`). **[VERIFIED]**

Who is **not** covered by that first sweep, and so genuinely lands in zero teams:
1. **A user created after that first bootstrap run**, via the web Users admin screen. `POST /api/users`
   (`SettingsEndpoints.cs:461-476`) does not touch `UserTeams` at all — confirmed by reading the full
   handler body (insert row + notify, nothing else) — and `createUserFully`
   (`user-create.ts:97-123`, the only orchestrated create path) is explicitly three steps (create,
   username, password) with **no team step**. Team membership for a newly created user is a wholly
   separate, admin-only action: `PUT /api/teams/{id}/members`
   (`SettingsEndpoints.cs:304-316`, `SetMembersCheckedAsync`, a bulk-replace of the member set, editable
   from a different screen). Nothing in the create-user flow links to or reminds the admin of this step.
   **[VERIFIED]**
2. **A first-admin seeded onto a genuinely empty database** (§3a empty-DB branch) — self-healed by the
   explicit second `EnsureBootstrappedAsync()` call in `Program.cs:186-208`, as covered in §3c. **[VERIFIED,
   and explicitly regression-tested:** `AdminBootstrapSeedTests.cs:74-102`,
   `The_seeded_admin_is_a_member_of_a_team`, asserts non-empty `MemberTeamIds`, `ActiveTeamId > 0`, and a
   real successful `POST /api/backlogs` for the seeded admin.**]**

### What zero-team membership actually does to a logged-in user

`IClientContext.MemberTeamIds` (`Infrastructure/IClientContext.cs:31-32`) is populated from
`ITeamRepository.GetTeamIdsForUserAsync` per request. A user in no team has an empty array, and
`ApiCurrentTeamService` resolves `ActiveTeamId = 0` for them
(comment at `BacklogEndpoints.cs:89-90`). Concretely:
- `POST /api/backlogs` refuses outright with a 400 and a user-facing message:
  `"You are not a member of any team, so this backlog would be invisible to everyone. Ask an admin to add
  you to a team."` (`src/TimesheetApp.Api/Endpoints/BacklogEndpoints.cs:98-101`). **[VERIFIED]**
- Every team-scoped **read** (`GET /api/backlogs`, Task List, etc.) filters on
  `team_id IN (ctx.MemberTeamIds)`, which is empty, so these endpoints return nothing — not an error, just
  silently empty lists (confirmed pattern at `TimesheetEndpoints.cs:406-438`,
  `BacklogEndpoints.cs:559-582`, both intersecting client-requested team ids with the empty
  `MemberTeamIds`). **[VERIFIED]**
- Net effect, stated directly in the regression-test doc comment
  (`AdminBootstrapSeedTests.cs:70-73`): such a user "could log in to an app that did nothing" — they
  authenticate successfully, and every team-scoped screen (Backlog, Task List, Reports scoped to a team)
  renders empty or refuses writes. **[VERIFIED via the cited code paths, not merely the comment.]**

---

## 6. Summary table of what is/is not automated today

| Step | Automated? | Mechanism | Reaches |
|---|---|---|---|
| Give the ONE promoted admin a working password | Yes, on next API startup | `AdminBootstrap.EnsureAdminPasswordAsync` (`AdminBootstrap.cs:41-127`) | Exactly 1 user; password shown once in server logs only |
| Give any OTHER (non-admin) user a password | No | none — admin must call `POST /api/auth/users/{id}/set-password` per user, one at a time (UI: Users screen "Password" button) | N/A |
| Join a NEW user (created after first boot) to a team | No | separate admin action, `PUT /api/teams/{id}/members`, different screen, not part of user-create flow | N/A |
| Join a PRE-EXISTING (migrated) user to a team | Yes, already done | `TeamBootstrapService` first-run backfill, ran once at/soon after the DB was first opened post-migration | Every user who existed at that time |
| Self-service "change my own password" (after an admin sets one) | No UI, backend route exists and is tested | `POST /api/auth/set-password` has no calling component anywhere in `timesheet-web` | N/A |
| See which users still lack a password, in the admin UI | No | `UserDto`/`GET /api/users/all` do not project password state at all; the UI's own `canLogIn` check only looks at `username`, not `password_hash` | N/A |
| Bulk/scripted provisioning of many users at once | Does not exist | — | — |

All rows independently verified by reading the cited source; none are inferred from documentation comments
alone without also tracing the code path they describe.
