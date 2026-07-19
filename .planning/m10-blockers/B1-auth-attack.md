# B1 — Attack on the auth-cutover recommendation

**Target:** "Option B, paired with a flag-day WPF cutoff (W-1) unless reading the startup banner proves
the database is on local disk."

**Bottom line:** the *choice of B over A and C survives, and its central argument is understated* — but
the plan wrapped around it does not. Three of the four prerequisites the document treats as one-line
setup steps are the actual blocker, and two of them can fail silently. Option B's own implementation
sketch, as written, ships a leak and a lie.

Every claim below carries `file:line`. Rule 1 was honoured: no build, no test, no database opened.

---

## FATAL

### F1 — There is no deployment target, and `dotnet run` is not one

The document's P0 reduces "who hosts it" to *"change `deploy-local.bat:38` to `--urls http://0.0.0.0:5080`
and open inbound TCP 5080."* That is necessary and nowhere near sufficient.

What actually exists [VERIFIED]:

- `deploy-local.bat:38` — `dotnet run -c Release --urls http://localhost:5080`. `dotnet run` is an SDK
  build-and-launch command running in the **foreground of a console window**. Close the window, the
  company's only remaining application is gone.
- `deploy-local.bat:19-31` — the script also runs `npm ci` / `npm run build` and `xcopy`. The host needs
  the .NET SDK **and** the Node toolchain **and** a source checkout.
- `ls src/TimesheetApp.Api/` returns `Auth Contracts Endpoints Hubs Infrastructure Program.cs Properties
  TimesheetApp.Api.csproj` — **no `appsettings.json`**, confirming `Program.cs:53`.
- Repo-wide grep for `UseWindowsService`, `New-Service`, `nssm`, `web.config`, IIS: **no hosting artifact
  of any kind** outside `.planning/` prose and `launchSettings.json`.
- No restart-on-reboot, no supervision, no health-check consumer (`/health` exists at `Program.cs:237`
  and nothing polls it).

Why this is fatal rather than merely untidy: **WPF ran on every user's own machine.** After M10 the
entire company's ability to do any work depends on one console window on one workstation staying open
through reboots, Windows Update, and someone tidying their taskbar — with the fallback deleted. The
document prices the availability change at zero. It is the largest single item between any option and
execution, and it is not a code decision, so no option below can absorb it.

[ASSUMED] that an operator would in fact host it this way — I cannot know what they would improvise.
[VERIFIED] that the repository offers them nothing else to improvise from.

### F2 — P2, the backup prerequisite, can silently produce an unusable backup — and P2 is the safety net for everything else in the document

P2 says: *"Take a backup by a path that is not the app."* Both available roads to that can lie.

**Road 1 — copy the file.** Under `SqliteProfile.Server` the API sets `PRAGMA journal_mode=WAL`
(`SqliteConnectionFactory.cs:65-66`), which is **persistent**. The repository states the consequence in
its own words at `SqliteOnlineBackup.cs:11-16` [VERIFIED]:

> A file-level `File.Copy` of a live .db is only safe under the pre-M8.2 profile … Under WAL those
> premises are gone — committed pages sit in the `-wal` sidecar until a checkpoint, so copying the .db
> alone yields … a file whose *"transactions that were previously committed … might be lost, or the
> database file might become corrupted"*.

An operator following P2 literally, while the API is up, gets a file that looks like a backup and may
not be one. They find out when they need it.

**Road 2 — click "Backup now" in Settings.** The M10 coverage audit already documents this at its line
92: `BackupService.BackupNowAsync()` returns `null` (not an exception) when the folder or DB path is
blank, the null survives the wire, and the client renders `` `Backup written to ${r.value ?? 'the
configured folder'}` `` plus a `'Backup complete'` toast. `JsonAppConfig` defaults `BackupFolderPath`
to `""`, so a server that never configured one **says "Backup complete" every time**.

The document warns *"Do not assume startup backs anything up"* — correct, and it stops one step short
of the two roads the operator will actually take.

