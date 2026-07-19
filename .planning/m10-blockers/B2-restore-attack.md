# B2-restore — Attack on the recommendation

**Target:** Option B (offline CLI restore mode in the existing API host), with Option A's runbook folded in.

**Verdict in one line:** the *shape* of Option B survives — it is still the right place to put a restore — but **the recommendation as written does not**, because it recommends shipping a command that calls a `RestoreAsync` which deletes the production database before it has established that the file replacing it is a database at all, and it rests two of its four rationales on tests that M10 is about to delete.

Everything below carries `file:line`. `[VERIFIED]` = I read it. `[ASSUMED]` = I inferred it and could not demonstrate it.

---

## HOLE 1 — FATAL. Restore destroys the live database *before* validating the source, and the failure silently rebuilds an empty app

This is the single largest defect and the recommendation does not mention it.

`SqliteOnlineBackup.Copy` deletes the destination first and opens the source second:

```
SqliteOnlineBackup.cs:41   DeleteWithSidecars(destDbPath);          // live .db + -wal + -shm GONE
SqliteOnlineBackup.cs:43   using var source = Open(sourceDbPath, SqliteOpenMode.ReadWrite);
```
`[VERIFIED]` — `DeleteWithSidecars` is `SqliteOnlineBackup.cs:85-90`; it is three unconditional `File.Delete` calls.

`RestoreAsync` validates the source with **exactly two checks**, neither of which is "is this a database":
- `BackupService.cs:99` — `File.Exists(backupPath)`
- `BackupService.cs:106-108` — not the live DB onto itself

`[VERIFIED]`. There is no integrity check. And the codebase *has* one: `SqliteOnlineBackup.IsIntact` (`SqliteOnlineBackup.cs:64-79`), whose own doc-comment says `"Exists && Length > 0" is not evidence that a file is a usable database — six arbitrary bytes pass it`. Its production callers are: **one** — `PruneArchiver.cs:178`, where it gates permanent row deletion. `[VERIFIED — grep across src/, excluding bin/obj]`

So the more destructive of the two operations is the one that skips the check.

**The failure chain, end to end:**

1. Operator runs `-- --restore "<some .bak>"` against a truncated / half-synced / not-actually-a-database file.
2. `BackupService.cs:122` writes the `.pre-restore_{stamp}.bak` safety copy. Good.
3. `BackupService.cs:124` → `Copy` → `SqliteOnlineBackup.cs:41` **deletes `timesheet.db`, `timesheet.db-wal`, `timesheet.db-shm`**.
4. `SqliteOnlineBackup.cs:43` throws `SqliteException` ("file is not a database").
5. **There is now no production database.** No rollback exists in the code path.
6. Operator restarts via `deploy-local.bat`. `SqliteConnectionFactory.cs:53` opens with `Mode = ReadWriteCreate` → **creates a new empty file**. `Program.cs:176` `InitializeAsync()` builds the full v11 schema. `Program.cs:179` `EnsureBootstrappedAsync()` creates a team. `Program.cs:181` `AdminBootstrap.EnsureAdminPasswordAsync` seeds `admin`/`admin` — and it is **on by default in production**: `AdminBootstrap.cs:36-37` is `!bool.TryParse(config?["TimesheetApp:SeedFirstAdmin"], out var enabled) || enabled`, so an absent key evaluates true. `[VERIFIED]`
7. The startup banner (`Program.cs:57-60`) prints the correct DB path. The browser shows a working, logged-in, **completely empty** application.

The operator's evidence that the restore failed is a stack trace in a console window they have already closed and restarted. The evidence that it "worked" is a functioning app. **This is the third instance of the exact failure mode the brief says must not be let in — and unlike the first two it is unrecoverable in effect, because the `.pre-restore` file is the only survivor and nothing points at it.**

Note this defect lives in `RestoreAsync`, which A, B and C all call, so it does not *select between* the options. But it directly contradicts rationale (2) of the recommendation — "it does not extend the code into an untested regime". Handing an operator a command-line flag that accepts **an arbitrary path** is an extension: the only shipped caller, `SettingsViewModel.cs:293-305`, takes a `BackupInfo` and can therefore only ever pass a path that `ListBackups()` already produced and parsed (`BackupService.cs:78-80`). `--restore "<path>"` removes that constraint. `[VERIFIED]`

