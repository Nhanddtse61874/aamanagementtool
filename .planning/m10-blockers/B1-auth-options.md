# B1 — Auth cutover options

**Blocker:** deleting `src/TimesheetApp/` removes the only entry to the system that does not
require a web password, and on a real migrated database **no non-admin user has one**.

**Status:** decision required from a human. This document makes the trade-offs visible; it does
not resolve them.

---

## 1. Verification of the prior evidence pass

I re-read every cited line. **Nine of ten findings confirmed. One is materially wrong**, and three
facts the prior pass did not surface change the shape of the problem more than anything in its list.

### Confirmed

| # | Claim | Verdict |
|---|---|---|
| 1 | v10 adds `password_hash` with no default and promotes `MIN(id)` to admin without setting a hash | [VERIFIED] `DatabaseInitializer.cs:331`, `:337` |
| 1 | Login fails closed on a NULL/empty hash | [VERIFIED] `AuthSetup.cs:176-177` — `Results.Unauthorized()` |
| 2 | `AdminBootstrap` runs every startup; only touches `IsAdmin` users; password printed once, never persisted | [VERIFIED] `AdminBootstrap.cs:47`, `:87-124`; `Program.cs:181-184` |
| 3 | No bulk/scripted provisioning path exists; one admin HTTP call per user | [VERIFIED] `AuthEndpoints.cs:116-138`; `users.component.ts:273-290` |
| 4 | Self-service `POST /api/auth/set-password` exists, is unreachable from the UI, and cannot bootstrap a NULL hash | [VERIFIED] `AuthEndpoints.cs:53-108`, 400s at `:91-93`. Grep for `authSetPassword` in `src/timesheet-web/src/app` returns only `api/` generated-client files — no component or service caller |
| 5 | Admin cannot see who lacks a password; `canLogIn()` checks username only | [VERIFIED] `Dtos.cs:61-62` (`UserDto` has no password field); `users.component.ts:122` — `(u.username ?? '') !== ''`. Grep for `hasPassword`/`HasPassword`/`has_password` across `src`: **zero matches** |
| 6 | A zero-team user authenticates but every team-scoped write is refused | [VERIFIED] and independently corroborated by `Program.cs:197-201`, which describes the same failure verbatim |
| 7 | `SeedFirstAdmin` defaults true, gates only the empty-DB branch | [VERIFIED] `AdminBootstrap.cs:36-37`, `:65-72`; no flag check in the `:87-124` loop |
| 8 | WPF login has no password: OS username → `Users` row, auto-provisions on no match | [VERIFIED] `CurrentUserService.cs:25-37`; `MainViewModel.cs:268-287` |
| 9 | Schema is at v11, not v10; v11 adds a UNIQUE NOCASE username index | [VERIFIED] `DatabaseInitializer.cs:353-356` |

### ❌ Corrected — finding 5 (team backfill) is wrong in a way that matters

The prior pass says the team backfill was a **"one-time"** sweep and therefore *"Only users created
AFTER that first startup land in zero teams."*

**That is not what the code does.** `TeamBootstrapService.EnsureBootstrappedAsync` takes the
`existing is not null` branch on every startup and **still calls the backfill**:

```
// TeamBootstrapService.cs:50-56
var existing = (await _teams.GetAllAsync()).OrderBy(t => t.Id).FirstOrDefault();
if (existing is not null)
{
    await BackfillTeamAsync(existing.Id, backupFirst: false);
    return;
```

and `BackfillTeamAsync` runs `INSERT OR IGNORE INTO UserTeams(user_id, team_id) SELECT id, @t FROM Users;`
(`:111-113`) plus `UPDATE Users SET active_team_id = @t ... WHERE active_team_id = 0` (`:124-126`).
The class doc calls this out explicitly: *"Idempotent + self-healing: when a team already exists it
still re-runs the (no-op-when-done) backfill"* (`:14-16`).

**Consequence for the cutover:** a user created through the web Users screen is in zero teams only
**until the next API restart**, at which point they are silently auto-joined to the **lowest-id
team**. That is better than the prior pass claimed in one way (the zero-team dead end self-heals)
and worse in another: in a multi-team company, restarting the API **auto-joins every teamless user
to whichever team has the lowest id**, which nobody chose. [VERIFIED]