This is fatal because P2 is what makes every other risk in the document survivable, including the
OneDrive/WAL corruption the document is most worried about. A prerequisite that can silently
not-happen, underneath a plan whose entire safety argument rests on it, is the worst hole here — and
it is the same "operator believes something worked when it did not" shape the audit already logs twice.

The safe procedure exists but is written down nowhere: **stop the API, let it shut down cleanly so the
WAL checkpoints, then copy `.db` + `-wal` + `-shm` together** — or drive `SqliteOnlineBackup.Copy`
(`SqliteOnlineBackup.cs:39-45`), which the repo built for exactly this and which no operator-facing path
reaches.

---

## SERIOUS

### S1 — W-1 does not make the WAL/OneDrive problem go away; the document's own table says its data-safety cost is "None"

The document frames `DbPath` as deciding **W-1 vs W-2**. It decides something larger.

`SqliteConnectionFactory.cs:21` states the Server profile's precondition outright [VERIFIED]:

> Single writer process **on the host's local disk** -> WAL is safe and wanted.

and `:8-11`:

> Core is shared by WPF (SQLite on a synced folder) and the future API (SQLite **on the host's local
> disk**), and the two need OPPOSITE settings.

So if `DbPath` points at a synced folder, running the API in Server profile violates a precondition the
code states about itself — **permanently, flag day or not**. The `-wal`/`-shm` sidecars the Desktop
profile exists to avoid (`:15-18`) are created on every connection and sync out of band forever, not
just during an overlap window. The document's W-1 column reads *Concurrency risk: None / Data-safety
cost: None*; that is true only of the two-writer hazard, not of the hazard the profile comment names.

"Move the database to local disk first, which is its own piece of work" is relegated to a parenthetical
in §6. On a synced `DbPath` it is not an aside — it is P0.5, and it precedes everything.

I could not determine which case applies (`%APPDATA%\TimesheetApp\appsettings.json`, Rule 1). The
document is right that this is the highest-value unknown. It is wrong about what the answer decides.

### S2 — Option B's `User.HasPassword` sketch produces a silently wrong value in three of four read paths, and the document's risk note is exactly inverted

`UserRepository` has four SELECTs into `UserRaw`, all funnelling through one mapper [VERIFIED]:

| Method | Line |
|---|---|
| `GetActiveAsync` | `:34` |
| `GetAllAsync` | `:42` |
| `GetByIdAsync` | `:50` |
| `GetByUsernameAsync` | `:58` |
| `MapUser` — 6 positional args | `:255-256` |

The document instructs: project `has_password` **in `GetAllAsync` only** (`:42`), and append
`bool HasPassword` to `User` (`Entities.cs:21`) *"last with a default so existing call sites keep
compiling."*

Those two instructions together guarantee that **nothing fails to compile** and that `HasPassword` is
`false` for every user returned by the other three methods — Dapper leaves an unselected column at its
CLR default. `false` then means both *"this account has no password"* and *"nobody asked."*

The document's own assessment says the change is *"a compile-time risk, which is the good kind —
nothing silently misbehaves."* The defaulted-parameter instruction one paragraph earlier is precisely
what removes the compile-time check. For a change whose entire purpose is **to stop the application
lying about who can log in**, shipping a field that is a lie in 3 of 4 read paths is the wrong failure.

Fix before implementing: project `has_password` in **all four** SELECTs, or type it `bool?` so
"unknown" is representable and a null-vs-false bug is visible at the call site.

(Mitigating, and worth recording: today the Users page reads `getUsersAll()` → `/api/users/all`
(`users.component.ts:131`), which *would* carry the projection, and the stale-`false` direction
over-reports blocked users — so the "banner reads 0" gate would fail closed, never open. The landmine
is for the next person, not this cutover.)

### S3 — Option B's stated blast-radius containment is contradicted by the code; the plan as written ships the leak it warns about