**Required amendment before B ships:** `IsIntact(backupPath)` as a hard precondition inside `RestoreAsync` (not in the CLI wrapper — A and any future C must inherit it), plus a post-restore `IsIntact(dbPath)` and a non-zero exit code. Both are cheap; neither is in the recommendation.

---

## HOLE 2 — SERIOUS. Two of the four rationales rest on tests that M10 deletes

The recommendation cites `WalBackupSafetyTests.cs:87` and `TinyDb.cs:86` as its evidence that restore is safe in B's regime. Both claims check out `[VERIFIED]` — the restore at `:87` is outside the `using` block that closes at `:81`, and `TinyDb.cs:86` sets `Pooling = false`.

But both files live in `src/TimesheetApp.Tests/`, and:

```
src/TimesheetApp.Tests/TimesheetApp.Tests.csproj:21
    <ProjectReference Include="..\TimesheetApp\TimesheetApp.csproj" />
```
`[VERIFIED]`

M10 deletes `src/TimesheetApp/`. **That project reference then dangles and `TimesheetApp.Tests` does not compile.** The coupling is not incidental: 18 files in that project reference WPF namespaces and there are 13 files under `ViewModels/`. `[VERIFIED — grep + ls]`

So the entire evidentiary basis for "restore is safe when quiesced" — `WalBackupSafetyTests` (WAL-consistent snapshot, stale-`-wal` removal) and `BackupServiceTests` (missing-file rejection, self-restore rejection, list ordering) — is inside the blast radius of the milestone this decision belongs to. Nothing in the recommendation mentions moving them.

Relocating them is not free either: `TimesheetApp.ApiTests` is `Microsoft.NET.Sdk.Web` / `net8.0` and has **no Moq package reference** `[VERIFIED — TimesheetApp.ApiTests.csproj]`, while `WalBackupSafetyTests.cs:47-53` builds its fixture on `Mock<IAppConfig>` / `Mock<IClock>`. Either Moq is added there, or a new `net8.0` Core-test project is created, or `TimesheetApp.Tests` is gutted down to a Core-only reference. All three are real M10 work that B's stated cost ("2-3 tests, one doc... half a day to a day") does not contain.

---

## HOLE 3 — SERIOUS. On the actual production machine, `--list-backups` prints nothing and `--restore` has no valid input

The recommendation files this under "two adjacent facts that may outrank this blocker entirely" and explicitly declines to fold it in. I think that is the wrong call, because it is not adjacent to Option B — **it is Option B's input.**

- `%APPDATA%\TimesheetApp\appsettings.json:4` → `"BackupFolderPath": null` `[VERIFIED]`
- Therefore `BackupService.cs:74-75` returns `Array.Empty<BackupInfo>()`. `--list-backups` prints an empty list. `[VERIFIED]`
- `%APPDATA%\TimesheetApp\appsettings.json:5` → `"AutoBackupEnabled": false` `[VERIFIED]`. This is **stronger than the recommendation's fact (b)**: scheduled backup is not merely losing its caller at `App.xaml.cs:63`, it is already switched off by configuration. `AutoBackupIfDueAsync` returns `false` at `BackupService.cs:61` before it reaches the folder check.
- The only backup artifacts that exist on disk are **11** files named `timesheet.db.{stamp}.bak` in `C:\Users\Admin\Documents\TimesheetApp\`, produced by `DbBackupHelper.cs:38`. `[VERIFIED — directory listing]`
- Those do **not** match `ListBackups`' glob `timesheet_*.db` (`BackupService.cs:22-23, :78`), and are not in a `BackupFolderPath`. `[VERIFIED]`

B therefore ships: a list command that returns nothing, and a restore command whose only real-world inputs on this machine are files it was never designed to enumerate, validate, or order. Combined with HOLE 1 (arbitrary path + no integrity check), the realistic first use of this feature is an operator hand-typing a path to a `.bak` file of unverified provenance while the app's own safety net is switched off.

**B should be sequenced after the "we are producing no backups at all" decision, not before it.**

---

## HOLE 4 — SERIOUS. The runbook half points at the wrong directory

The recommendation (and the brief) anchor on `%APPDATA%\TimesheetApp\appsettings.json`. That is correct for the **config**. It is not where the **database** is.

```
%APPDATA%\TimesheetApp\appsettings.json:2
    "DbPath": "C:\\Users\\Admin\\Documents\\TimesheetApp\\timesheet.db"
