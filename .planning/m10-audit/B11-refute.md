# B11 — Adversarial refutation (CurrentUserService / user identification)

Scope: the single row the auditor marked **COVERED** — *"NO password, NO session, NO role/permission — Windows
identity is the sole identity; every user can do everything, including delete a team or run retention."*

**Result: REFUTED → PARTIAL.** The web app does build real auth, and the ops/team routes really are gated —
that half of the auditor's evidence survives a full trace. But the note's conclusion (*"Nothing is lost because
the row describes a gap the web app closes, not a capability it needs to preserve"*) is wrong on both of its
load-bearing words:

1. **"Nothing is lost"** — "no password" is not only a liability, it is the mechanism by which *every existing
   non-admin employee currently has access*. Deleting WPF converts "works today, no credential needed" into
   "401, with no self-recovery path", for the entire existing user population. That is a deletion-caused loss
   of access with a concrete remedy cost, not a gap being closed.
2. **"a server-verified admin claim gating every destructive route"** — server-verified (DB-fresh) admin
   checking exists on exactly **7 routes**. Every other admin route — *including the three team routes the
   auditor cited by line number, and including admin password-reset* — trusts a claim frozen into a 30-day
   cookie at login. The codebase says so itself.

| Feature | Original | Refuted? | Final | Evidence |
|---|---|---|---|---|
| 🔴 NO password, NO session, NO role/permission — Windows identity is the sole identity; every user can do everything, incl. delete a team / run retention | COVERED | **YES** | **PARTIAL** | See both strands below |

---

## Strand 1 — "Nothing is lost" is false: every existing non-admin user is locked out by the deletion

The WPF side (dies in M10) grants access with **no credential of any kind**:

- `src/TimesheetApp/App.xaml.cs:160` registers `ICurrentUserService → CurrentUserService`.
- `src/TimesheetApp.Core/Services/CurrentUserService.cs:14` — the WPF-host constructor is
  `: this(users, () => Environment.UserName)`. `ResolveAsync()` (`:25-37`) is a bare
  `GetByUsernameAsync(Environment.UserName)`. No hash is read, no password is asked for. [VERIFIED]

The web side cannot serve those same users, and **the block is structural, not incidental**:

- `src/TimesheetApp.Core/Data/DatabaseInitializer.cs:331` — v10 is
  `ALTER TABLE Users ADD COLUMN password_hash TEXT;` — no `DEFAULT`, **no backfill statement anywhere in the
  migration**. `:337` promotes only `MIN(id)` to `is_admin = 1`. So on the production database every user
  except exactly one has `password_hash IS NULL`. [VERIFIED at the schema level; the row contents themselves
  are [ASSUMED] — the DB was deliberately not opened.]
- `src/TimesheetApp.Api/Auth/AuthSetup.cs:176-177` — `if (string.IsNullOrEmpty(creds.PasswordHash)) return
  Results.Unauthorized();`. A NULL hash is a hard 401. Confirmed by `VerifyPassword`'s fail-closed guard at
  `:66-67`. [VERIFIED]
- `src/TimesheetApp.Api/Endpoints/AuthEndpoints.cs:91-93` — **there is no self-recovery.** Self set-password
  on a NULL hash returns 400 *"No password is set for this account. Ask an administrator to set one."* A user
  in this state cannot let themselves back in. [VERIFIED]
- The only remedy is `POST /api/auth/users/{id}/set-password` — `AuthEndpoints.cs:116`, gated
  `.RequireAuthorization(AuthSetup.AdminPolicy)` at `:131` — driven **one user at a time** from the Users
  screen (`src/timesheet-web/src/app/pages/users/users.component.ts:278`, `this.api.adminSetPassword(id, …)`).
  [VERIFIED]
- **No bulk provisioning path exists.** `SetPasswordHashAsync` has exactly two call sites in the whole
  product (`AuthEndpoints.cs:101` self, `:128` admin-for-one-user), and
  `TryBootstrapAdminPasswordAsync` only ever runs inside `AdminBootstrap`, whose loop iterates
  `all.Where(u => u.IsAdmin)` — `src/TimesheetApp.Api/Auth/AdminBootstrap.cs:47, 87, 102`. Ordinary users are
  never touched by bootstrap. `AdminBootstrap.cs:9-12` states it plainly: v10 *"gave nobody a hash. As it
  stands NOBODY CAN LOG IN."* [VERIFIED]

**Net effect of the deletion:** today an employee opens the desktop app and works. After `src/TimesheetApp/`
is removed, their only door is the web login, which 401s them, and the self-service door is explicitly closed
against them. Access is restored only by an admin performing a manual per-user password reset for every
employee. That is a real migration obligation created by this row's change, and the auditor's note asserts the
opposite ("not a capability it needs to preserve"). Nothing here is *ported from WPF* — the remedy is
operational, not code — but it must not be recorded as COVERED, because COVERED reads as "delete and move on."

*(Note: B11's own MISSING rows cover the **new**-user onboarding path. This is a distinct population — users
who **already exist** in the production database with a `username` and a NULL hash.)*

## Strand 2 — "a server-verified admin claim gating every destructive route" is factually overstated

The API has **two different admin checks**, and the codebase documents the discrepancy itself:

> `src/TimesheetApp.Api/Endpoints/AdminEndpoints.cs:23` — *"**THERE ARE TWO ADMIN CHECKS IN THIS API AND THEY
> DISAGREE FOR UP TO THIRTY DAYS.**"* `:25-27` — the policy gates on the `is_admin` **claim**, *"written ONCE,
> at login, into a 30-day sliding cookie. Demote an admin in the database and the cookie they are already
> holding still satisfies this policy until it expires."* `:37-40` — closing that window is *"out of scope
> here, and is recorded as an open risk."* [VERIFIED]

DB-fresh `ctx.IsAdmin` re-checking exists on **7 routes only**:

- `SettingsEndpoints.cs:1004, 1025, 1043, 1053` — the four `/api/ops/*` routes (retention preview/run, export,
  backup). The auditor's retention citation therefore holds. [VERIFIED]
- `AdminEndpoints.cs:60, 104, 138` — set-admin-flag, settings write, standup archive. [VERIFIED]

Everything else is claim-only. Critically, **the auditor's own cited evidence falls in the claim-only bucket**:

- `SettingsEndpoints.cs:254` (`POST /api/teams`), `:271` (`PUT /api/teams/{id}`), `:286`
  (`PUT /api/teams/{id}/active`) — `.RequireAuthorization(AdminPolicy)` and **no `ctx.IsAdmin` line in any of
  the three handlers** (`:226-252`, `:260-269`, `:278-284`). A demoted admin can still create, rename and
  deactivate teams for up to 30 days. [VERIFIED]
- `AuthEndpoints.cs:116-131` — `POST /api/auth/users/{id}/set-password` has **no DB-fresh check at all**. This
  is a full account-takeover primitive (reset any user's password, no current-password proof — `:110-112`),
  reachable for 30 days on a revoked admin's cookie. The UI even tells the operator so:
  `src/timesheet-web/src/app/pages/users/users.component.html:155-157` — *"Removing admin takes effect on
  their next login — the session cookie carries the claim for up to 30 days."* [VERIFIED]

So the property is "a **login-time** admin claim gates every destructive route, and a **server-verified** one
gates seven of them." That is materially narrower than the note's wording, and it is exactly the
partial-coverage-claimed-as-full shape the audit is meant to catch.

## What the auditor got right (kept, not downgraded)

Traced end to end and confirmed — do **not** re-litigate these:

- Password auth is real, not named-only: PBKDF2/HMAC-SHA512 via one shared hasher (`AuthSetup.cs:41, 44,
  64-70`), fail-closed on null hash (`:66-67`), `!= Failed` rather than `== Success` (`:69`).
- Cookie auth is wired and reachable: `AuthSetup.cs:92-128`, login route `:157-211`, `AllowAnonymous` at `:207`
  so the login route is not self-locked; `FallbackPolicy = DefaultPolicy` at `:146` makes the API
  secure-by-default.
- The chain is complete on the client: `pages/login/login.component.ts:42` → `AuthService.login()` →
  `POST /api/auth/login`; `app.routes.ts:18` `authGuard` on the shell; `:53` and `:59` `adminGuard` on
  `/users` and `/settings`.
- `/api/ops/*` retention **is** double-gated exactly as the class doc at `SettingsEndpoints.cs:38` claims —
  verified against the handlers at `:1002-1059`, not just the comment.
- Every global/destructive mutation in `SettingsEndpoints.cs` carries `.RequireAuthorization(AdminPolicy)`; a
  full sweep of `Map(Post|Put|Delete)` across `src/TimesheetApp.Api/Endpoints` found **no ungated destructive
  route**. The two deliberately-open reads (`/api/pca-contacts/names` `:341`, `/api/users/names` `:434`,
  `GET /api/settings/{key}` `AdminEndpoints.cs:89`) are still behind the authenticated `FallbackPolicy` and
  carry no credential fields.

## Bottom line

`PARTIAL`, not `COVERED`. The web app closes most of the gap and closes it properly, but (a) the deletion
strands every existing non-admin user behind a 401 with no self-service recovery and no bulk provisioning
path, and (b) "server-verified admin claim on every destructive route" is true of 7 routes and false of the
rest — including the three the auditor cited and including admin password-reset. Record the cutover
obligation (provision passwords for all existing users before removing the desktop app) before M10 ships.