The document says the disclosure is confined to the admin-gated `/api/users/all`
(`SettingsEndpoints.cs:454`), then warns *"`hasPassword` must not leak into"* the un-gated
`/api/users` (`:421`) and `/api/users/names` (`:434`).

There is exactly one `User → UserDto` mapper [VERIFIED]:

- `Dtos.cs:204-205` — `public static UserDto ToDto(this User u) => new(u.Id, u.Name, u.WindowsUsername, u.IsActive, u.IsAdmin, u.RowVersion);`
- `SettingsEndpoints.cs:422` — `/api/users` … `.Select(u => u.ToDto())`, **no `.RequireAuthorization`**
- `SettingsEndpoints.cs:453` — `/api/users/all` … `.Select(u => u.ToDto())`, `.RequireAuthorization(AuthSetup.AdminPolicy)` at `:454`

Adding `HasPassword` to `UserDto` and "the corresponding `ToDto()` mapping" — the document's literal
instruction — puts the field on the **un-gated** route by construction. The caveat and the
implementation plan are mutually exclusive. A subagent handed this plan and told to follow it ships the
leak while believing the caveat was honoured.

`/api/users/names` is safe as-is (`:435` projects `NamedRefDto`). Closing this needs a second DTO or an
endpoint-level projection on `/api/users/all` — a design decision the plan does not make.

The leak itself is modest (a list of dormant accounts, to authenticated users only). The defect is that
the document asserts a security property its own instructions break.

### S4 — Every API restart silently re-grants team membership, and the cutover guarantees several restarts

The document's headline correction — that `TeamBootstrapService` re-runs the backfill on **every**
startup — is **confirmed** [VERIFIED]: `TeamBootstrapService.cs:50-57` takes the `existing is not null`
branch and calls `BackfillTeamAsync(existing.Id, backupFirst: false)` unconditionally. Good catch, and
the prior pass was indeed wrong.

It then stops one line short of the consequence that matters. `:111-113` runs

```sql
INSERT OR IGNORE INTO UserTeams(user_id, team_id) SELECT id, @t FROM Users;
```

against the **lowest-id team**, for **every user**, on every boot — not merely teamless ones. Meanwhile
`PUT /api/teams/{id}/members` → `SetMembersCheckedAsync` (`SettingsEndpoints.cs:308`) is a replace-all,
so an admin genuinely can remove someone from a team.

**That removal is not durable.** The next API restart re-adds them, silently, with no admin action and
nothing logged.

It is not cosmetic, because membership is the authorization bound. `EffectiveTeamIds`
(`BacklogEndpoints.cs:571-583`) defaults an unfiltered `GET /api/backlogs` to `ctx.MemberTeamIds` —
every team the caller belongs to — and the M10 audit (line 53) records that the web Backlog screen has
**no team control at all**, so the user cannot narrow it back. A restart therefore silently *widens*
what a person sees.

Restarts are guaranteed during this cutover: the null-the-column admin-recovery hatch requires one, and
deploying Option B requires another.

### S5 — No transport security, and the procedure common to all three options is the moment it matters most

[VERIFIED]:

- `deploy-local.bat:38` and `start-web.bat:39` — `--urls http://…`. No HTTPS anywhere.
- `AuthSetup.cs:102` — `o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest`, with the comment
  conceding *"HTTP is a knowingly accepted risk on the internal network"*. Over HTTP the session cookie
  is **not** marked Secure.
- No `UseHttpsRedirection`, no certificate, no reverse proxy in the repo.

The procedure shared by A, B and C is: *an admin types N passwords into a form, and N people then type
them back in.* Every one of those `POST /api/auth/login` and `POST /api/auth/users/{id}/set-password`
bodies crosses the LAN in cleartext, as does every session cookie, for as long as the deployment lives.

The document's entire security discussion is about Option C's CSV file. The wire is the larger and more
permanent exposure and it is **not** a differentiator between the options — which is worth stating
plainly, because it is currently the unexamined constant against which C is being judged too harshly
and A and B too kindly.

### S6 — `savePassword` claims "can now log in" without checking, then re-reads a DTO that cannot confirm the write