```
`[VERIFIED]` — and this matches the code default: `JsonAppConfig.cs:178-182` resolves `DefaultDbPath()` from `SpecialFolder.MyDocuments`, while `JsonAppConfig.cs:172-176` resolves the config from `SpecialFolder.ApplicationData`. **They are deliberately different folders.** `%APPDATA%\TimesheetApp\` contains one file, `appsettings.json`. `[VERIFIED — directory listing]`

Option A's steps 3, 4 and 5 — the ones the recommendation says must be written *as part of* B — are all "go to the DB folder and copy/delete these files". A runbook that names `%APPDATA%` sends the operator to a directory with no database, no `-wal`, no `-shm`. Step 4 ("delete the sidecars") deletes nothing and reports success. Step 3 ("copy the current DB aside") copies nothing. The operator then performs step 5 against the *real* path or not at all, and in either case believes the preceding safety steps were done.

This is a runbook that is silently a no-op for its safety steps and load-bearing only for its destructive one. B's code reads `appConfig.DbPath` programmatically so the code is immune — **the document is not**, and the recommendation bundles the document into B.

---

## HOLE 5 — SERIOUS. "Quiescence by construction" is true of Kestrel and of nothing else

This is rationale (1), the recommendation's lead argument. It is narrower than stated.

B guarantees *this process* opens no connections. It does not guarantee **no other process holds the file**, which the recommendation concedes — but it then treats the exclusivity probe as sufficient. Two other processes are in scope here and neither is bounded by a probe:

**(a) A second instance of the app.** Nothing prevents `dotnet run --project src/TimesheetApp.Api -- --restore X` while `deploy-local.bat:38`'s `dotnet run -c Release --urls http://localhost:5080` is live. In that case:

- `SqliteOnlineBackup.ClearPools()` (`SqliteOnlineBackup.cs:83`) is `SqliteConnection.ClearAllPools()` — a **per-process** static. It cannot touch process 1's pooled handles. `[VERIFIED — it is a plain static call; the per-process scope is inherent to ADO.NET pooling]`
- The recommendation's unresolved `[ASSUMED]` on `ClearAllPools` is aimed at the wrong property. The open question is not only "gate vs. one-shot drain" — it is that **in the two-process case it is not even a drain.**
- Best case: `File.Delete` at `SqliteOnlineBackup.cs:87` throws because SQLite's Windows VFS does not open with `FILE_SHARE_DELETE` → loud failure, live DB intact, an orphan `.pre-restore` left behind. `[ASSUMED — reasoned from SQLite's win32 VFS share flags, not demonstrated here]`
- Worst case: process 1's pool happens to be empty at that instant (idle since startup, or post-eviction) → the delete succeeds → process 1 serves the next request through `SqliteConnectionFactory.cs:53` with `Mode = ReadWriteCreate` **mid-`BackupDatabase`** → the precise corruption the recommendation attributes to Option C, in Option B.

A `BEGIN EXCLUSIVE` probe catches this only if process 1 holds a lock at probe time. Against an idle WAL server it returns clean and the operator proceeds. **`dotnet run` also does not reliably interlock**: if the build is up-to-date it will not fail on a locked output DLL, so the accidental "you must stop the app first" guard does not fire.

**(b) The file-sync client.** The codebase is written throughout as though this database sits in a synced folder, and the DB *is* under `Documents\`:
- `SqliteConnectionFactory.cs:15-18` — `journal_mode=DELETE (NOT WAL -> no -wal/-shm sidecars to sync out of band over OneDrive), Pooling=False (Dispose truly releases the file handle so OneDrive can upload)`
- `SqliteOnlineBackup.cs:49-51` — `a read-only share or a synced backup folder ... exactly where these files go`
- Retention aborts outright on OneDrive conflict copies (`M10-COVERAGE-AUDIT.md`, XC-08 row)

A sync client is a second process that opens, locks and *resurrects* files, and "Kestrel never listens" does nothing to it. Whether `C:\Users\Admin\Documents` is OneDrive-redirected on this machine I **could not determine** `[ASSUMED]` — but the API runs `SqliteProfile.Server` → WAL (`SqliteConnectionFactory.cs:58-66`), so it is now writing `-wal`/`-shm` sidecars into exactly the folder the Desktop profile exists to keep them out of.

Net: B obtains **in-process** quiescence by construction and **cross-process** quiescence by the same operator discipline Option A relies on. That is still better than A and C, but the rationale as stated overclaims, and the overclaim is load-bearing for the recommendation.

---

## HOLE 6 — SERIOUS. The `Program.cs` entry point is shared with the test host, and `CreateBuilder(args)` has already eaten the args

The recommendation says the guard block goes after config resolution at `:34-36`, and that `args` "is already in scope at `:10`". Both true. Two consequences it does not draw:

**(a) `WebApplicationFactory<Program>` drives this exact entry point.** `Program.cs:287` carries `public partial class Program;` specifically so `ApiFactory.cs:31` (`WebApplicationFactory<Program>`) can bind it. A code path that `return`s before `builder.Build()` at `:169` produces *"The entry point exited without ever building an IHost"* if it ever fires under test. The factory passes no args so it should not fire — but this is the one file every deployment **and every one of the ~489 API tests** runs through. `[VERIFIED for the mechanism at :287/ApiFactory.cs:31; [ASSUMED] for the exact factory exception text]`

**(b) `WebApplication.CreateBuilder(args)` at `:10` has already parsed the args into configuration before any guard can see them.** .NET's command-line configuration provider pairs a value-less `--flag` with the **next** argument. So:

```
dotnet run -- --list-backups --urls http://localhost:5080
```
makes `http://localhost:5080` the *value* of key `list-backups`, and `--urls` is silently consumed as part of that pair rather than binding Kestrel. `[ASSUMED — reasoned from the provider's documented key/value pairing; not executed]` A hand-rolled parse over `args` is unaffected, but the guard and the framework now disagree about what was passed, and the disagreement is order-dependent and silent. Worth pinning down before writing the block, not after.