### Three facts the prior pass missed — these dominate the decision

**(a) 🔴 The brief's premise — "WPF still available as the fallback" — is something the project
already tells you not to do.** `start-web.bat:16-18`, in the repo root:

```
REM  🔴 CLOSE THE WPF APP FIRST. Both write to the same SQLite file. SQLite will
REM     lock correctly and you will not lose data -- but two writers is exactly
REM     the thing this migration exists to stop.
```

And the two apps do not merely share a file, they **disagree about how to journal it**.
`SqliteConnectionFactory` ships two profiles that set **opposite** persistent journal modes on
every connection open:

```
// SqliteConnectionFactory.cs:65-66  (Server — the API)
pragmaSql = "PRAGMA journal_mode=WAL; ... busy_timeout=1000; synchronous=NORMAL;";
// SqliteConnectionFactory.cs:72     (Desktop — WPF, the default profile)
pragmaSql = "PRAGMA journal_mode=DELETE; PRAGMA foreign_keys=ON;";
```

The `Desktop` profile's stated reason (`:15-18`) is *"journal_mode=DELETE (NOT WAL -> no -wal/-shm
sidecars to sync out of band over OneDrive), Pooling=False (Dispose truly releases the file handle
so OneDrive can upload)"*. So the WPF profile exists **specifically** to keep the database safe on a
synced folder, and the API profile does the exact thing that profile exists to prevent.

[ASSUMED, and this is the load-bearing assumption in this document] While the API holds pooled
connections open, WPF's `PRAGMA journal_mode=DELETE` cannot take effect — SQLite requires exclusive
access to leave WAL mode, and the pragma fails **silently**, returning the current mode rather than
raising. I did not execute anything to confirm this (Rule 1), and it is standard SQLite behaviour
rather than something this repo asserts. **If it holds, running both apps concurrently leaves the
production database in WAL mode with `-wal`/`-shm` sidecars beside it.** Whether that is dangerous
depends entirely on where `DbPath` points — see the open question below.

**(b) 🔴 The API points at the same database as WPF by default, and nothing in the repo pins where
that is.** `Program.cs:29-30`: *"In production all three fall back to the desktop app's own
app-local locations, so the API reads the same database the WPF app already uses."* The path comes
from `%APPDATA%\TimesheetApp\appsettings.json` → `JsonAppConfig.cs:57`, defaulting to
`Documents\TimesheetApp\timesheet.db` (`:178-182`). `Program.cs:52-53` warns that this is
*"invisible"* and prints it at startup for exactly this reason.

**(c) 🔴 There is no deployment. `deploy-local.bat` is localhost-only by design**, and the comment
names this as an open decision, not an oversight (`:12-14`):

```
REM  To let OTHER machines on the LAN reach it, change the --urls at the bottom to
REM  http://0.0.0.0:5080  and open Windows Firewall for inbound TCP 5080. That is the deferred
REM  "who hosts it" decision, so this script defaults to localhost only.
```

**No option below is executable until that decision is made.** A cutover procedure for a company
cannot run against a server only one machine can reach. This is a prerequisite, not a step.

### One more risk worth naming

The admin password-reset route is a full account-takeover primitive that trusts a **claim frozen
into a persistent cookie**, with no DB-fresh admin re-check: `AuthEndpoints.cs:131` is
`.RequireAuthorization(AuthSetup.AdminPolicy)` and the handler (`:116-130`) never re-reads
`is_admin`. The cookie is minted `IsPersistent = true` (`AuthSetup.cs:203`). The M10 coverage audit
already flags this at line 97. Demoting an admin does **not** revoke their ability to reset anyone's
password until their cookie expires. [VERIFIED]

---

## 2. Who can log into the web app today

| Population | Can log in? | Why |
|---|---|---|
| Every migrated non-admin user | **No** | `password_hash` is NULL → `AuthSetup.cs:176-177` returns 401 |
| The one v10-promoted admin | **Only if someone still has the console line** | `AdminBootstrap.cs:116-123` printed it once, to stdout, on the first API start. Not persisted anywhere (`:114-115`) |
| A user created via the web Users screen | **Yes** | The 3-step create flow ends in `adminSetPassword` (`user-create.ts:115`), and `addReady` (`users.component.ts:167-172`) refuses to create without a password |
| Anyone at all, via WPF | **Yes, with no credential** | `CurrentUserService.cs:25-37` |

