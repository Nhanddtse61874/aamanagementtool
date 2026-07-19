# B2-restore — Attack on the recommendation

**Target:** Option B (offline CLI restore mode in the existing API host), with Option A's runbook folded in,
as specified in `.planning/m10-blockers/B2-restore-options.md`.

**Verdict in one line:** the *shape* of Option B survives — it is still the right place to put a restore — but
**the recommendation as written does not**, because it ships a command that deletes the production database
before establishing that the file replacing it is a database at all, points that command at a per-user path it
never prints, guards it with a probe that is definitionally incapable of detecting a running server, and rests
two of its four rationales on tests M10 deletes.

**This file now holds two passes.** Pass 1 found HOLES 1-9. Pass 2 (this one) independently re-read every
load-bearing claim in the options doc from source, **re-verified HOLES 1, 2, 3, 4, 5, 7 and 9 and found none
of them wrong**, and added HOLES 10-14 plus one correction *against* pass 1 (see HOLE 12, which softens
HOLE 3). Where the two passes disagree it is called out explicitly.

Everything below carries `file:line`. `[VERIFIED]` = I read it. `[CITED]` = external documentation.
`[ASSUMED]` = inferred and not demonstrated. Read-only pass: no source edited, no command run that could open
a database.

---

## HOLE 1 — FATAL. Restore destroys the live database *before* validating the source, and the failure silently rebuilds an empty app

This is the single largest defect and the recommendation does not mention it.

`SqliteOnlineBackup.Copy` deletes the destination first and opens the source second:

```
SqliteOnlineBackup.cs:41   DeleteWithSidecars(destDbPath);          // live .db + -wal + -shm GONE
SqliteOnlineBackup.cs:43   using var source = Open(sourceDbPath, SqliteOpenMode.ReadWrite);
```
`[VERIFIED]` — `DeleteWithSidecars` is `SqliteOnlineBackup.cs:85-90`; three unconditional `File.Delete` calls.

`RestoreAsync` validates the source with **exactly two checks**, neither of which is "is this a database":
- `BackupService.cs:99` — `File.Exists(backupPath)`
- `BackupService.cs:106-108` — not the live DB onto itself

`[VERIFIED]`. There is no integrity check. And the codebase *has* one: `SqliteOnlineBackup.IsIntact`
(`SqliteOnlineBackup.cs:64-79`), whose own doc-comment says *"Exists && Length > 0" is not evidence that a
file is a usable database — six arbitrary bytes pass it*. Its production callers: **one** —
`PruneArchiver.cs:178`, where it gates permanent row deletion. `[VERIFIED — grep across src/]`

So the more destructive of the two operations is the one that skips the check.

**The failure chain, end to end:**

1. Operator runs `-- --restore "<some .bak>"` against a truncated / half-synced / not-actually-a-database file.
2. `BackupService.cs:122` writes the `.pre-restore_{stamp}.bak` safety copy. Good.
3. `BackupService.cs:124` → `Copy` → `SqliteOnlineBackup.cs:41` **deletes `timesheet.db`, `timesheet.db-wal`, `timesheet.db-shm`**.
4. `SqliteOnlineBackup.cs:43` throws `SqliteException` ("file is not a database").
5. **There is now no production database.** No rollback exists in the code path.
6. Operator restarts via `deploy-local.bat`. `SqliteConnectionFactory.cs:53` opens `ReadWriteCreate` →
   **creates a new empty file**. `Program.cs:176` `InitializeAsync()` builds the full v11 schema.
   `Program.cs:179` `EnsureBootstrappedAsync()` creates a team. `Program.cs:181`
   `AdminBootstrap.EnsureAdminPasswordAsync` seeds `admin`/`admin` — and it is **on by default in
   production**: `AdminBootstrap.cs:36-37` is
   `!bool.TryParse(config?["TimesheetApp:SeedFirstAdmin"], out var enabled) || enabled`, so an absent key
   evaluates true; the credentials are `AdminBootstrap.cs:28-29`, applied at `:158-159`.
   **`[VERIFIED — pass 2 re-read AdminBootstrap.cs:25-37,155-159 independently and confirms it]`**
7. The startup banner (`Program.cs:57-60`) prints the correct DB path. The browser shows a working, logged-in,
   **completely empty** application.

The operator's evidence that the restore failed is a stack trace in a console window they have already closed
and restarted. The evidence that it "worked" is a functioning app. **This is the third instance of the exact
failure mode the brief says must not be let in — and unlike the first two it is unrecoverable in effect,
because the `.pre-restore` file is the only survivor and nothing points at it** (see HOLE 7: the tool that is
supposed to find it cannot).

