# B4-missing — Adversarial attack, pass 2 (on the REVISED recommendation)

**Target:** the current `B4-missing-options.md` recommendation — *Option B, shape **B-ii** (out-of-process CLI
+ Windows Task Scheduler), with the post-M10 milestone created in the same sitting as the deletion commit*,
and its stated lean toward config-fork shape **(b)+(c)**.

**This file supersedes the pass-1 attack** (H1-H8), whose findings the revised options file re-derived and
absorbed. **None of H1-H8 is repeated here.** Everything below is new, found by opening files the previous
two passes did not open, and it attacks the *revision*, not the version the revision already fixed.

**Method.** `[VERIFIED]` = I read that line in this pass. `[ASSUMED]` = inferred, and labelled where the
inference is about Windows/SQLite behavior rather than about this repo. No code was edited, no build or test
was run, no SQLite file was opened.

**Bottom line up front.** The split-gate *shape* survives a second attack — I tried to break it and could not.
What does not survive is **the two sub-decisions the recommendation leaned on**: shape **B-ii** and config fork
**(b)**. Both are broken by the same underlying property of this codebase — *`JsonAppConfig` treats an
unreadable config as an empty one and silently repoints the production database* — and B-ii additionally
splits the backup writer from the backup viewer, which is the one thing P3 exists to prevent. There is also a
**live, shipped instance of "the operator believes it worked"** in the export path (the fourth in this app,
and the first one nobody has reported yet), sitting in the exact subsystem P1 proposes to run unattended.

---

## N1 — FATAL. The config fork has no safe branch, because an unreadable config silently repoints the production database

This is the single most important finding in this pass. It kills the recommendation's stated lean, and it also
kills the alternative it was leaning away from.

### The chain, verified end to end

```
JsonAppConfig.cs:166-169   catch (JsonException) { return null; }   // corrupt config -> fall back to default
JsonAppConfig.cs:57        _dbPath = model?.DbPath ?? defaultDbPath;
JsonAppConfig.cs:178-181   DefaultDbPath() => Documents\TimesheetApp\timesheet.db
```
[VERIFIED: all three, `src/TimesheetApp.Core/Config/JsonAppConfig.cs`]

A parse failure does not fail loudly — it returns `null`, and **every one of the ten fields, `DbPath`
included, silently reverts to its default.** The file's own header comment says this is load-bearing and
knows it is dangerous: *"LoadModel swallows JsonException and returns null, which would silently reset DbPath
to the default — i.e. point every upgrading user at an EMPTY database"* [VERIFIED: `JsonAppConfig.cs:14-17`].
A regression test was written for the *legacy-key* case only (`Legacy_ActiveTeamId_Key_Is_Ignored_And_DbPath_Survives`).
**No guard exists for the malformed-JSON case** — it is the designed fallback.

And a wrong `DbPath` does not fail either. It **creates a new database and boots normally**:

```
SqliteConnectionFactory.cs:44-48   Directory.CreateDirectory(dir);      // creates the missing parent
SqliteConnectionFactory.cs:52-53   DataSource = _config.DbPath, Mode = SqliteOpenMode.ReadWriteCreate
Program.cs:176                     await ...GetRequiredService<IDatabaseInitializer>().InitializeAsync();
DatabaseInitializer.cs:24-34       CreateTables + RunMigrations + EnsureDefaultBacklog + SeedDefaultTasksIfEmpty
```
[VERIFIED: all four]

So the API comes up healthy, fully migrated, seeded, serving — against the wrong file. The **only** signal
that anything happened is a `Console.WriteLine` banner:

```
Program.cs:57-60   Console.WriteLine($"  Database : {appConfig.DbPath}");
```
[VERIFIED — and `Program.cs:52-53` explains it exists precisely because *"a second machine that 'cannot log in'
almost always means the API opened a DIFFERENT database than expected … invisibly"*]

### Why this is fatal for shape (b) specifically

Shape (b) is *"the operator edits `%APPDATA%\TimesheetApp\appsettings.json` by hand and restarts the API."*
That procedure hands a human a JSON file containing **Windows paths**, and JSON's escape rules make Windows
paths a minefield with two distinct failure modes, both silent:

- `"BackupFolderPath": "C:\data\backups"` → `\d` is not a legal JSON escape → **`JsonException` → the whole
  config reverts to defaults → the API opens/creates a different database.** [VERIFIED for the code chain;
  the JSON escape rule is [ASSUMED] standard `System.Text.Json` strict behavior.]
- `"BackupFolderPath": "C:\backups"` → `\b` **is** a legal JSON escape (backspace) → **no exception at all** →
  `BackupFolderPath` becomes `C:<BS>ackups` → backup writes to a garbage path or throws deep inside
  `Directory.CreateDirectory`. Same for `\t` (`C:\temp`), `\f`, `\n`, `\r`. [ASSUMED — same basis.]

Now compose it with the recommendation's own **required decision 0b**. B-ii demands a supervised host
(Windows Service / Task Scheduler / something that survives a reboot). *Every one of those removes the console
that `Program.cs:57-60` prints to.* **The recommendation's two preferred sub-decisions, taken together,
produce a silent production-database repoint with the diagnostic switched off.**

The benign version is an empty database: nobody can log in, it looks like catastrophic data loss, and it is
recoverable in five minutes once someone reads the banner that no longer prints. **The malignant version is
worse and more likely:** if the Windows account hosting the API ever ran the WPF desktop, then
`Documents\TimesheetApp\timesheet.db` **already exists** with an old desktop database. Everyone logs in with
old credentials, a day of production timesheet writes lands in the wrong file, and the real database sits
untouched. That is a **new way to lose production data, created by the recommended procedure**, and it is
triggered by exactly the situation the procedure exists for: someone editing that file because the backup is
not configured.

### And fork (a) is broken by the same root cause

`Save()` serialises **all ten fields from in-memory state** and `File.WriteAllText`s them
[VERIFIED: `JsonAppConfig.cs:140-157`]. If the file failed to parse at load, in-memory state is *all
defaults*. So the **first** call through an admin route — "set the backup folder" — **permanently overwrites
the good config file with defaults**, destroying `DbPath`, both export roots and the retention settings. The
options file guessed at this ("a partial-write bug could clobber `DbPath`"); it is not a bug you might write,
it is the designed behavior of `Save()` composed with the designed behavior of `LoadModel`.

### What this actually means for the decision

**Neither branch of the config fork is safe until a third thing exists that is in neither branch:** a
fail-fast / validate-on-load step — `LoadModel` distinguishing "no file" (legitimate first run) from "file
present but unparseable" (abort, or at minimum log at Error and refuse to start), and the resolved `DbPath`
logged through `ILogger` rather than `Console.WriteLine` so it survives a windowless host.

Call it **gate item 0c**. It is roughly half a day. With it, shape (b) becomes defensible again and the
recommendation's lean survives. Without it, **(b) can repoint the production database by typo and (a) can
repoint it by clicking Save once.**

I would put 0c in the gate ahead of P1, P3 and P2 — it is cheaper than any of them and it is a precondition
for two of them. **But whether the deletion waits on it is a scope call with an owner, not mine.**

---

## N2 — SERIOUS (FATAL if unmitigated). B-ii splits the backup writer from the backup viewer, and P3 can only ever report the viewer's half

This is the specific cost of preferring **B-ii over B-i**, and the recommendation does not price it.

Under **B-i**, one process resolves config once and hands the same singleton to both the scheduled job and the
list route:

```
Program.cs:34-36   IAppConfig appConfig = string.IsNullOrWhiteSpace(configPath) || string.IsNullOrWhiteSpace(dbPath)
                       ? new JsonAppConfig() : new JsonAppConfig(configPath, dbPath);
Program.cs:49      builder.Services.AddSingleton(appConfig);
```
[VERIFIED]

The job and the list **cannot disagree**. That structural guarantee is what makes P3 a check on P1 rather than
a second opinion.

Under **B-ii**, the CLI is a separate OS process that constructs its own `JsonAppConfig` — resolving
`%APPDATA%` under **whatever account Task Scheduler runs it as** [VERIFIED: `JsonAppConfig.cs:172-175`
`DefaultConfigPath()` reads `Environment.SpecialFolder.ApplicationData`]. Nothing in the design ties the two
resolutions together. The realistic outcomes:

- CLI writes to folder X, API's Settings list reads folder Y → **the screen shows "no backups" while backups
  are being taken**, or, worse,
- CLI is silently no-opping (blank `BackupFolderPath` in *its* config), API's Settings list reads a folder
  still holding files from the WPF era → **"Latest backup: 12 March, 41 MB" is displayed as current, and it is
  four months stale.** `ListBackups()` parses the timestamp out of the *filename*
  [VERIFIED: `BackupService.cs:78-86, 147-155`], so old files render as perfectly legitimate rows forever.

**That second one is a worse failure than the one P3 was added to fix.** An empty list at least prompts a
question. A populated list is an affirmative false answer.

The mitigation is cheap and known — pin `--config`/`--db` explicitly on **both** the Task Scheduler command
line and the API, and have P3's route return the *resolved config path and folder it read*, not just the rows.
It is not in the gate as written. **B-i does not need the mitigation at all**, because the disagreement is
structurally impossible.

Honest counterweight, because the options file's argument for B-ii is not wrong: B-i really does inherit the
API's liveness, and B-ii really does survive a reboot. The trade is **liveness you can verify manually**
(B-ii) versus **config coherence you cannot break** (B-i). See N6 before pricing B-ii's liveness advantage —
it is smaller than claimed.

---

## N3 — SERIOUS. There is a fourth live "operator believes it worked", it is in the export path, and P1 proposes to automate it

The audit found two. The revised options file cites a third (`POST /api/ops/backup/run`) — which, per N8
below, has since been fixed. **This one is live right now and appears in no audit row.**

`IExportHubService.ExportNowAsync()` returns a **status string**, never a path, and never null:

```
ExportHubService.cs:44   public Task<string> ExportNowAsync() => RunAsync(backfillOnly: false);
ExportHubService.cs:53   if (roots.Count == 0) return "no export root configured";
ExportHubService.cs:68   status.AppendLine($"failed: {root} — {check.Message}");
ExportHubService.cs:79   status.AppendLine($"failed: {root} — {ex.Message}");
ExportHubService.cs:83   return status.ToString().TrimEnd();
```
[VERIFIED]

The endpoint passes it through verbatim [VERIFIED: `SettingsEndpoints.cs:1041-1047`,
`Results.Ok(new SettingsOpsResult(await export.ExportNowAsync()))`]. And the client renders it as a success
sentence, unconditionally:

```
settings.component.ts:577   this.opsResult.set(`Export written to ${r.value ?? 'the configured folder'}`);
settings.component.ts:578   this.toast.show('Export complete');
```
[VERIFIED: `src/timesheet-web/src/app/pages/settings/settings.component.ts:571-580`]

So an admin whose export root is unwritable, or a web URL, or unconfigured, is currently shown:

> **Export written to failed: \\\\server\\share — Access to the path is denied.**
> *Export complete* ✓

and

> **Export written to no export root configured**
> *Export complete* ✓

Three things follow, and the third is the one that matters for M10:

1. It is live in the shipped web app today, independent of the deletion.
2. It sits **immediately below** `runBackup()`, which was fixed for precisely this defect and carries a
   comment calling it *"the very defect this milestone exists to remove"* [VERIFIED:
   `settings.component.ts:554-561`]. The fix was applied to one of two adjacent functions.
3. **P1 proposes to run this same code path unattended on a schedule.** The recommendation's Tier-1 pairing is
   "auto-backup + export-hub catch-up backfill" in one scheduler. Automating a job whose only existing
   feedback surface reports failures as successes means a scheduled export that has never worked can run for
   months while the one screen that could reveal it says *Export complete*. Under **B-ii** it is worse still,
   because the CLI has no screen at all (N6).

This is a two-line client fix. It is not on any list in M10. **I would put it in the gate** — not because it
blocks the deletion, but because P1 without it is automating a liar. That is a judgment about scope, and it
has an owner.

---

## N4 — SERIOUS. `RestoreAsync` destroys the live database before it validates the backup, and this codebase's own validator sits unused ten lines away

The revised options file correctly establishes the three mechanical constraints on restore (process-local
`ClearPools`, sidecar deletion, `journal_mode=DELETE`). It misses the ordering, which is the one that bites
under pressure.

```
SqliteOnlineBackup.cs:39-46   public static void Copy(string sourceDbPath, string destDbPath)
                              {
                                  DeleteWithSidecars(destDbPath);          // ← destination destroyed FIRST
                                  using var source = Open(sourceDbPath, ...);   // ← source opened SECOND
                                  using var dest   = Open(destDbPath, ...);
                                  source.BackupDatabase(dest);
```
[VERIFIED]

and `RestoreAsync`'s only check on the source is that the path exists:

```
BackupService.cs:99-100   if (string.IsNullOrWhiteSpace(backupPath) || !File.Exists(backupPath))
                              throw new FileNotFoundException(...);
```
[VERIFIED: `src/TimesheetApp.Core/Services/BackupService.cs:97-125` in full]

Meanwhile the same file the `Copy` lives in already contains the right validator, with a doc-comment that
reads like it was written for this exact gap:

```
SqliteOnlineBackup.cs:64-79   public static bool IsIntact(string dbPath)   // PRAGMA integrity_check == "ok"
SqliteOnlineBackup.cs:60-62   "'Exists && Length > 0' is not evidence that a file is a usable database —
                               six arbitrary bytes pass it."
```
[VERIFIED. `RetentionService` calls it before permanently deleting rows; **`RestoreAsync` never calls it.**]

So: point the restore at a truncated, half-synced or zero-byte backup and the sequence is *delete
`timesheet.db`, delete `-wal`, delete `-shm`, then fail.* The live database is gone before the source is ever
read.

**Why this is not merely theoretical here, and why it is worse under B-ii.** The backup folder is a
user-chosen path that the A1 discussion establishes may well be a sync target. A OneDrive **online-only
placeholder** returns `true` from `File.Exists` and materialises only on open — so "the file is there" is
exactly the check that cannot distinguish a real 40 MB backup from a 0 KB stub. B-ii's whole pitch is putting
restore in a CLI **where the input is a file path typed by a human under pressure with the API stopped**. That
is the highest-risk possible framing for a function that deletes before it validates.

**One thing I expected to find and did not, stated because it matters:** the `.pre-restore_{stamp}.bak` safety
copy is **correct**. It goes through `SqliteOnlineBackup.Copy`, not `File.Copy`
[VERIFIED: `BackupService.cs:121-122`], so it is a real online snapshot including WAL content. Recovery from
the failure above is possible. But it requires the operator to know the `.bak` exists, find it next to the
database, and rename it — under pressure, with the app down, from a runbook. **And the gate's rehearsal cannot
surface any of this, because a rehearsal is run with a good backup file.**

The fix is one line — `if (!SqliteOnlineBackup.IsIntact(backupPath)) throw` before line 124 — and it belongs
in whatever restore shape is chosen.

---

## N5 — SERIOUS. The deletion is not "four project-file edits". It removes 190 of 652 tests in `TimesheetApp.Tests` (29%)

All three options describe the mechanical deletion as *the four project-file edits the audit specifies*
(`.sln` entry + GlobalSection rows, the `TimesheetApp.Tests.csproj` ProjectReference, the dead
`InternalsVisibleTo`). That is not what `git rm -r src/TimesheetApp/` costs.

`TimesheetApp.Tests` references the WPF project directly [VERIFIED:
`src/TimesheetApp.Tests/TimesheetApp.Tests.csproj` — `<ProjectReference Include="..\TimesheetApp\TimesheetApp.csproj" />`],
and **18 test files import WPF namespaces**, plus a 19th that loads WPF resources by pack URI:

| Test file | `[Fact]`/`[Theory]` |
|---|---:|
| `ViewModels/SettingsViewModelTests.cs` | 41 |
| `ViewModels/TimesheetViewModelTests.cs` | 20 |
| `ViewModels/TaskListViewModelTests.cs` | 18 |
| `ViewModels/RequestEditorViewModelTests.cs` | 18 |
| `ViewModels/MainViewModelTests.cs` | 16 |
| `ViewModels/RequestsViewModelTests.cs` | 15 |
| `ViewModels/ReportsViewModelTests.cs` | 12 |
| `ViewModels/SmartInputPanelVmTests.cs` | 10 |
| `ViewModels/DailyReportViewModelTests.cs` | 10 |
| `ViewModels/TimesheetRowVmTests.cs` | 7 |
| `ViewModels/TeamFilterViewModelTests.cs` | 6 |
| `Views/HexToBrushConverterTests.cs` | 4 |
| `ViewModels/UsersViewModelTests.cs` | 4 |
| `DependencyInjectionTests.cs` | 4 |
| `ViewModels/CrossTabSyncTests.cs` | 2 |
| `Views/TeamFilterLoadTests.cs` | 1 |
| `Views/TaskListTabRenderTests.cs` | 1 |
| `Views/SettingsMembershipOverlayLoadTests.cs` | 1 |
| **Total** | **190** |

[VERIFIED: `grep -rln "TimesheetApp.ViewModels\|TimesheetApp.Views" src/TimesheetApp.Tests --include=*.cs`
then `grep -c` per file. Repo totals: `TimesheetApp.Tests` **652**, `TimesheetApp.ApiTests` **213**, **865**
C# tests overall.]

Plus `Views/PaletteParityTests.cs`, which the namespace grep misses because it imports only `System.Windows`
but resolves `pack://application:,,,/TimesheetApp;component/Views/Theme/Palette.Light.xaml`
[VERIFIED: `PaletteParityTests.cs:32`] — it breaks the moment the ProjectReference goes.

**Three consequences the options do not price:**

1. The deletion commit is a **19-file test deletion**, not a four-line edit. Whoever executes M10 needs to
   know that before they start, and the "regression gate" story in every option assumes a stable suite.
2. **29% of the `TimesheetApp.Tests` net disappears at the same moment the plan starts adding new backup,
   restore and scheduler machinery.** That is the worst possible ordering: maximum new risk, minimum
   regression cover.
3. The largest single file is `SettingsViewModelTests.cs` at **41 tests** — the coverage of
   `SettingsViewModel`, which is *the only production writer of the three backup settings* and therefore the
   exact capability P11 exists to replace. **The gate replaces a 41-test-covered writer with either a hand-edit
   (b) or a new route (a), and no option budgets tests for the replacement.**

None of this changes which option is right. It changes the size of the thing everyone is calling "just delete
the folder."

---

## N6 — SERIOUS. B-ii's liveness advantage is untracked Windows configuration with the wrong defaults, and a console job cannot report failure through the one channel it has

B-ii is chosen over B-i on one argument: *"A Task Scheduler entry survives reboots."* Two problems.

**(a) The scheduler entry is not in the repo, and its defaults are wrong for this use.** A Task Scheduler task
is machine-local state configured in a GUI. It is not versioned, not reviewed, not visible to anyone reading
the codebase, and not reproducible on a rebuilt host. That is *precisely* the property the options file
correctly condemns for the backup config — *"a config value written by a deleted application, which nobody can
subsequently change or verify from the product."* **B-ii reproduces that property for the schedule itself.**

Further, the defaults work against it [**[ASSUMED]** — Windows platform behavior, not verifiable from this
repo, and it should be checked by whoever owns the host]: *"Run task as soon as possible after a scheduled
start is missed"* is **off** by default, so a task scheduled at 02:00 on a machine that is off at 02:00 simply
does not run and does not retry; *"Start the task only if the computer is on AC power"* is **on** by default;
and *"Run only when the user is logged on"* reproduces exactly the liveness B-ii criticises B-i for. B-ii's
advantage is real **only** if three specific checkboxes are set correctly on a machine nobody has chosen yet,
and there is no artifact anywhere that records whether they were.

**(b) The CLI's only feedback channel is an exit code, and the method it calls cannot produce one.**

```
BackupService.cs:59-69   public async Task<bool> AutoBackupIfDueAsync()
                         {
                             if (!_config.AutoBackupEnabled) return false;                       // disabled
                             if (string.IsNullOrWhiteSpace(_config.BackupFolderPath)) return false; // unconfigured
                             if (ListBackups().Any(b => b.Timestamp.Date == today)) return false;   // already done
                             var path = await BackupNowAsync();
                             return path is not null;                                            // null => false
                         }
```
[VERIFIED. That last `null` has its own three causes — blank folder, blank `DbPath`, **or the database file
not existing at the resolved path** — collapsed at `BackupService.cs:43-44`.]

So **`false` means one of six different things**, one of which ("the database is not where I think it is") is
catastrophic and one of which ("already backed up today") is the healthy case. A CLI that returns `false` from
`Main` as exit code 0 gives Task Scheduler **Last Run Result: 0x0 (The operation completed successfully)** —
the operator's only dashboard affirmatively reporting success for a backup that never happened. That is the
audit's signature failure, recreated inside the recommended shape, on the item the gate exists for.

The gate's inherited H1 remedy — *"log at Error when it returns early"* — was written for B-i, where Error
reaches the API's logging pipeline. **In B-ii it reaches a console window that no one is attached to.** B-ii
needs something the gate does not specify: distinct exit codes per outcome *and* a durable log destination
(Windows Event Log or a file), *and* someone who looks at it.

**Where this leaves the B-i/B-ii choice.** B-ii's advantage shrinks to "survives a reboot *if* three
checkboxes are right on the right machine"; its costs grow by N2 (config split) and this hole (no reporting
channel). B-i's disadvantage — inheriting an unsupervised `dotnet run` — is real, but it is **erased by the
same decision 0b that B-ii also requires**: once the API runs under something that restarts on boot, a hosted
service inherits that liveness for free, shares one config resolution, and logs through a pipeline that
already exists. **B-ii's argument was strongest under the assumption that 0b would go unanswered — but B-ii
requires 0b too.**

I would pick **B-i for the backup trigger, plus a small offline console for restore only** — the restore
genuinely does need to be a separate process (`ClearPools()` is process-local, `SqliteOnlineBackup.cs:83`,
so the API must be stopped), and that CLI does not need a schedule, an exit-code protocol, or a config of its
own beyond the path it is given. That splits the two Tier-1 items along the line where their requirements
actually differ, instead of merging them because one binary is tidier. **This reverses the recommendation's
shape choice, and it is an architecture call with an owner — I am stating my reasoning, not settling it.**

---

## N7 — MINOR (but it is the load-bearing premise of the recommendation's reason #2). "Deletion destroys `Palette.Dark.xaml`" is false

The recommendation justifies deferring P5-P10 on the grounds that the specs are *"recoverable from git
history"*, and justifies the one non-code gate item on the grounds that the palette is *"the only artifact
where deletion destroys information rather than relocating it into git history."*

```
$ git ls-files | grep -i palette
src/TimesheetApp/Views/Theme/Palette.Dark.xaml
src/TimesheetApp/Views/Theme/Palette.Light.xaml
```
[VERIFIED. 77 files are tracked under `src/TimesheetApp/`, and `.gitignore` excludes only `bin/`, `obj/` and
build output — nothing under `Views/`.]

The palette is tracked. `git rm` relocates it into history **exactly like every other file**. The two claims
cannot both be true: either git history preserves specs (then the palette item is a convenience, not a gate
item) or it does not (then deferring P5-P10 loses its stated safety net). The extraction costs half an hour
and I would still do it — but **the reasoning that puts it in the gate is wrong, and it is the same reasoning
that licenses deferring ten other items.** Worth knowing which one is actually load-bearing.

Sharper version of the real loss: what deletion removes is not the hex values, it is **`PaletteParityTests`** —
the automated guard asserting Light and Dark define identical key sets [VERIFIED:
`src/TimesheetApp.Tests/Views/PaletteParityTests.cs:36-56`]. The gate as written replaces a *tested invariant*
with *hex values copied into a document*. If the extraction is worth gate time, the thing to preserve is the
parity check in whatever form the web design system takes — not the numbers.

---

## N8 — MINOR. One of P3's two stated justifications is already stale — the second confirmed staleness in the frozen spec

The options file argues P3's urgency partly on the audit's PARTIAL row: *"paired with the audit's PARTIAL
finding that 'Backup now' reports a backup that did not happen as success [CITED: `M10-COVERAGE-AUDIT.md:92`],
the post-M10 state is: a button that always says it worked, and no way to check."*

**That has been fixed.** [VERIFIED: `settings.component.ts:544-562`]

```ts
if (!r.value) {
  this.opsResult.set(null);
  this.fail(new Error('Backup did not run — no file was written.'));
  return;
}
```

with a comment naming the exact reasoning — *"null/empty means the server ATTEMPTED NOTHING —
BackupService.cs:43 collapses three different preconditions … Name none of them; a false specific cause is
worse than an honest generic one."* The endpoint still returns `200 OK` with a null value
[VERIFIED: `SettingsEndpoints.cs:1051-1057`], but the client is honest about it.

P3 survives on its remaining justification (there is genuinely no way to *see* whether backups exist, and
`ListBackups()`'s only production caller is the WPF view-model). But this is the **second** confirmed stale row
in the document all three options propose to freeze as the spec — after `.cell input.invalid`, which the
options file itself caught. Two confirmed stale rows out of a handful spot-checked is a rate, not an anomaly.
**Any option that freezes the audit as the spec should budget a re-verification pass at the moment the
deferred work starts**, not treat the document as authoritative three months on.

One further note on P3's costing: the options file prices it at *"~0.5 day, pattern-matching, not design"*
because four `/api/ops/*` routes already exist in that shape. Per N3, **the existing pattern is the defect** —
`200 OK` carrying a status string the client renders as success. Copying it is how P3 becomes false
reassurance #5. The 0.5 day is fine; "pattern-matching, not design" is not.

---

## N9 — MINOR. `BackupKeepCount` is one knob serving two independent retention policies, and P11 makes it reachable for the first time

```
BackupService.cs:35-36     BackupNowAsync() => BackupToFolderAsync(_config.BackupFolderPath, _config.BackupKeepCount);
ExportHubService.cs:143    await _backup.BackupToFolderAsync(Path.Combine(root, "db"), _config.BackupKeepCount);
```
[VERIFIED — the second runs **once per export root, per run**, inside `ExportRootAsync`.]

The same integer bounds (i) how many daily user backups survive and (ii) how many whole-DB copies survive in
`{exportRoot}/db`, for each root. Today that coupling is inert because nothing in the web product can change
the value. **P11's entire purpose is to make it changeable** — so the first operator who sets
`BackupKeepCount: 7` to bound disk usage on the backup folder also silently truncates the export-root DB
history on both roots, and vice versa. Neither surface says so.

Not a blocker. It belongs in P11's design note and in whatever UI or runbook shape (a)/(b) produces, because
it is the kind of coupling that is obvious in the code and invisible from the screen.

---

## N10 — MINOR. The Data Protection key ring is not in the backup, despite the comment saying it is

```
Program.cs:38-39   // The Data Protection key ring MUST outlive the process (see AuthSetup). Default it next to
                   // the database so it is picked up by whatever already backs the database up.
Program.cs:43-46   keyRingPath = Path.Combine(dbDir, "keys");
AuthSetup.cs:87-90 Directory.CreateDirectory(keyRingPath); services.AddDataProtection()
                       .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath)).SetApplicationName("TimesheetApp");
```
[VERIFIED]

But the only automatic backup in the product copies **one file**:

```
BackupService.cs:50-55   Directory.CreateDirectory(folder);
                         SqliteOnlineBackup.Copy(dbPath, backupPath);
                         Prune(folder, keep);
```
[VERIFIED — no directory copy, no `keys` handling anywhere in `BackupService`.]

So the stated rationale for the key ring's location is not satisfied by the mechanism the gate is about to
automate. The blast radius is bounded — the key ring protects the auth cookie, so losing it forces everyone to
log in again rather than losing data. Rated minor for that reason. **Recorded because it is a documented
belief that a backup covers something it does not**, sitting inside the exact subsystem M10 is hardening, and
because a restore runbook written from that comment would be wrong about what a full recovery requires.

---

## What I attacked and could not break

Stated so the holes above are not read as a general indictment. The revised options file is substantially
stronger than the version pass 1 attacked, and several things I expected to break held up.

- **The `.pre-restore` safety copy is genuinely safe.** I went looking for a `File.Copy` that would silently
  drop WAL content from the one file an operator reaches for after a bad restore. It is
  `SqliteOnlineBackup.Copy` [VERIFIED: `BackupService.cs:121-122`]. The claim in the options file is correct.
- **The "frozen spec" premise holds.** `.planning/` is committed — 119 tracked files, including
  `M10-COVERAGE-AUDIT.md` and the whole of `m10-audit/` [VERIFIED: `git ls-files .planning | wc -l`], and
  `commit_docs.planning_artifacts` is `true` [VERIFIED: `.planning/config.json`]. The spec survives the
  deletion on every machine, not just this one. (Its *accuracy* over time is N8's problem, not its existence.)
- **P6's port really is client-side and really does need no regen, and the TEAM column has a name source.**
  `BacklogListItemDto` carries `teamId` but no team name [VERIFIED: `Contracts/Dtos.cs:83-85`;
  `api/models/backlog-list-item-dto.ts:4-13`] — I expected this to force a server change. It does not:
  `getTeamsActive(): Observable<TeamDto[]>` already exists [VERIFIED: `worklog.service.ts:875`] and `TeamDto`
  carries `name` [VERIFIED: `api/models/team-dto.ts`]. Filter and column are both reachable from loaded data.
- **P10's open question is answerable, and the answer helps.** The options file left *"whether WPF's
  `SmartInputPanelVm` calls Core's `BuildPlan` today"* untraced. It does — `SmartInputPanelVm.cs:135`
  `var result = _smartInput.BuildPlan(...)` [VERIFIED] — and Core's multi-task path is covered by five tests
  including holiday exclusion and both divide-by-zero guards [VERIFIED: `SmartInputServiceTests.cs:182-246`].
  **So "route P10 through Core" is an endpoint plus a client swap against tested code, not a new Core
  implementation**, which makes the fork cheaper on the Core side than the options file assumed. (The ten
  `SmartInputPanelVmTests` that exercise it end-to-end are among the 190 deleted by N5.)
- **The dispositions themselves.** I re-read the PORT/ACCEPT/OBSOLETE assignments looking for one that is
  miscategorised in a way that changes the gate. I did not find one. A3 and A10 remain correctly flagged
  `[SIGN-OFF]`; A6 remains the most likely to be wrong and is honestly labelled as such.
- **Gate zero.** Nothing I found weakens it. It remains the precondition for all three options and it remains
  unanswerable without opening a copy of production, which I did not do.

---

## Verdict

**The split-gate shape survives a second attack. The two sub-decisions the recommendation leaned on do not.**

The core reasoning is intact and I could not break it: the PORT list genuinely is two kinds of thing; deletion
genuinely does not make the deferred UI work harder (N7's correction *strengthens* this — git preserves
everything, not almost everything); Option A's duration risk and Option C's overlapping no-backup/no-restore
window are both fairly stated. **Nothing I found argues for Option A or Option C instead.**

What I found is that the recommendation's two open-but-leaned sub-decisions are each broken by something in
this codebase:

- **Config fork (b)** — leaned toward — asks a human to hand-edit JSON whose parse failure silently repoints
  the production database to an empty or stale file, with the only warning printed to a console that decision
  0b removes (N1). Fork (a) fails the same way through `Save()`. **Neither branch is safe until a fail-fast
  config-load guard exists**, and that guard is in neither branch.
- **Shape B-ii** — recommended — splits the backup writer from the backup viewer so that P3, the trust
  mechanism, can report an affirmative false answer (N2); and gives the job no failure channel, since
  `AutoBackupIfDueAsync` collapses six outcomes into `false` and Task Scheduler reads only an exit code (N6).
  Its one advantage over B-i evaporates once you notice **B-ii requires decision 0b too** — and 0b is what
  fixes B-i's liveness for free.

**What I would put to the human, and my reasoning — this is not a decision I am making:**

Keep **Option B**. Change three things inside it:

1. **Add gate item 0c: fail-fast config validation** — distinguish "no config file" from "unparseable config
   file", refuse to start on the latter, and log the resolved `DbPath`/`BackupFolderPath` through `ILogger`
   rather than `Console.WriteLine`. ~0.5 day. It is a precondition for *both* branches of the config fork, so
   it is cheaper than resolving the fork. With it, the recommendation's (b)+(c) lean becomes defensible again.
2. **Switch to B-i for the trigger, and keep a console binary for restore only.** The two Tier-1 items have
   genuinely different requirements — the scheduler needs one config resolution and a real log pipeline, the
   restore needs a separate process because `ClearPools()` is process-local. Merging them because one binary
   is tidier costs N2 and N6. Splitting them costs one extra project.
3. **Add the two one-line honesty fixes** — `runExport()`'s false success (N3) and `IsIntact()` before restore
   (N4). Together under an hour, and they close two of the three live "operator believes it worked" instances,
   one of which P1 is about to automate.

And **re-price the deletion commit itself**: it is 19 test files and 190 tests (N5), not four project-file
edits. That does not change the decision; it changes who is surprised on the day.

**Still not mine, and I am not deciding them:** whether the documented `IAppConfig.Set*` rule bends (N1 makes
the fork *safe* either way once 0c exists — it does not make the architectural question go away); who hosts
the API; whether the affordance gaps are acceptable to ship; whether the export-honesty fix (N3) is in scope
for M10 at all; and whether anyone accepts the window between deletion and the provisioning sweep if gate zero
comes back bad.

---

*No code was edited, no build or test was run, and no SQLite file was opened in producing this file.*