**The recovery hatch if the console password was lost** is named in the source
(`AdminBootstrap.cs:114-115`): *"if the operator misses it, they re-run bootstrap by nulling the
column."* That means a **hand-written UPDATE against the production database** —
`UPDATE Users SET password_hash = NULL WHERE id = <admin>;` — followed by an API restart. It works
because `TryBootstrapAdminPasswordAsync` matches on `WHERE password_hash IS NULL`
(`UserRepository.cs:239-240`). It is also the single most dangerous keystroke in this whole
blocker, and no option below can avoid needing it if the console line is gone.

---

## 3. Prerequisites — true for every option

These are not part of any option. Nothing works without them.

- **P0. Decide who hosts the API** and change `deploy-local.bat:38` to `--urls http://0.0.0.0:5080`
  + open inbound TCP 5080. Until then the web app is reachable from one machine.
- **P1. Read the startup banner** (`Program.cs:57-60`) and record which `.db` the API actually
  opened. Do this before anything else touches it.
- **P2. Take a backup by a path that is not the app.** `TeamBootstrapService` takes one only on the
  legacy-migration branch (`:69`, `backupFirst: true`); the every-startup branch passes
  `backupFirst: false` (`:55`). Do not assume startup backs anything up.
- **P3. Establish that an admin can log in** — either from the captured console password, or via the
  null-the-column hatch above. Every option's first action is an admin-authenticated HTTP call.

---

## 4. The WPF-overlap decision — separate, and it is the data-safety one

The brief asks for a sequence *"with WPF still available as the fallback until the last person is
through."* That is a coherent thing to want and it is in direct tension with `start-web.bat:16-18`.
It is its own decision, and it applies to whichever option is chosen.

| | **W-1. Flag day** | **W-2. Overlap** |
|---|---|---|
| Procedure | Everyone off WPF at one announced moment; API starts after | Both live; users move over individually |
| Concurrency risk | None — one writer at a time | Two writers; journal-mode conflict per (a) above |
| If a user can't get in | They are stuck until an admin reaches them | They reopen WPF and keep working |
| Data-safety cost | None | **Depends on where `DbPath` points.** Local disk: SQLite locking holds and `start-web.bat` itself says *"you will not lose data"*. Synced/OneDrive folder: WAL sidecars sync out of band — the corruption mode `SqliteConnectionFactory.cs:15-18` exists to prevent |
| Reversible? | Yes — restore backup, restart WPF | A corrupted synced DB is recoverable **only** from P2's backup |

**I could not determine which case applies.** `DbPath` lives in `%APPDATA%\TimesheetApp\appsettings.json`,
which Rule 1 forbids me from opening. The presence of `SqliteMaintenance.FindConflictCopies`
(*"scan the DB folder for OneDrive conflict-copy siblings"*, `SqliteMaintenance.cs:5`) and the
`Desktop` profile's OneDrive-specific design are **circumstantial evidence that the deployed
database sits on a synced folder**, but neither is proof about the current production value.

**Read the P1 banner first. If the path is under OneDrive/SharePoint/any synced root, W-2 is not a
convenience trade — it is a corruption risk against the company's live data, and the honest answer
is W-1 (or: move the database to local disk first, which is its own piece of work).**

I searched for an application-level read-only WPF mode that would make overlap safe. There is none:
the ~20 `ReadOnly` matches under `src/TimesheetApp/` are per-control `IsReadOnly` XAML bindings, and
the connection open mode is hard-coded `SqliteOpenMode.ReadWriteCreate` for **both** profiles
(`SqliteConnectionFactory.cs:53`). A read-only WPF fallback would have to be built. [VERIFIED]

---

## 5. Options

### Option A — Procedure only, ship no code

**What gets built:** nothing. Run the cutover with what exists.

1. P0–P3 above.
2. Admin logs into the web app and changes the bootstrap password immediately
   (`users.component.ts:265-290`, the per-row **Password** button — the self-service route is not
   wired, so an admin changes their own password through the admin route on their own row).
3. Admin opens the Users screen and, **row by row**, sets a password for every user
   (`POST /api/auth/users/{id}/set-password`).