`users.component.ts:284` — on success: ``this.toast.show(`${u.name ?? 'The user'} can now log in`)`` —
then `this.load()` (`:285`), which re-reads `/api/users/all` → `UserDto`, which has **no password
field** (`Dtos.cs:61-62`). The refreshed screen is byte-identical to what it showed before the write.
**The toast is the only evidence the write landed anywhere.**

And the claim is not always true: `POST /api/users` creates a user with `username = NULL`
(`SettingsEndpoints.cs:468`), the admin set-password route has no username check and no `is_active`
check (`AuthEndpoints.cs:116-138`), and login is keyed on username (`AuthSetup.cs:162`,
`UserRepository.cs:190-199`). Setting a password on a name-only user produces "X can now log in" for
someone who cannot.

This sits **inside Option A's step 3 loop**, repeated N times. It is the same lie-shape the audit logs
twice already. Option B fixes the screen; it does not fix this toast unless the plan says so, and the
plan does not.

---

## MINOR

### M1 — The Data Protection key ring follows the database, in both directions

`Program.cs:43-46` defaults `keyRingPath` to `<database directory>/keys`. Two unmentioned consequences:

- If `DbPath` is on a synced folder, **the cookie key ring syncs too** — conflict copies of key files,
  and the mass-logout failure `AuthSetup.cs:75-90` was written specifically to prevent.
- If the operator follows the document's "move the database to local disk" aside, the key ring moves
  with it and **every live session dies at that moment**. Harmless if expected, alarming mid-cutover.

### M2 — No password floor, no rate limiting, no lockout

`AuthEndpoints.cs:121-122` validates an admin-set password with `IsNullOrWhiteSpace` only — `"a"` is
accepted. `AddRateLimiter` does not appear in `Program.cs` (read in full, 287 lines) and no lockout or
failed-attempt counter exists. For a flag day where one admin hand-types N passwords under time
pressure onto an HTTP endpoint, both matter more than usual.

### M3 — Two stale comments that will mislead whoever runs this

- `AdminBootstrap.cs:146-154` warns that `Users.username` *"carries no UNIQUE index"* and calls closing
  it out of scope. **v11 closed it** — `CREATE UNIQUE INDEX IF NOT EXISTS ux_users_username ON
  Users(username COLLATE NOCASE) WHERE username IS NOT NULL` (`DatabaseInitializer.cs`, v11 step). The
  double-admin deadlock that comment describes can no longer happen.
- `AuthEndpoints.cs:64` (*"Core is frozen, so one cannot be added here"*) and `AdminBootstrap.cs:44`
  (*"Wave 1 may not modify Core"*) are M8.3-era constraints, **no longer true** — M9 added
  `SetIsAdminCheckedAsync` to `UserRepository.cs:216`. This matters because Option B *requires* a Core
  change, and a reader could reasonably take those comments as a standing prohibition and abandon B for
  the wrong reason.

### M4 — The "banner reads 0" gate needs an `is_active` filter nobody specified

`GetAllAsync` (`:42`) returns deactivated users. The admin set-password route has no `is_active` check
(`AuthEndpoints.cs:116-138`), and login refuses inactive users anyway (`AuthSetup.cs:170-171`).
Unfiltered, a "N users cannot log in yet" banner counts every departed employee and **never reaches
zero** — so the cutover gate never opens, or worse, someone provisions passwords for leavers to clear
it.

---

## What the attack could NOT break

Stated plainly, because it is the part of the recommendation that should be kept.

**B's central argument is correct and understated.** The document says `canLogIn`
(`users.component.ts:122`) "checks username only", which is true — but the template is worse than that.
`users.component.html:90-97` [VERIFIED]:

```html
@if (canLogIn(u)) {
  {{ u.username }}
} @else {
  <!-- The ghost, made visible. This is the state a half-finished create leaves behind. -->
  <span class="badge badge-warn" title="No username — this account cannot be logged into.">
    No login
  </span>
}
```