This defect lives in `RestoreAsync`, which A, B and C all call, so it does not *select between* the options.
But it directly contradicts rationale (2) — "it does not extend the code into an untested regime". Handing an
operator a flag that accepts **an arbitrary path** is an extension: the only shipped caller,
`SettingsViewModel.cs:293-305`, takes a `BackupInfo` and can therefore only pass a path `ListBackups()`
already produced and parsed (`BackupService.cs:78-80`). `--restore "<path>"` removes that constraint.
`[VERIFIED]`

**Required amendment before B ships:** `IsIntact(backupPath)` as a hard precondition inside `RestoreAsync`
(not in the CLI wrapper — A and any future C must inherit it), plus a post-restore `IsIntact(dbPath)` and a
non-zero exit code. Both cheap; neither is in the recommendation.

---

## HOLE 2 — SERIOUS. Two of the four rationales rest on tests that M10 deletes

The recommendation cites `WalBackupSafetyTests.cs:87` and `TinyDb.cs:86` as its evidence that restore is safe
in B's regime. Both claims check out `[VERIFIED — both passes]` — the restore at `:87` is outside the `using`
block that closes at `:81`, and `TinyDb.cs:86` sets `Pooling = false`.

But both files live in `src/TimesheetApp.Tests/`, and:

```
src/TimesheetApp.Tests/TimesheetApp.Tests.csproj:21
    <ProjectReference Include="..\TimesheetApp\TimesheetApp.csproj" />
```
**`[VERIFIED — pass 2 re-read the csproj and confirms line 21]`**

M10 deletes `src/TimesheetApp/`. **That reference then dangles and `TimesheetApp.Tests` does not compile.**
The coupling is not incidental: 18 files reference WPF namespaces, 13 files under `ViewModels/`.
`[VERIFIED — grep + ls]`

So the entire evidentiary basis for "restore is safe when quiesced" — `WalBackupSafetyTests` (WAL-consistent
snapshot, stale-`-wal` removal) and `BackupServiceTests` (missing-file rejection, self-restore rejection, list
ordering) — is inside the blast radius of the milestone this decision belongs to. Nothing in the
recommendation mentions moving them.

Relocating them is not free: `TimesheetApp.ApiTests` is `Microsoft.NET.Sdk.Web` / `net8.0` and has **no Moq
package reference** `[VERIFIED]`, while `WalBackupSafetyTests.cs:47-53` builds its fixture on
`Mock<IAppConfig>` / `Mock<IClock>` — `TimesheetApp.Tests.csproj:16` carries `Moq 4.20.72`
**`[VERIFIED — pass 2]`**. Either Moq is added there, or a new `net8.0` Core-test project is created, or
`TimesheetApp.Tests` is gutted to a Core-only reference. All three are real M10 work that B's stated cost
("2-3 tests, one doc… half a day to a day") does not contain.

---

## HOLE 3 — SERIOUS. On the actual production machine, `--list-backups` prints nothing

The recommendation files this under "two adjacent facts that may outrank this blocker entirely" and declines
to fold it in. That is the wrong call, because it is not adjacent to Option B — **it is Option B's input.**

- `%APPDATA%\TimesheetApp\appsettings.json:4` → `"BackupFolderPath": null` `[VERIFIED — both passes]`
- Therefore `BackupService.cs:74-75` returns `Array.Empty<BackupInfo>()`. `--list-backups` prints an empty
  list. `[VERIFIED]`
- `appsettings.json:5` → `"AutoBackupEnabled": false` `[VERIFIED]`. This is **stronger than the
  recommendation's fact (b)**: scheduled backup is not merely losing its caller at `App.xaml.cs:63`, it is
  already switched off by configuration — `AutoBackupIfDueAsync` returns `false` at `BackupService.cs:61`
  before it reaches the folder check. M10 deletes the caller of something already inert.