4. Passwords are distributed out of band — one message per person.
5. Track who has logged in on **paper or a spreadsheet**; the app cannot tell you
   (`Dtos.cs:61-62`).
6. At the agreed cutoff, uninstall WPF and delete `src/TimesheetApp/`.

**Cost:** zero code, zero regen. Operationally *N* admin round-trips (open editor → type → save →
message the person), plus a manually maintained tracking list.

**What could go wrong:**
- Step 5 is the real failure mode. The Users screen shows **every** migrated user as able to log in
  (`canLogIn` at `users.component.ts:122` checks username only), so the UI actively contradicts the
  tracking list. Deleting WPF while trusting the screen strands whoever was missed behind a 401
  with **no way back in** — the exact outcome the M10 audit predicts at line 200.
- Admin-chosen passwords travel over chat/email and there is no forced rotation: the self-service
  change-password route has no UI (`AuthEndpoints.cs:53-108` unreachable), so whatever the admin
  typed **is the user's permanent password** until an admin changes it again.
- Every reset in step 3 runs through the not-DB-fresh admin gate (`AuthEndpoints.cs:131`).

**Undo cost:** nothing to revert in code. Distributed passwords cannot be un-distributed. If WPF is
already deleted, recovery is `git revert` + rebuild + redeploy the desktop app to every machine.

---

### Option B — Make the gap visible first, then run Option A's procedure

**What gets built:** a read-only truth signal. No new write route, no new attack surface.

- `src/TimesheetApp.Core/Data/Repositories/UserRepository.cs` — project password presence in
  `GetAllAsync` (`:42`): add `password_hash IS NOT NULL AND password_hash <> '' AS has_password`.
  **Never project the hash itself.**
- `src/TimesheetApp.Core/Models/Entities.cs` — add `bool HasPassword` to `User`. ⚠️ This is the
  expensive part: `User` is constructed positionally across Core, the API, WPF and the test suite
  (`UserRepository.cs:256` maps it positionally). Add it **last with a default** so existing call
  sites keep compiling.
- `src/TimesheetApp.Api/Contracts/Dtos.cs:61-62` — add `bool HasPassword` to `UserDto`, and the
  corresponding `ToDto()` mapping.