Rendering the username **is** the affirmative "this account can be logged into" signal, with an explicit
tooltip contract on the negative branch. On a migrated production database — every user carrying a
username from WPF's auto-provision, every `password_hash` NULL — the screen makes a **positive false
claim about the entire population**, not merely an absent one. The component's own docstring at `:121`
already says what it intends to check (*"no username, **or created before a password was ever set**"*)
and the code implements only the first half.

`createUserFully`'s error handler (`users.component.ts:~200`) makes the same point in the codebase's own
voice: *"If steps 1-2 landed and step 3 did not, an account that cannot be logged into is now sitting in
the list looking completely normal."* That partial-failure path is **already live**. Option B is the fix
for a defect the code has already documented against itself.

**B over C also holds**, and S5 strengthens it: a cleartext-HTTP deployment with no rate limiting is a
poor place to add an endpoint that returns every credential in one response.

Also confirmed against source: the every-startup backfill (S4), the WAL/DELETE profile opposition
(`SqliteConnectionFactory.cs:65-66`, `:72`), fail-closed login on NULL hash (`AuthSetup.cs:176-177`),
the atomic bootstrap claim (`UserRepository.cs:235-243`), the once-only console password
(`AdminBootstrap.cs:114-123`), and the non-DB-fresh admin gate on password reset
(`AuthEndpoints.cs:131` with no `is_admin` re-read in the handler, cookie `IsPersistent = true` at
`AuthSetup.cs:203`).

---

## Verdict

**Option B survives as the code choice. "Option B paired with W-1" does not survive as an executable
plan.** The reasoning for B over A and over C is sound and its core evidence is stronger than the
document claims. What fails is the framing that treats the prerequisites as setup: P0 is a project, not
a flag (F1); P2 can silently not happen and everything else leans on it (F2); and W-1 does not address
the hazard the document is most afraid of if `DbPath` is synced (S1).

**Recommended replacement — same option, honest sequencing.** A human still decides each gate; these
are ordered because each one makes the next survivable, not because I am choosing for anyone:

1. **P0.5 — Read the banner (`Program.cs:57-60`) first.** If `DbPath` is on a synced root, moving it to
   local disk is the first task, not a footnote (S1). If it is already local, this gate closes free and
   W-2 becomes a genuine option the human can weigh.
2. **P2' — Establish a *verified* backup procedure before anything else touches the database.** Stop the
   API, copy `.db` + `-wal` + `-shm`, and confirm the copy opens. Do not use the Settings "Backup now"
   button as the cutover backup — it reports success when nothing happened (F2).
3. **P0' — Settle hosting properly:** a supervised service that survives reboot, not a console window
   (F1). This is the item most likely to be larger than the code work.
4. **Then Option B's code**, with two corrections to its own sketch: project `has_password` in **all
   four** `UserRepository` SELECTs or type it `bool?` (S2), and keep it off the un-gated `/api/users` by
   giving `/api/users/all` its own DTO rather than by adding a field to the shared `ToDto` (S3). Add the
   `is_active` filter to the banner (M4) and fix the `savePassword` toast to stop claiming a login that
   a NULL username makes impossible (S6).
5. **Then Option A's provisioning procedure**, gated on the banner reading 0 — with the team-membership
   restart behaviour (S4) understood in advance, since the cutover forces the restarts that trigger it.

**W-1 vs W-2 remains the human's call and gets easier after step 1**, not before: on local disk the
document's own cited line (`start-web.bat:16-18`, *"you will not lose data"*) makes W-2 defensible and
restores the fallback the brief asked for; on a synced root neither W-1 nor W-2 is safe until the
database moves, which is why step 1 is step 1.

**Still unverified and still worth settling on a scratch copy, as the document itself says:** whether
`PRAGMA journal_mode=DELETE` fails silently against a live WAL database. [ASSUMED] in the original,
[ASSUMED] here. S1 reduces how much rides on it — if `DbPath` is synced the answer stops mattering,
because W-1 is not sufficient either.