---

## HOLE 7 — MINOR/SERIOUS. The safety copy is invisible to the tool that is supposed to find it, and it evicts real backups

`BackupService.cs:122` writes `{dbPath}.pre-restore_{stamp}.bak`, i.e. into the **DB folder**, not the backup folder.

- It does not match `ListBackups`' `timesheet_*.db` glob (`BackupService.cs:78`), so `--list-backups` — B's own discovery tool — will **never show the operator their own undo file.** `[VERIFIED]` The recommendation's promise that "the undo path is the path" is therefore only true if the operator wrote the printed path down; the tool cannot recover it.
- It **does** match `DbBackupHelper`'s prune glob `timesheet.db.*.bak` (`DbBackupHelper.cs:55-58`), and that folder already holds **11** matching files against `KeepBackups = 10` (`DbBackupHelper.cs:20`) — so pruning is live *right now* and the next bulk write deletes one. `[VERIFIED — file count]`
- The safety copy itself survives: ordinal-descending sort (`DbBackupHelper.cs:59`) puts `pre-restore_…` above every numeric stamp. But it permanently occupies a retention slot, so **each restore silently evicts one more dated backup** from a set that is already at the limit. `[VERIFIED by reading the comparison; not executed]`

---

## HOLE 8 — MINOR. One cited "adjacent fact" is materially misleading

The recommendation states that `POST /api/ops/backup/run` "no-ops and **returns 200 OK today**", implying a live silent-success bug. The HTTP status claim is true (`SettingsEndpoints.cs:1051-1054` wraps a null in `Results.Ok`). The implication is not:

```
settings.component.ts:554-557
    if (!r.value) { this.fail(new Error('Backup did not run — no file was written.')); return; }
```
with the comment `🔴 null/empty means the server ATTEMPTED NOTHING`, locked by a regression test at `settings.component.spec.ts:446-455` labelled `🔴 BUG-2 (M9.2 / BK-02)`. `[VERIFIED]`

An admin clicking "Back up now" today gets an honest failure message. Presenting this as an open silent-success defect risks a human spending M10 budget re-fixing something fixed in M9.2.

---

## HOLE 9 — SERIOUS, and it cuts *for* B. The trade-off the recommendation refuses to resolve is not currently live

The recommendation's central "I will not decide this" is: B needs a person at a console; if the 9am-Monday operator is non-technical, C is the only reachable option.

In this deployment there is no such gap:

- `deploy-local.bat:38` — `dotnet run -c Release --urls http://localhost:5080` — **localhost only**. `deploy-local.bat:12-14` states that LAN reachability is a *deferred* decision needing a `--urls` edit plus a firewall rule. `[VERIFIED]`
- `start-web.bat:39` — same, localhost. `[VERIFIED]`

**The only person who can open the web UI today is sitting at the host machine.** The web admin and the console operator are the same human at the same keyboard. So:

- Option C's reachability benefit — its entire justification for accepting a destructive operation one click from production data — is worth approximately nothing today.
- The proposed middle path (`GET /api/ops/backup/list`) buys the operator a directory listing they could get from `dir`, at the cost of an OpenAPI regen.

This does not break the recommendation; it means the human is being asked to weigh a trade-off that does not yet exist. If and when the deferred "who hosts it" decision makes this LAN-reachable, the trade-off becomes real and should be re-opened then.

---

## What actually checks out (so this is not read as uniformly negative)

- **Schema migration across a restore works.** `DatabaseInitializer.cs:15` `SchemaVersion = 11`; migrations are forward-only additive gated on `PRAGMA user_version` (`:227-228`, `:359-368`). A restored pre-v11 backup migrates forward on next start. This matters because a `timesheet.db.20260715-pre-v11.bak` is sitting in the DB folder right now. `[VERIFIED]`
- **The journal-mode round trip works.** `SqliteOnlineBackup.cs:52` leaves the restored file in `journal_mode=DELETE`; `SqliteConnectionFactory.cs:65` re-issues `PRAGMA journal_mode=WAL` on every connection. `[VERIFIED]`
- **The recommendation's SignalR correction is right, and its concern is right.** `DataHub.cs:39` touches the DB once per connect via a per-call repository; `DataHub.cs:20-23` documents that reconnects re-join and re-query. `[VERIFIED]`
- **Rationale (3) is real.** `settings.component.ts:40-61` is an explicit ruling that host-level state belongs in `appsettings.json` where "exactly one person can change them and a restart makes it so", and it names `SetDbPath` as the thing that must never be browser-reachable. `SettingsEndpoints.cs:43` independently records that `RestoreAsync` "is NOT exposed in M8.3". B is consistent with two prior rulings; C reverses both. `[VERIFIED]`
- **Rationale (4) is real.** No route, no contract, no client regen. `[VERIFIED]`

---

## Verdict

**The recommendation's conclusion survives. Its specification does not.**

Option B is still the right shape, and HOLE 9 strengthens it — the accessibility objection that was the main argument for C is inert in a localhost-only deployment. But B as specified would ship a command that can permanently destroy the production database and then, on the next startup, silently rebuild a fully-functional empty one (HOLE 1), documented by a runbook pointing at the wrong folder (HOLE 4), justified by tests the same milestone deletes (HOLE 2), for a machine that has no valid backups to restore from (HOLE 3).

**Replace with: Option B + four mandatory amendments, sequenced after the backup-production decision.**

1. **`IsIntact(backupPath)` as a hard precondition inside `RestoreAsync`** — before `Copy`, therefore before `DeleteWithSidecars`. Put it in `BackupService`, not the CLI wrapper, so A and any future C inherit it. Add `IsIntact(dbPath)` after, and a non-zero exit code on either failure. (Closes HOLE 1.)
2. **Decide the test relocation before writing the command** — move `WalBackupSafetyTests` / `BackupServiceTests` / `TinyDb` to a `net8.0` home and budget it. Adding a corrupt-source restore test is the natural place to prove amendment 1. (Closes HOLE 2.)
3. **The runbook names `C:\Users\Admin\Documents\TimesheetApp\`** — resolved from `appsettings.json:2`, not assumed from `%APPDATA%` — and instructs the operator to confirm it against the startup banner at `Program.cs:57-60` before touching anything. (Closes HOLE 4.)
4. **Ship the exclusivity probe as `BEGIN EXCLUSIVE` plus an explicit "stop the app and the sync client" instruction**, and drop the "quiescence by construction" framing from the rationale — B earns in-process quiescence by construction and cross-process quiescence by discipline, same as A. (Closes HOLE 5; HOLE 6 is a code-review item on the guard block.)

**Three things remain the human's to decide and I have not decided them:**

- **Sequencing.** HOLE 3 says B is unusable on this machine until backups are actually being produced (`BackupFolderPath` set, and something replacing the `App.xaml.cs:63` scheduler). I would do that first and B second; the counter-argument — that a restore path is cheap insurance to have written before you need it — is legitimate and I am not overriding it.
- **Whether amendment 1 blocks M10 or ships with it.** It is a change to surviving Core code (`BackupService`), which M10 was scoped to leave untouched.
- **Whether to re-open Option C when the deferred "who hosts it" decision (`deploy-local.bat:12-14`) is made.** Today C buys nothing; on a LAN-reachable host it buys something real, at the cost this document and the prior pass both describe.