- Regenerate the TypeScript client from the OpenAPI document (`Program.cs:161-167` — the document
  is the generator's input, not documentation).
- `src/timesheet-web/src/app/pages/users/users.component.ts:122` — `canLogIn` becomes
  `(u.username ?? '') !== '' && u.hasPassword === true`; add a "**N users cannot log in yet**"
  banner and a filter to that list.
- Optionally wire the already-built, already-tested `POST /api/auth/set-password` into a
  "change my password" screen so admin-issued passwords are temporary rather than permanent.

Then run Option A's procedure — but step 5 is now the application telling you the truth, and the
cutover gate becomes **"the banner reads 0"** instead of a spreadsheet.

**Cost:** Core + API + Angular + a client regen. Small in lines, wide in blast radius because of the
`User` record change. One or two focused sessions.

**What could go wrong:**
- The `User` positional-construction change is the risk, and it is a **compile-time** risk, which is
  the good kind — nothing silently misbehaves.
- Exposing password *presence* on an admin-only route (`/api/users/all` is
  `.RequireAuthorization(AuthSetup.AdminPolicy)`, `SettingsEndpoints.cs:454`) is a small
  information disclosure. It tells an admin — who can already reset any password — which accounts
  are dormant. Note `/api/users` and `/api/users/names` are **not** admin-gated
  (`SettingsEndpoints.cs:421`, `:434`); `hasPassword` must not leak into those. `/names` already
  projects a deliberately narrow `NamedRefDto` (`:432-435`) and must stay that way.
- Does not reduce the *N* clicks at all. It only stops you from mistaking *N-3* for *N*.

**Undo cost:** cheap and total. It is a projection plus a boolean; revert the commit, regenerate the
client. No production data is written by any of it.

---

### Option C — Bulk-provision endpoint + one-shot handout

**What gets built:** `POST /api/auth/users/provision-all` in `AuthEndpoints.cs`, admin-gated. For
every active user whose hash is NULL, generate a password with `AdminBootstrap.GeneratePassword()`
(`:205-209`, currently `private` — would need widening), claim it via
`TryBootstrapAdminPasswordAsync` (`UserRepository.cs:239-240` — the atomic
`WHERE password_hash IS NULL`, so it can **never** overwrite an existing password), and return the
`{username, password}` pairs **once**, in the response body, for the admin to save as a CSV. Plus
Option B's visibility work, so you can confirm the sweep landed.

**Cost:** highest. New route + contract + a Core method to enumerate hashless users + the Angular
call + regen + tests. It also needs an explicit no-logging guard on the response.

**What could go wrong — this is the one to read carefully:**
- 🔴 **It creates a single HTTP call that returns every user's password in plaintext.** Combined
  with the cookie-trusted, non-DB-fresh admin gate (`AuthEndpoints.cs:131`; audit line 97), one
  stolen or borrowed admin session compromises **every account in the company at once**. Today the
  equivalent attack is *N* separate calls and leaves *N* separate opportunities to notice.
- The response must never reach a log, a browser history, an error report or a proxy. That is a
  property of the whole deployment, not of the endpoint, and it is not something a code review can
  guarantee.
- A CSV of live credentials now exists on somebody's laptop.
- It reuses `TryBootstrapAdminPasswordAsync`, so it is genuinely non-destructive to anyone who
  already has a password — that part is solid.

**Undo cost:** the code reverts cleanly. **The passwords do not.** Once the sweep runs, every
account has a credential in a file, and un-ringing that means resetting everyone again.

---

## 6. What I would pick, and why

**Option B, paired with W-1 (flag day) unless P1 proves the database is on local disk.**

**Why B over A:** the binding constraint is not the clicking, it is that **the application currently
lies about who can log in**. `canLogIn` returns `true` for every migrated user
(`users.component.ts:122`) at the precise moment when it is `false` for all of them. A cutover whose
completion gate is a hand-maintained list, checked against a screen that contradicts it, is a
cutover that strands somebody — and the cost of stranding somebody lands *after* WPF is deleted,
which is exactly when it is most expensive to fix. Option B converts the exit condition into
something the system can assert. That is worth a boolean.

**Why B over C:** C optimises the part that is merely tedious and inflates the part that is
dangerous. Unless the user count is genuinely large, *N* admin clicks spread over a week is not the
problem worth trading a total-compromise primitive for — especially against an admin gate that a
30-day cookie already over-trusts.

**Why W-1:** because I cannot see `DbPath`, and the asymmetry is brutal. If the database is local,
W-1 costs some inconvenience. If it is on a synced folder, W-2 risks the company's live data through
the exact mechanism `SqliteConnectionFactory.cs:15-18` was written to avoid. **If P1 shows a local
path, W-2 becomes reasonable and the fallback the brief asks for is available — that is the human's
call to make once the banner is read, not mine to make now.**

**What I am least sure of:** the silent-failure behaviour of `PRAGMA journal_mode=DELETE` against a
live WAL database (marked [ASSUMED] in §1a). It is the mechanism behind the W-1/W-2 recommendation.
It could be settled cheaply and safely — a scratch copy of the database, two processes, one pragma,
outside production — and it should be, before anyone commits to W-2.

---

## 7. Open questions a human must answer

1. **Where does `DbPath` actually point?** Read the P1 banner. This decides W-1 vs W-2 and is the
   single highest-value fact not in this document.
2. **Was the one-time admin console password ever captured?** If not, the cutover opens with a
   hand-written `UPDATE` against production. Plan it, back it up, do not improvise it.
3. **Who hosts the API, on what machine, reachable how?** (`deploy-local.bat:12-14`.) Nothing starts
   before this.
4. **How many users are there?** The A/C trade-off is entirely a function of *N*, and I have not
   inspected the database.
5. **Is any user expected to be in more than one team, or in a team other than the lowest-id one?**
   If so, note that every API restart auto-joins teamless users to the lowest-id team
   (`TeamBootstrapService.cs:111-113`, `:124-126`) — silently, and with no admin action.
6. **Should admin-issued passwords be forced to rotate on first login?** If yes, wiring the existing
   dead `POST /api/auth/set-password` route into a UI is a prerequisite, not a nice-to-have.