- The artifacts that exist on disk are **11** files named `timesheet.db.{stamp}.bak` in
  `C:\Users\Admin\Documents\TimesheetApp\`, produced by `DbBackupHelper.cs:38`. `[VERIFIED — directory
  listing, filenames only, no file opened]`
- Those do **not** match `ListBackups`' glob `timesheet_*.db` (`BackupService.cs:22-23, :78`), and are not in
  a `BackupFolderPath`. `[VERIFIED]`

**Pass 2 partially reverses pass 1's conclusion here — see HOLE 12.** Pass 1 wrote that B ships "a restore
command whose only real-world inputs are files it was never designed to enumerate, validate, or order",
implying they are unsafe inputs. They are in fact valid, self-contained, restorable databases. What survives
is narrower and still serious: **B's discovery half is dead on arrival on this machine**, and the operator
must hand-type a path — which is precisely the condition under which HOLE 1 fires.

**B's discovery half should be sequenced after the "we are producing no `IBackupService` backups at all"
decision.**

---

## HOLE 4 — SERIOUS. The runbook half points at the wrong directory

The recommendation (and the brief) anchor on `%APPDATA%\TimesheetApp\appsettings.json`. Correct for the
**config**. Not where the **database** is.

```
%APPDATA%\TimesheetApp\appsettings.json:2
    "DbPath": "C:\\Users\\Admin\\Documents\\TimesheetApp\\timesheet.db"
```
`[VERIFIED]` — and it matches the code default: `JsonAppConfig.cs:178-182` resolves `DefaultDbPath()` from
`SpecialFolder.MyDocuments`, while `JsonAppConfig.cs:172-176` resolves the config from
`SpecialFolder.ApplicationData`. **Deliberately different folders.** `%APPDATA%\TimesheetApp\` contains one
file, `appsettings.json`. `[VERIFIED — both passes]`

Option A's steps 3, 4 and 5 — the ones the recommendation says must be written *as part of* B — are all "go to
the DB folder and copy/delete these files". A runbook naming `%APPDATA%` sends the operator to a directory
with no database, no `-wal`, no `-shm`. Step 4 ("delete the sidecars") deletes nothing and reports success.
Step 3 ("copy the current DB aside") copies nothing. The operator then performs step 5 against the *real* path
or not at all, and in either case believes the preceding safety steps were done.

A runbook that is silently a no-op for its safety steps and load-bearing only for its destructive one. B's
code reads `appConfig.DbPath` programmatically so the code is immune — **the document is not**, and the
recommendation bundles the document into B.

---

## HOLE 5 — SERIOUS. "Quiescence by construction" is true of Kestrel and of nothing else

Rationale (1), the recommendation's lead argument. Narrower than stated.

B guarantees *this process* opens no connections. **The handles that endanger the restore are in the server
process, which Option B does nothing to stop.** B's actual precondition is "the operator stopped the server
first" — bit-for-bit the same operator-discipline precondition as Option A, whose enforcement model the
rationale contrasts itself against.

**(a) A second instance of the app.** Nothing prevents the restore command running while
`deploy-local.bat:38`'s `dotnet run -c Release --urls http://localhost:5080` is live. In that case:

- `SqliteOnlineBackup.ClearPools()` (`SqliteOnlineBackup.cs:83`) is `SqliteConnection.ClearAllPools()` — a
  **per-process** static. It cannot touch process 1's pooled handles. `[VERIFIED that it is a plain static
  call; per-process scope is inherent to ADO.NET pooling — CITED]` In the WPF app the pool and the caller
  were the *same process*, so `BackupService.cs:116` did real work. **In Option B it is a no-op.** The one
  in-code mitigation the restore path has is silently disabled by the architecture chosen.
- The recommendation's `[ASSUMED]` on `ClearAllPools` is aimed at the wrong property. The open question is
  not only "gate vs one-shot drain" — **in the two-process case it is not even a drain.**
- Best case: `File.Delete` at `SqliteOnlineBackup.cs:87` throws because SQLite's Windows VFS does not open
  with `FILE_SHARE_DELETE` → loud failure, live DB intact, orphan `.pre-restore` left behind.
  `[ASSUMED — reasoned from SQLite's win32 VFS share flags, not demonstrated]`
- Worst case: process 1's pool is empty at that instant → the delete succeeds → process 1 serves the next
  request through `SqliteConnectionFactory.cs:53` with `Mode = ReadWriteCreate` **mid-`BackupDatabase`** →
  the precise corruption the recommendation attributes to Option C, occurring in Option B.

**`dotnet run` does not reliably interlock either**: if the build is up to date it will not fail on a locked
output DLL, so the accidental "you must stop the app first" guard does not fire.

**(b) The file-sync client.** The codebase is written throughout as though this database sits in a synced
folder — `SqliteConnectionFactory.cs:15-18`, `SqliteOnlineBackup.cs:49-51` — and the DB *is* under
`Documents\`. **Pass 2 resolved pass 1's open question and it comes out in B's favour:** `shell:Personal`
resolves to `C:\Users\Admin\Documents`, i.e. **Documents is NOT OneDrive-redirected on this machine**
(a `C:\Users\Admin\OneDrive` root exists separately). `[VERIFIED — shell namespace query, this machine only;
NOT verified for whatever the production host is]` So a sync client holding or resurrecting deleted sidecars
is not a live failure mode here. The design tension remains — the API runs `SqliteProfile.Server` → WAL
(`SqliteConnectionFactory.cs:58-66`), writing `-wal`/`-shm` into the folder the Desktop profile exists to keep
them out of — but it is not a restore hazard on this box.

Net: B obtains **in-process** quiescence by construction and **cross-process** quiescence by the same operator
discipline Option A relies on. Still better than A — see the verdict — but the rationale overclaims, and the
overclaim is load-bearing.

---

## HOLE 6 — SERIOUS. The `Program.cs` entry point is shared with the test host, and `CreateBuilder(args)` has already eaten the args

The recommendation puts the guard block after config resolution at `:34-36`, and notes `args` is in scope at
`:10`. Both true. Two consequences it does not draw:

**(a) `WebApplicationFactory<Program>` drives this exact entry point.** `Program.cs:287` carries
`public partial class Program;` specifically so `ApiFactory.cs:31` can bind it. A path that `return`s before
`builder.Build()` at `:169` produces *"The entry point exited without ever building an IHost"* if it ever
fires under test. The factory passes no args so it should not fire — but this is the one file every deployment
**and every one of the ~489 API tests** runs through. `[VERIFIED for the mechanism at :287 / ApiFactory.cs:31;
ASSUMED for the exact exception text]`

**(b) `WebApplication.CreateBuilder(args)` at `:10` has already parsed the args into configuration before any
guard can see them.** .NET's command-line provider pairs a value-less `--flag` with the **next** argument. So
`dotnet run -- --list-backups --urls http://localhost:5080` makes `http://localhost:5080` the *value* of key
`list-backups`, and `--urls` is consumed as part of that pair rather than binding Kestrel.
`[ASSUMED — reasoned from the provider's documented key/value pairing; not executed]` A hand-rolled parse over
`args` is unaffected, but the guard and the framework now disagree about what was passed, silently and
order-dependently. Pin this down before writing the block, not after.

---

## HOLE 7 — SERIOUS. The safety copy is invisible to the tool meant to find it, and it evicts real backups

`BackupService.cs:122` writes `{dbPath}.pre-restore_{stamp}.bak` into the **DB folder**, not the backup folder.

- It does not match `ListBackups`' `timesheet_*.db` glob (`BackupService.cs:78`), so `--list-backups` — B's own
  discovery tool — will **never show the operator their own undo file.** `[VERIFIED]` The recommendation's
  promise that "the undo path is the path" holds only if the operator wrote the printed path down; the tool
  cannot recover it. **Combined with HOLE 1 step 6, this is what makes the empty-app outcome unrecoverable in
  practice rather than merely in principle.**
- It **does** match `DbBackupHelper`'s prune glob `timesheet.db.*.bak` (`DbBackupHelper.cs:55-58`), and that
  folder already holds **11** matching files against `KeepBackups = 10` (`DbBackupHelper.cs:20`) — pruning is
  live *right now* and the next bulk write deletes one. `[VERIFIED — file count]`
- The safety copy itself survives: ordinal-descending sort (`DbBackupHelper.cs:59`) puts `pre-restore_…` above
  every numeric stamp (`'p'` = 0x70 > every digit). But it permanently occupies a retention slot, so **each
  restore attempt — including each *failed* one, since `:122` runs before the failure at `:124` — silently
  evicts one more dated backup** from a set already at the limit. `[VERIFIED by reading the comparison]`

---

## HOLE 8 — MINOR. One cited "adjacent fact" is materially misleading

The recommendation states `POST /api/ops/backup/run` "no-ops and **returns 200 OK today**", implying a live
silent-success bug. The HTTP status claim is true (`SettingsEndpoints.cs:1051-1054` wraps a null in
`Results.Ok`). The implication is not:

```
settings.component.ts:554-557
    if (!r.value) { this.fail(new Error('Backup did not run — no file was written.')); return; }
```
with the comment `🔴 null/empty means the server ATTEMPTED NOTHING`, locked by a regression test at
`settings.component.spec.ts:446-455` labelled `🔴 BUG-2 (M9.2 / BK-02)`. `[VERIFIED]`

An admin clicking "Back up now" today gets an honest failure message. Presenting this as an open
silent-success defect risks a human spending M10 budget re-fixing something fixed in M9.2.

---

## HOLE 9 — SERIOUS, and it cuts *for* B. The trade-off the recommendation refuses to resolve is not currently live

The recommendation's central "I will not decide this" is: B needs a person at a console; if the 9am-Monday
operator is non-technical, C is the only reachable option.

In this deployment there is no such gap:

- `deploy-local.bat:38` — `dotnet run -c Release --urls http://localhost:5080` — **localhost only**.
  `deploy-local.bat:12-14` states LAN reachability is a *deferred* decision needing a `--urls` edit plus a
  firewall rule. `[VERIFIED — both passes]`
- `start-web.bat` — same, localhost. `[VERIFIED]`

**The only person who can open the web UI today is sitting at the host machine.** The web admin and the
console operator are the same human at the same keyboard. So Option C's reachability benefit — its entire
justification for accepting a destructive operation one click from production data — is worth approximately
nothing today, and the proposed middle path buys a directory listing obtainable from `dir`, at the cost of an
OpenAPI regen.

This does not break the recommendation; it means the human is being asked to weigh a trade-off that does not
yet exist. Re-open it if and when the deferred "who hosts it" decision makes this LAN-reachable.

---

## HOLE 10 — SERIOUS (pass 2, new). The recommended exclusivity guard is definitionally incapable of detecting a running server

The options doc calls this "the design, not a detail" and prefers a SQLite `BEGIN EXCLUSIVE` probe over a
port-bind probe because it "asks the **authority** rather than a proxy". Pass 1's amendment 4 accepted the
probe. **Both are wrong, and pass 1's own HOLE 5 line about "catches this only if process 1 holds a lock"
understated why.**

**In WAL mode, `BEGIN EXCLUSIVE` is defined to be identical to `BEGIN IMMEDIATE`** — it takes the write lock
and does *not* exclude readers. `[CITED — sqlite.org/lang_transaction.html: "EXCLUSIVE is the same as
IMMEDIATE in WAL mode"]` The API host runs `PRAGMA journal_mode=WAL` on every connection
(`SqliteConnectionFactory.cs:65`, Server profile set explicitly at `Program.cs:75-76`). `[VERIFIED]`

So the probe answers "is another connection mid-write *at this instant*?" An API host between requests — which
is what an API host is almost all of the time, sitting on idle pooled handles — has no write transaction open.
**The probe returns "clear" against a fully running, actively-serving production server.** It is not a weak
authority; it is a different proxy, answering a question nobody asked. The question that matters — "does any
process hold an open handle on this `.db`?" — has no SQLite API.

And the rejection reasoning does not fit this codebase. The port probe was dismissed because "a second
instance on another port defeats it" — true generically, but `deploy-local.bat:38` pins one process on one
fixed port `[VERIFIED]`, and no IIS, service or web-farm artifact exists in the repo. **For this deployment
the rejected proxy is the better-fitting one.**

Severity is serious rather than fatal only because the false green light most likely degrades to a loud
failure (HOLE 5's "best case") rather than corruption. But it hands the operator an authoritative-sounding
"clear" immediately before a destructive operation.

---

## HOLE 11 — FATAL as specified (pass 2, new). The CLI can silently restore into the wrong database and report success

Three verified facts compose into a live silent-success path that neither the options doc nor pass 1 caught:

1. `JsonAppConfig`'s default constructor resolves **per-Windows-user** paths: `DefaultConfigPath()` →
   `%APPDATA%\TimesheetApp\appsettings.json` (`JsonAppConfig.cs:172-176`), `DefaultDbPath()` →
   `MyDocuments\TimesheetApp\timesheet.db` (`JsonAppConfig.cs:178-182`). A missing config file is swallowed
   and the default applied (`:161` returns null, `:57` falls back). `[VERIFIED]`
2. The recommendation places the CLI branch "right after config resolution at `:34-36`" — which is **before**
   the startup banner at `Program.cs:57-60`, the only thing in the system that prints which database was
   opened. That banner exists precisely because this failure class already bit this project: *"A second
   machine that 'cannot log in' almost always means the API opened a DIFFERENT database than expected"*
   (`Program.cs:52-53`). **As specified, the CLI skips it.** `[VERIFIED]`
3. If the resolved `dbPath` does not exist, `RestoreAsync` **skips the safety copy** (`BackupService.cs:121`
   gates on `File.Exists(dbPath)`) and calls `SqliteOnlineBackup.Copy(backupPath, dbPath)`, which opens the
   destination `ReadWriteCreate` (`SqliteOnlineBackup.cs:44`) and **creates it**. `[VERIFIED]`

Compose them: an operator running the restore under a different Windows account (service account, another
admin's RDP session) gets a fresh database created at a path nobody uses, exit code 0, and — because the
safety-copy branch was skipped — **the one line the doc says the tool prints (the resulting `.pre-restore_*.bak`
path) is not printed either.** The exact case where the wrong path was used is the exact case where the tool
prints nothing that would reveal it. Production data is untouched; the operator believes the restore ran.

The claim under attack is: *"Reusing `Program.cs`'s own config resolution is the point — it guarantees the CLI
opens the SAME `.db` the server does."* It guarantees the same resolution **logic**, not the same **result**.
The result depends on the invoking account and environment, which the recommendation never states.

Fatal against the specification, cheap to fix — but the fix is not in the current spec, and it is mandatory.

---

## HOLE 12 — SERIOUS (pass 2, new). "There are no backups to restore from today" is wrong, and the reason is a live bug

The options doc offers this to the human as a finding that "may outrank this blocker entirely". It is
materially false, and pass 1 got the direction wrong too (see HOLE 3).

`NoOpDbBackupHelper` exists for exactly one purpose, stated in its own summary: *"M8.2 (spec §9) — the
`IDbBackupHelper` **the API host registers**. Does nothing and returns no path… `DbBackupHelper` snapshots the
ENTIRE database before every bulk write… **On a server it is a disaster**"* (`NoOpDbBackupHelper.cs:4-14`).

**It is not registered anywhere.** Grep over `src/` returns three hits: its own declaration, and one unit test
(`NoOpServicesTests.cs:21,23`). Zero composition roots. `[VERIFIED]` `Program.cs:103` registers the real one:
`AddSingleton<IDbBackupHelper, DbBackupHelper>()`. `[VERIFIED]`

Consequences:

- The API host takes a **full online backup of the entire database before every bulk write** —
  `TimeLogService.cs:142` and `:199` (Smart Fill apply), `DefaultTaskSyncService.cs:48`,
  `TeamBootstrapService.cs:90`, `RetentionService.cs:132`. `[VERIFIED]`
- Those artifacts are written as `{dbPath}.{stamp}.bak` (`DbBackupHelper.cs:38`) via `SqliteOnlineBackup.Copy`
  (`:42`) — so they are `journal_mode=DELETE` (`SqliteOnlineBackup.cs:52`), self-contained,
  integrity-checkable databases, pruned to the newest 10 (`:20`). `[VERIFIED]`
- `RestoreAsync` validates only `File.Exists(backupPath)` (`BackupService.cs:99`), so **these files are
  directly restorable by every option under consideration.** `[VERIFIED]`
- Eleven exist right now in `C:\Users\Admin\Documents\TimesheetApp\`, including a
  `timesheet.db.20260715-pre-v11.bak`. `[VERIFIED — directory listing]`

The true statement is narrower and much less dramatic: *no `IBackupService` backups exist, because
`BackupFolderPath` is null.* **"A restore path with nothing to restore from is ceremony" does not hold**, and
the human should not deprioritise B2 on the strength of it.

**Separate bug surfaced, deserving its own ticket regardless of B2:** the server is doing a full-database copy
per Smart Fill apply, per user, all day — exactly what `NoOpDbBackupHelper.cs:8-10` was written to prevent and
calls "a disaster". This also makes Option C's background-work problem worse than the options doc states, for
the retention path specifically (`SettingsEndpoints.cs:1027` `_ = Task.Run` → `RetentionService.cs:132` opens
the DB for a full backup *before* its `BEGIN IMMEDIATE`) — though note the doc's broader claim is imprecise:
the `TimeLogService` / `DefaultTaskSyncService` calls are `await`ed inside the request and *would* be visible
to a request counter. `[VERIFIED]`

---

## HOLE 13 — SERIOUS (pass 2, new). §1.9 — the load-bearing argument against Option C — is unreconciled with §1.8

§1.9 (the doc's self-declared "single most important inference") requires `DeleteWithSidecars` to **succeed**
while the server holds handles, opening a window in which a fresh request auto-creates an empty WAL database
at the production path.

§1.8 lists "whether `File.Delete` on the live `.db` throws or silently succeeds while another handle is open"
as **COULD NOT DETERMINE**. And `SqliteOnlineBackup.cs:36-37` asserts the opposite of §1.9's premise:
*"Callers must ensure no handle is still open on the destination — otherwise this **throws rather than
silently corrupting it**."* `[VERIFIED both]`

If the delete throws, §1.9's race cannot start in the pooled case, and Option C's headline risk falls from
*"silent corruption of production data by a browser click"* to *"a 500 and an orphan `.pre-restore` file"*.
That is the difference between "C is unsafe at any speed" and "C is merely expensive" — and C is the only
option the non-technical operator the doc worries about could run.

The doc never reconciles these two sections, and it is the single argument doing the most work against C.
**Resolving it is one throwaway experiment** — open a WAL connection with `Pooling=true` on a scratch
database, attempt `File.Delete` — and it should be run before this decision is made. On a scratch database.
Never on production.

---

## HOLE 14 — MINOR (pass 2, new). Recovery tooling that requires a compiler; and the restore is partial by construction

**(a) `dotnet run` at recovery time.** `dotnet run --project src/TimesheetApp.Api -- --restore "..."` triggers
an MSBuild build and a NuGet restore during a data emergency. The host does run from source
(`deploy-local.bat:37-38`) `[VERIFIED]`, so the SDK is present — but a broken working tree or an offline feed
would then block recovery, and Option A's file copy has no such dependency. **Fix:** invoke the already-built
output (`dotnet TimesheetApp.Api.dll --restore …`) and say so in the runbook.

**(b) The artifact is the `.db` only.** Not restored by any option: the Data Protection key ring
(`Program.cs:40-47`; observed at `C:\Users\Admin\Documents\TimesheetApp\keys\key-*.xml`), `StandupArchives\`,
and the retention archive root (`ArchivePath: E:\Learning\vite-version\test`). `[VERIFIED]` Two
operator-visible consequences belong in whichever runbook ships: user accounts and password hashes roll back
with the database while **issued cookies stay valid** (the key ring is untouched), so a user
deleted-by-restore presents a valid cookie and is 401'd with no explanation; and on-disk retention archives
now describe rows the restored database still contains, so a re-run of retention re-archives them.

---

## What actually checks out (so this is not read as uniformly negative)

- **Schema migration across a restore works.** `DatabaseInitializer.cs:15` `SchemaVersion = 11`; migrations are
  forward-only additive gated on `PRAGMA user_version` (`:227-229`), and the code documents that "an old
  client opening a newer DB still works" (`:229`). A restored pre-v11 backup migrates forward on next start —
  which matters, because a `timesheet.db.20260715-pre-v11.bak` is in the DB folder right now.
  `[VERIFIED — both passes]`
- **The journal-mode round trip works.** `SqliteOnlineBackup.cs:52` leaves the restored file
  `journal_mode=DELETE`; `SqliteConnectionFactory.cs:65` re-issues `PRAGMA journal_mode=WAL` on every
  connection. `[VERIFIED]`
- **`Documents` is not OneDrive-redirected on this machine**, closing pass 1's open sync-client question.
  `[VERIFIED — this machine only]`
- **The recommendation's SignalR correction is right, and its concern is right.** `DataHub.cs:34-45` touches
  the DB once per connect via a per-call repository; `DataHub.cs:20-23` documents that reconnects re-join and
  re-query. `[VERIFIED]`
- **B can construct `BackupService` directly, as claimed.** Its ctor is `(IAppConfig, IClock)`,
  `BackupService.cs:29-33`. `[VERIFIED]`
- **Rationale (3) is real.** `settings.component.ts:40-61` is an explicit ruling that host-level state belongs
  in `appsettings.json` where "exactly one person can change them and a restart makes it so", naming
  `SetDbPath` as the thing that must never be browser-reachable. `SettingsEndpoints.cs:43-44` independently
  records that `RestoreAsync` "is NOT exposed in M8.3". B is consistent with two prior rulings; C reverses
  both. `[VERIFIED]`
- **Rationale (4) is real.** No route, no contract, no client regen. `[VERIFIED]`

One correction to the doc's own middle path, on the way past: `GET /api/ops/backup/list` would return an
**empty list** today (HOLE 3) and would never show the 11 artifacts that do exist (HOLE 12) — wrong folder,
wrong glob. Shipped as specified, the mitigation designed to close the visibility gap tells the operator "no
backups" while eleven restorable snapshots sit on disk. It also returns full host filesystem paths
(`IBackupService.cs:5`; `BackupService.cs:84`), arguably the same category `settings.component.ts:45-60` is
cited to forbid. And its cost is overstated: `SettingsEndpoints.cs:1010-1013` sets precedent for passing a
Core type straight through with `.Produces<RetentionPreview>()`, so no new `Contracts/` type is needed.

---

## Verdict

**The recommendation's conclusion survives. Its specification does not.**

Option B is still the right shape, for one reason that survived both passes intact: it converts the two
silent, unrecoverable *human* failure modes of Option A — forgetting the sidecars, forgetting the safety copy
— into code that cannot be forgotten (`SqliteOnlineBackup.cs:41,85-90`; `BackupService.cs:121-122`). HOLE 9
strengthens it further: the accessibility objection that was the main argument for C is inert in a
localhost-only deployment. HOLE 13 does not make C cheap; it only makes C less catastrophic than advertised.

But B as specified would ship a command that can permanently destroy the production database and then, on the
next startup, silently rebuild a fully-functional empty one (HOLE 1); or silently restore into a phantom
database under the wrong Windows account and print nothing that reveals it (HOLE 11); guarded by a probe that
cannot detect a running server (HOLE 10); documented by a runbook pointing at the wrong folder (HOLE 4);
justified by tests the same milestone deletes (HOLE 2).

**Replace with: Option B + six mandatory amendments, sequenced after the backup-production decision.**

1. **`IsIntact(backupPath)` as a hard precondition inside `RestoreAsync`** — before `Copy`, therefore before
   `DeleteWithSidecars`. In `BackupService`, not the CLI wrapper, so A and any future C inherit it. Add
   `IsIntact(dbPath)` after, and a non-zero exit code on either failure. (HOLE 1.)
2. **Move the CLI branch to after `Program.cs:60`; echo the resolved `DbPath` and config path; refuse to
   proceed when the live `.db` is absent** rather than creating one. (HOLE 11 — non-negotiable.)
3. **Drop `BEGIN EXCLUSIVE` as the primary guard.** It is a no-op against WAL. Use a port-bind probe on the
   configured URL — better-fitting for this single-process, fixed-port deployment — and present it to the
   operator as *advisory*. The runbook's "close the server window and confirm it is gone" step stays
   load-bearing. (HOLE 10.)
4. **Strike "quiescence by construction", and any implication that `ClearPools()` protects the operator.**
   B earns in-process quiescence by construction and cross-process quiescence by discipline, same as A; say so
   in the runbook. (HOLES 5, 10.)
5. **Decide the test relocation before writing the command** — move `WalBackupSafetyTests` /
   `BackupServiceTests` / `TinyDb` to a `net8.0` home (Moq required) and budget it. Adding a corrupt-source
   restore test is the natural place to prove amendment 1. (HOLE 2.)
6. **The runbook names `C:\Users\Admin\Documents\TimesheetApp\`** — resolved from `appsettings.json:2`, not
   assumed from `%APPDATA%` — instructs the operator to confirm it against the startup banner
   (`Program.cs:57-60`) before touching anything, and invokes the built assembly rather than `dotnet run`.
   (HOLES 4, 14.)

**Correct the evidence before the human reads it:** §1.7(a) is wrong — restorable full-DB snapshots exist
today (HOLE 12) — and §1.7(b) is understated in the other direction: auto-backup is already switched off by
config, so M10 deletes the caller of something already inert (HOLE 3).

**Four things remain the human's to decide and I have not decided them:**

- **Sequencing.** B's discovery half is dead on this machine until `BackupFolderPath` is set and something
  replaces the `App.xaml.cs:63` scheduler. I would do that first and B second; the counter-argument — a
  restore path is cheap insurance to have written before you need it — is legitimate and I am not overriding
  it.
- **Run the HOLE 13 experiment first.** If `File.Delete` throws on an open handle, Option C's data-safety
  objection collapses to a cost objection and the operational-capability side of the trade gets materially
  stronger. Deciding B-vs-C before running it is deciding on an unknown that costs ten minutes to make known.
- **Whether amendments 1 and 2 block M10 or ship with it.** Amendment 1 changes surviving Core code
  (`BackupService`), which M10 was scoped to leave untouched.
- **Whether to re-open Option C when the deferred "who hosts it" decision (`deploy-local.bat:12-14`) is made.**
  Today C buys nothing; on a LAN-reachable host it buys something real, at the cost both passes describe.
