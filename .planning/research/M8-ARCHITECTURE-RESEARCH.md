# M8 Architecture Research — Core Extraction Mechanics

**Date:** 2026-07-12
**Agent:** Architecture Research (STEP 4, Mode B)
**Spec:** `docs/superpowers/specs/2026-07-12-m8-backend-foundation-design.md` (esp. §5)
**Inventory:** `.planning/M8-FEATURE-INVENTORY.md`
**Scope:** The *mechanics* of splitting `TimesheetApp` (net8.0-windows, WPF) into `TimesheetApp.Core` (net8.0) + WPF, keeping 548 tests green.

> **Path note:** written as `M8-ARCHITECTURE-RESEARCH.md`, not the unprefixed `ARCHITECTURE-RESEARCH.md` requested — that path already holds the **2026-06-21 P1 WPF-layering research** (git-tracked, still live). This directory prefixes by phase (`P8-`, `P10-`, `P12-`). Rename if you disagree.

Claim tags: `[VERIFIED]` = grepped/read/executed against real code. `[CITED]` = external source. `[ASSUMED]` = inference.

---

## 0. Baseline (measured, not assumed)

`[VERIFIED]` `dotnet test src/TimesheetApp.sln` → **`Failed: 0, Passed: 548, Skipped: 0, Total: 548, Duration: 5 s`**. The gate is real and fast — 5 seconds means you can afford to run it after **every** step below.

`[VERIFIED]` Where 548 comes from: **502 `[Fact]` + 46 `[InlineData]` rows (across 9 `[Theory]`) = 548.** Exact.

`[VERIFIED]` Toolchain: .NET SDK 8.0.205 (also 8.0.201). No `Directory.Build.props`, no `Directory.Packages.props`, no `global.json`, no `.editorconfig`, **no CI workflow**. The split is 2 csproj edits + 1 new csproj + 1 sln edit. Nothing else to update.

`[VERIFIED]` Neither csproj contains a single `<Compile Include>` item — both rely on pure SDK globbing (`**/*.cs`). **Moving a folder between project directories is sufficient to move it between assemblies.** No csproj item edits needed for the file moves.

---

## 1. `InternalsVisibleTo` — where it must be declared

`[VERIFIED]` Today: `TimesheetApp.csproj:13` → `<InternalsVisibleTo Include="TimesheetApp.Tests" />`.

`[VERIFIED]` **Exactly 6 `internal` members exist in the Core-bound dirs** (Services/Data/Models/Config). I grepped every one and traced its consumers:

| # | Member | File | Consumed by | After split |
|---|---|---|---|---|
| 1 | `internal static class DateHelpers` | `Services/DateHelpers.cs:3` | Services (ExportHub, PruneArchiver, StandupArchive, TimeLogService) **+ `ViewModels/ReportsViewModel.cs:56` + `ViewModels/TimesheetViewModel.cs:83,99`** | ⚠️ **CROSS-ASSEMBLY BREAK** |
| 2 | `internal static class FormatHelpers` | `Services/FormatHelpers.cs:5` | Services only (ExportHub, Export, PruneArchiver, TaskListArchive) | ✅ safe, stays internal |
| 3 | `internal static string EscapePipe` | `Services/ExportService.cs:135` | its own class only | ✅ safe |
| 4 | `internal async Task OnTeamsChangedAsync()` | `Services/CurrentTeamService.cs:85` | self + **`Tests/Services/CurrentTeamServiceTests.cs:146,169`** | needs IVT → Tests |
| 5 | `internal enum PathKind` | `Services/SharePointDestinationValidator.cs:13` | self + **`Tests/Services/SharePointDestinationValidatorTests.cs:27,36,43`** | needs IVT → Tests |
| 6 | `internal static PathKind Classify` | `Services/SharePointDestinationValidator.cs:54` | self + **`Tests/…ValidatorTests.cs:28,37,44`** | needs IVT → Tests |

`[VERIFIED]` And on the WPF side, internals the tests reach into: `App.ConfigureServices` (`App.xaml.cs:130` ← `DependencyInjectionTests.cs:27`), `TaskListViewModel.BuildGantt`, `TimesheetViewModel.LastAutoSave` / `RaiseSmartInputAppliedForTest`, `MainViewModel`'s internal ctor, and 8 `internal` members on `DailyReportViewModel`.

### Conclusion

**Both** projects need `InternalsVisibleTo("TimesheetApp.Tests")` — this is not a "move the attribute" job, it is a "declare it in both" job.

```xml
<!-- TimesheetApp.Core.csproj -->
<ItemGroup>
  <InternalsVisibleTo Include="TimesheetApp.Tests" />  <!-- items 4,5,6 -->
  <InternalsVisibleTo Include="TimesheetApp" />        <!-- item 1: DateHelpers -->
</ItemGroup>

<!-- TimesheetApp.csproj — KEEP the existing line, do not move it -->
<ItemGroup>
  <InternalsVisibleTo Include="TimesheetApp.Tests" />
</ItemGroup>
```

`[VERIFIED]` `InternalsVisibleTo` matches on **assembly name**, not namespace. Assembly names here are `TimesheetApp`, `TimesheetApp.Core`, `TimesheetApp.Tests` — so the strings above are correct.

**Item 1 (`DateHelpers`) is the only genuine landmine.** Two fixes:
- **(a) `<InternalsVisibleTo Include="TimesheetApp" />` on Core** — zero `.cs` edits, keeps the file moves purely mechanical. Slight smell (Core trusting its consumer); deleted anyway in M8.10 when WPF dies.
- **(b) Make `DateHelpers` `public`** — a one-word change to a 6-line file whose entire body is `MondayOf(DateOnly)`. Nothing about a date utility is secret.

**Recommendation:** take **(a) during the move** so Steps 2–5 stay pure `git mv`, then promote to (b) in the hygiene pass. Both are one line.

---

## 2. Namespace — confirmed, and it is stronger than the spec claims

`[VERIFIED]` All **84** Core-bound `.cs` files (50 Services + 29 Data + 3 Models + 2 Config) declare an **explicit file-scoped namespace**. I counted: `84 files / 84 with an explicit namespace`. Exactly 5 distinct namespaces:

```
TimesheetApp.Config
TimesheetApp.Data
TimesheetApp.Data.Repositories
TimesheetApp.Models
TimesheetApp.Services
```

**The "no `using` changes anywhere" claim is TRUE — but not because of `RootNamespace`.** `RootNamespace` only supplies the *default* namespace for **newly added** files (and XAML codegen). It does **not** rewrite an existing `namespace X;` declaration. The real reason nothing breaks is simpler and more robust:

> **C# `using` directives resolve namespaces, not assemblies.** Moving `TimeLogService.cs` from one project to another does not change its declared namespace. `TimesheetApp.Services.TimeLogService` stays `TimesheetApp.Services.TimeLogService` — it just ships in a different DLL. Every `using TimesheetApp.Services;` keeps resolving.

`[VERIFIED]` Consequence worth exploiting: **a namespace can legally span two assemblies.** So `ICurrentTeamService` (Core) and `CurrentTeamService` (WPF) can *both* live in `TimesheetApp.Services`, and one `using TimesheetApp.Services;` sees both. This is what makes §4 Option B free. (No CS0433 risk — that requires the *same type name* in both assemblies, which cannot happen since each file lives in exactly one project.)

Still set `RootNamespace` so new files default correctly:

```xml
<!-- TimesheetApp.Core.csproj -->
<TargetFramework>net8.0</TargetFramework>   <!-- NOT -windows -->
<Nullable>enable</Nullable>
<ImplicitUsings>enable</ImplicitUsings>
<RootNamespace>TimesheetApp</RootNamespace>
<!-- AssemblyName defaults to TimesheetApp.Core — correct, leave it -->
```

`[VERIFIED]` No `ImplicitUsings` drift: `UseWPF=true` adds **no** extra implicit usings over the base `Microsoft.NET.Sdk` set (System, System.Collections.Generic, System.IO, System.Linq, System.Net.Http, System.Threading, System.Threading.Tasks). Core gets the identical set.

---

## 3. Package split — measured by grepping `using`, not guessed

`[VERIFIED]` I grepped every package namespace against both sides:

| Package | Core (Services/Data/Models/Config) | WPF (ViewModels/Views/App) |
|---|---|---|
| **Dapper** 2.1.79 | ✅ **20 files** (all repos + DatabaseInitializer + TimeLogService, RetentionService, TeamBootstrapService) | ❌ **zero** |
| **Microsoft.Data.Sqlite** 8.0.10 | ✅ **1 file** — `Data/SqliteConnectionFactory.cs` | ❌ **zero** |
| **ClosedXML** 0.105.0 | ✅ **1 file** — `Services/ExportService.cs` (`using ClosedXML.Excel;`) | ❌ **zero** |
| **CommunityToolkit.Mvvm** 8.4.2 | ⚠️ **1 file** — `Services/CurrentTeamService.cs` (`using CommunityToolkit.Mvvm.Messaging;`) | ✅ 20 VMs + 2 code-behinds + App |
| **Microsoft.Extensions.DependencyInjection** 8.0.1 | ❌ **zero today** | ✅ `App.xaml.cs` only |

`[VERIFIED]` **All four packages ship net8.0-compatible assets** — checked the restored NuGet cache directly:
- `communitytoolkit.mvvm/8.4.2/lib/` → `net8.0`, `net8.0-windows10.0.17763`, `netstandard2.0`, `netstandard2.1` → a plain `net8.0` Core resolves the **`net8.0`** asset. **No Windows dependency.**
- `closedxml/0.105.0/lib/` → `netstandard2.0`, `netstandard2.1`
- `dapper/2.1.79/lib/` → `net8.0`, `net461`, `netstandard2.0`, `net10.0`
- `microsoft.data.sqlite/8.0.10/lib/` → `net6.0`, `net8.0`, `netstandard2.0`

**Nothing blocks a `net8.0` Core.**

### Answering the specific question: is `CommunityToolkit.Mvvm` used in `Services/`?

`[VERIFIED]` **Yes — but in exactly ONE file, and not the one you expected.**

- **`Services/DataChangedMessage.cs` has ZERO CommunityToolkit imports.** It is `enum DataKind` (11 values) + `public sealed record DataChangedMessage(DataKind Kind)` — a **dependency-free POCO**. The only mention of `WeakReferenceMessenger` in it is a *comment*. It can move to Core with no package at all.
- **`Services/CurrentTeamService.cs:1`** is the sole `using CommunityToolkit.Mvvm.Messaging;` in all 84 Core-bound files. It injects `IMessenger`, `Register<>`s a listener, and `Send`s on team change.
- `[VERIFIED]` **Zero** `ObservableObject` / `RelayCommand` / `[ObservableProperty]` / `INotifyPropertyChanged` / `ObservableCollection` anywhere in Services/Data/Models/Config.

⇒ Core's entire CommunityToolkit surface is **one interface (`IMessenger`) in one class**. §4 shows how to get it to zero.

### Recommended csproj package sets

```xml
<!-- TimesheetApp.Core.csproj -->
<PackageReference Include="Dapper" Version="2.1.79" />
<PackageReference Include="Microsoft.Data.Sqlite" Version="8.0.10" />
<PackageReference Include="ClosedXML" Version="0.105.0" />
<!-- NO CommunityToolkit.Mvvm — see §4 Option B -->

<!-- TimesheetApp.csproj (WPF) — after the hygiene pass -->
<PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.2" />
<PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="8.0.1" />
<!-- Dapper / Microsoft.Data.Sqlite / ClosedXML REMOVED: WPF has zero `using` for all three -->
```

`[VERIFIED]` `TimesheetApp.Tests.csproj` must **keep** its direct `Microsoft.Data.Sqlite` reference — `TestDb.cs:114` and `SqliteConnectionFactoryTests.cs:101` call `SqliteConnection.ClearAllPools()`.

---

## 4. `DataChangedMessage` / `WeakReferenceMessenger` — three options, one recommendation

### The constraint that decides it

`[VERIFIED]` `ICurrentTeamService` is injected by **two Core services**: `Services/TimeLogService.cs:20,29` and `Services/StandupService.cs:12,20`. So **the interface must be in Core.** Not negotiable.

`[VERIFIED]` But they depend on the **interface**, never the concrete class. The only thing that touches `IMessenger` is the **impl**, `CurrentTeamService`.

`[VERIFIED]` `DataChangedMessage`/`DataKind` is dependency-free (see §3), and the 20 `IMessenger` consumers are **all ViewModels** — all staying in WPF.

### Option A — spec-literal: move `CurrentTeamService` + `DataChangedMessage` to Core; Core takes `CommunityToolkit.Mvvm`
- ✅ Zero code change, zero test change.
- ❌ Core drags an MVVM package into the API for one interface.
- ❌ **And it buys the API nothing.** `CurrentTeamService` is a **singleton** that persists the active team via `_config.SetActiveTeamId()` into `%APPDATA%`. On a server that is one process-global "active team" **shared across all users** — a cross-user leak that violates the codebase's own R6 no-leak rule. **The API must replace `ICurrentTeamService` regardless.** So the CommunityToolkit dependency in Core is pure WPF baggage.

### Option B — **RECOMMENDED**: split interface from impl
- `Services/DataChangedMessage.cs` → **Core** (dependency-free; and spec §7.2 says the SignalR hub broadcasts these same 11 `DataKind` values 1:1 — so the vocabulary *belongs* in Core).
- `Services/ICurrentTeamService.cs` → **Core** (required by TimeLogService + StandupService).
- `Services/CurrentTeamService.cs` (the impl) → **stays in WPF**. It is a desktop-session concept: singleton, `%APPDATA%`-backed, messenger-driven.
- ✅ **Core needs no CommunityToolkit at all.**
- ✅ Zero `.cs` edits. `App.ConfigureServices`'s `sc.AddSingleton<ICurrentTeamService, CurrentTeamService>()` still compiles — both types are in namespace `TimesheetApp.Services` (§2), just different assemblies.
- ✅ `CurrentTeamServiceTests.cs` unchanged (Tests references WPF anyway — see §7).
- ✅ The API writes a **scoped** `HttpCurrentTeamService : ICurrentTeamService` reading the team from the request principal — **which it has to write anyway**, per the bullet above.
- Cost: interface in Core, impl in WPF. Honest and correct — it *is* a desktop concept.

### Option C — spec's floated `IDataChangeNotifier` abstraction — **recommend AGAINST for M8.1**
- `CurrentTeamService` both **sends** and **subscribes**, so the interface needs `Send` + `Subscribe` — a full bidirectional bus, not a one-liner.
- Changes the `CurrentTeamService` ctor → touches `CurrentTeamServiceTests` (a Core test).
- The 20 WPF ViewModels would still use `IMessenger` directly → **two buses** unless you migrate all 20 (large, risky, zero user value in M8.1).
- `[VERIFIED]` **No Core service needs to send messages.** The only sender in Core-bound code is `CurrentTeamService`, which Option B relocates to WPF. Per spec §7.2 the API broadcasts SignalR **from its controllers** after each mutation — it never needs a Core-level notifier.
- ⇒ This is speculative generality. CLAUDE.md § "Simplicity First": *"No abstractions, flexibility, or configurability that were not requested."*

**Verdict: Option B.** Fall back to Option A only if you want the literal minimum diff and accept an MVVM package in Core.

---

## 5. `IAppConfig` — the spec's "no service changes" claim is wrong, and there's a landmine

`[VERIFIED]` Full member list (11 getters + 11 setters):

| Member | Read by **Core services**? | Written by **Core services**? | Desktop-only? |
|---|---|---|---|
| `DbPath` / `SetDbPath` | ✅ **9 services** (SqliteConnectionFactory, BackupService, DbBackupHelper, DefaultTaskSync, PruneArchiver, RetentionService, StandupArchive, TaskListArchive, TimeLogService) | ❌ only `SettingsViewModel:190` | path — API supplies a server path |
| `ArchivePath` / `Set…` | ✅ StandupArchive, TaskListArchive | ❌ only `SettingsViewModel:198` | folder |
| `BackupFolderPath` / `Set…` | ✅ BackupService | ❌ only `SettingsViewModel:264` | folder |
| `AutoBackupEnabled` / `Set…` | ✅ BackupService:55 | ❌ only `SettingsViewModel:265` | — |
| `BackupKeepCount` / `Set…` | ✅ BackupService, ExportHubService:145 | ❌ only `SettingsViewModel:266` | — |
| **`ActiveTeamId` / `SetActiveTeamId`** | ✅ CurrentTeamService:53 | 🔴 **YES — `CurrentTeamService:72,94` AND `TeamBootstrapService:66,76`** | 🔴 **per-USER state** |
| `ExportRoot1Path` / `ExportRoot2Path` + setters | ✅ ExportHubService:50, PruneArchiver:55 | ❌ only `SettingsViewModel:206,213` | UNC/SharePoint |
| `RetentionEnabled` / `Set…` | ❌ (only `App.xaml.cs:91`) | ❌ only `SettingsViewModel:315` | — |
| `RetentionMonths` / `Set…` | ✅ RetentionService:60 | ❌ only `SettingsViewModel:316` | — |
| `IsDarkMode` / `SetIsDarkMode` | ❌ **zero Core use** (only `App.xaml.cs:48`) | ❌ only `SettingsViewModel:151` | 🔴 **per-USER pref** |

### 🔴 Finding 1 — the spec's "No service among the 49 changes" is **false**

Spec §5.1 says the API can supply an `IAppConfig` backed by ASP.NET config and nothing else changes. But **`SetActiveTeamId` is called from two Core services**, not just ViewModels:

- `TeamBootstrapService:66,76` — and per inventory §0.2 this runs at **API startup** (as a hosted service / migration). If the API's `IAppConfig` throws `NotSupportedException` on setters, **the API fails to boot.**
- `CurrentTeamService:72,94` — writes one user's active team into a **process-global singleton**. On a server that is a cross-user leak.

`[VERIFIED]` **Every other setter is called only from `SettingsViewModel`** — those 9 can safely be `NotSupportedException` / no-op on the API impl.

⇒ `ActiveTeamId` is the single member that is both **load-bearing in Core** and **semantically per-user**. §4 Option B contains half of it (API replaces `ICurrentTeamService`); `TeamBootstrapService` is the other half and needs an explicit decision in M8.2 (simplest: the API's `IAppConfig` impl makes `SetActiveTeamId` a **no-op** — bootstrap doesn't need to persist it server-side).

### 🔴 Finding 2 — the landmine: **4 hand-rolled `IAppConfig` fakes in the test project**

`[VERIFIED]` Four test files implement `IAppConfig` **by hand**, member for member:

| File | Class |
|---|---|
| `Tests/Services/BackupServiceTests.cs:24` | `private sealed class FakeConfig : IAppConfig` |
| `Tests/Services/ExportHubServiceTests.cs:27` | `private sealed class FakeConfig : IAppConfig` |
| `Tests/Services/PruneArchiverTests.cs:49` | `private sealed class StubConfig : IAppConfig` |
| `Tests/Services/RetentionServiceTests.cs:45` | `private sealed class FakeConfig : IAppConfig` |

(8 further files use `Mock<IAppConfig>` — Moq auto-implements, so those are immune.)

> **⚠️ ADDING any member to `IAppConfig` breaks all four → the test project stops compiling → all 548 tests fail.**
> (Removing a member is safe — the fakes simply carry an extra property.)

**Rule for M8.1: do NOT touch the `IAppConfig` interface. At all.** In particular this **rules out** hanging `SqliteOptions` off `IAppConfig` (see §6). Purify it in M8.10 when WPF dies.

### 🟡 Finding 3 — `JsonAppConfig` should go to **Core**, contra spec §5.1

Spec §5.1: *"Core keeps **only the interface**. WPF keeps the file-based implementation."*

`[VERIFIED]` `JsonAppConfig` imports only `System.IO` + `System.Text.Json`. **Zero WPF.** It is 100% portable to net8.0.

`[VERIFIED]` It is `new`-ed in **9 test files across 25 call sites** — `JsonAppConfigTests` (15), `TestDb.cs:36`, `SqliteConnectionFactoryTests` (2), `DatabaseInitializerTests`, `SchemaV7/V8/V9UpgradeTests`, `StandupArchiveServiceTests`, `TaskListArchiveServiceTests`, `DependencyInjectionTests:32`. **Eight of those nine are otherwise pure-Core test files.**

If `JsonAppConfig` stays in WPF, those Core tests transitively depend on the WPF assembly. They'd still compile today (Tests references both — §7), but it poisons any future `Core.Tests` split and it is simply untrue that they need WPF.

**Recommendation: move both `Config/` files to Core.** The API just doesn't register `JsonAppConfig`. Cost is one unused class in Core; benefit is 25 call sites untouched and a genuinely WPF-free Core test surface. Flag as a deliberate deviation from spec §5.1.

---

## 6. `SqliteConnectionFactory` options — design + empirical proof it's free

### Constraints (all `[VERIFIED]`)

1. `SqliteConnectionFactory` has a **single-arg ctor `(IAppConfig config)`**, `new`-ed directly at 3 test call sites: `TestDb.cs:37`, `SqliteConnectionFactoryTests.cs:21` and `:91`. **A required second param breaks them.**
2. `SqliteConnectionFactoryTests` asserts `journal_mode == "delete"`, no `-wal`/`-shm` sidecar, `Pooling` off (handle released), FK on. **The default profile MUST stay Desktop**, or these fail.
3. **`IAppConfig` cannot gain a member** (§5 Finding 2). So options must NOT hang off `IAppConfig`.
4. `IOptions<SqliteOptions>` as a ctor param would **also** break the 3 direct-`new` sites (they'd need `Options.Create(...)`) and drags `Microsoft.Extensions.Options` into Core. **Reject `IOptions<T>` at the Core boundary.**

### Design — optional ctor param + static presets

```csharp
// TimesheetApp.Core/Data/SqliteOptions.cs
namespace TimesheetApp.Data;

public sealed record SqliteOptions
{
    public bool    Pooling       { get; init; }                 // false
    public string  JournalMode   { get; init; } = "DELETE";
    public bool    ForeignKeys   { get; init; } = true;
    public int?    BusyTimeoutMs { get; init; }
    public string? Synchronous   { get; init; }

    /// Today's WPF/OneDrive behaviour. The DEFAULT — existing callers are unchanged.
    public static readonly SqliteOptions Desktop = new();

    /// Server profile (M8.2): spec §5.2.
    public static readonly SqliteOptions Server = new()
    { Pooling = true, JournalMode = "WAL", BusyTimeoutMs = 5000, Synchronous = "NORMAL" };
}

public sealed class SqliteConnectionFactory : IConnectionFactory
{
    private readonly IAppConfig _config;
    private readonly SqliteOptions _options;

    public SqliteConnectionFactory(IAppConfig config, SqliteOptions? options = null)
    {
        _config  = config;
        _options = options ?? SqliteOptions.Desktop;   // ← preserves today's behaviour exactly
    }
    …
}
```

### `[VERIFIED]` — I executed this against MS.DI 8.0.1, not assumed it

The load-bearing question was: *does `Microsoft.Extensions.DependencyInjection` honor an optional ctor param whose type is **not registered**?* If not, `sc.AddSingleton<IConnectionFactory, SqliteConnectionFactory>()` would throw and `DependencyInjectionTests` would fail. I built a throwaway project against MS.DI 8.0.1 and ran it:

```
CASE1 (options NOT registered):            OK -> journal=DELETE pooling=False   ← WPF today, unchanged
CASE2 (options registered):                OK -> journal=WAL    pooling=True    ← API host, M8.2
CASE3 (new SqliteConnectionFactory(cfg)):        journal=DELETE pooling=False   ← TestDb + factory tests, unchanged
```

**Confirmed on all three paths.** Therefore:

- **WPF** `App.ConfigureServices`: `sc.AddSingleton<IConnectionFactory, SqliteConnectionFactory>();` → **unchanged**, resolves to Desktop.
- **API**: `sc.AddSingleton(SqliteOptions.Server); sc.AddSingleton<IConnectionFactory, SqliteConnectionFactory>();` → WAL + pooling. (Bind from `IConfiguration` with `GetSection("Sqlite").Get<SqliteOptions>()` — no `IOptions` needed at the Core boundary.)
- **Tests**: `new SqliteConnectionFactory(cfg)` → **unchanged**, Desktop, all 6 factory assertions still pass.

⇒ **Zero changes to any of the 548 tests.** Single ctor (avoids the multi-ctor MS.DI ambiguity footgun). No new package in Core.

---

## 7. Safe extraction order — the main deliverable

**Governing rule: Steps 2–5 are pure `git mv` + csproj edits. Not one line of C# changes.** If a step fails to build, the cause is unambiguous. Run `dotnet test` after **every** step — it takes 5 seconds.

**Do NOT mix the spec §11 bug-fixes into the extraction.** A pure move that goes red is trivially diagnosable; a move *plus* three behaviour changes is not. Bug-fixes land after the gate is green and committed (Step 8).

---

### Step 0 — Baseline
`dotnet test src/TimesheetApp.sln` → **548/548**. `[VERIFIED]` — already done, 5s.

### Step 1 — Create the empty Core project and wire all three references. *(nothing moves yet)*
- `dotnet new classlib -n TimesheetApp.Core -o src/TimesheetApp.Core -f net8.0`; delete `Class1.cs`.
- Core csproj: `net8.0` (**no** `-windows`, **no** `UseWPF`), `Nullable=enable`, `ImplicitUsings=enable`, `RootNamespace=TimesheetApp`.
- Core packages: `Dapper` 2.1.79, `Microsoft.Data.Sqlite` 8.0.10, `ClosedXML` 0.105.0.
- Core: `<InternalsVisibleTo Include="TimesheetApp.Tests" />` **and** `<InternalsVisibleTo Include="TimesheetApp" />` (§1).
- `TimesheetApp.csproj`: **add** `ProjectReference` → Core. **Keep** its `InternalsVisibleTo` and — for now — **all 5 packages**.
- `TimesheetApp.Tests.csproj`: **add** `ProjectReference` → Core. **Keep** the existing reference to WPF (§7 note below). Stays `net8.0-windows`.
- Add Core to `TimesheetApp.sln`.
- ✅ **Gate: build + 548 green.** (Trivially — no code moved.)

### Step 2 — `git mv Models/` → Core *(3 files)*
Leaf of the dependency graph: zero packages, zero internals, zero WPF.
- ✅ **Gate: 548 green.**

### Step 3 — `git mv Config/` → Core *(2 files: `IAppConfig.cs` + `JsonAppConfig.cs`)*
Move **both** (§5 Finding 3 — deviation from spec §5.1). `System.IO` + `System.Text.Json` only.
- ✅ **Gate: 548 green.** ← this one proves `JsonAppConfigTests` (15 call sites) survived.

### Step 4 — `git mv Data/` → Core *(29 files)*
Depends only on Config + Models (already moved). Pulls in Dapper + Microsoft.Data.Sqlite. No internals, no WPF.
- ✅ **Gate: 548 green.** ← proves `TestDb`, all repo tests, all 3 schema-upgrade tests, `SqliteConnectionFactoryTests` now run against Core.

### Step 5 — `git mv Services/` → Core, **minus 3 files** *(47 of 50)*
**Leave in `src/TimesheetApp/Services/`:**
- `ThemeService.cs` + `IThemeService.cs` — the only real `using System.Windows` (§8).
- `CurrentTeamService.cs` — the only `IMessenger` consumer (§4 Option B).

**Move** `ICurrentTeamService.cs` and `DataChangedMessage.cs` (both dependency-free; `TimeLogService` + `StandupService` need the former).

Pulls in ClosedXML. `DateHelpers` cross-assembly access is already covered by Core's `InternalsVisibleTo("TimesheetApp")` from Step 1.
- ✅ **Gate: 548 green.** **Core is now complete and CommunityToolkit-free.**

### Step 6 — Hygiene pass *(no behaviour change)*
- Drop `Dapper`, `Microsoft.Data.Sqlite`, `ClosedXML` from `TimesheetApp.csproj` — WPF has **zero** `using` for all three `[VERIFIED]`.
- Optionally promote `DateHelpers` to `public` and drop `InternalsVisibleTo("TimesheetApp")` from Core.
- Optionally add `AddTimesheetCore(this IServiceCollection)` to Core (needs only `Microsoft.Extensions.DependencyInjection.**Abstractions**` — `IServiceCollection` + `AddSingleton` live there) and have `App.ConfigureServices` call it, so WPF and the API can't drift. `DependencyInjectionTests` is unaffected — it still calls `App.ConfigureServices(sc)`.
- ✅ **Gate: 548 green. This is the M8.1 acceptance gate (spec §5.3) — plus launch the WPF app once.**

### Step 7 — `SqliteOptions` *(enables M8.2; still zero test change — §6)*
- Add `Core/Data/SqliteOptions.cs`; add the **optional** ctor param. Default = `Desktop` = today.
- **Do not touch `IAppConfig`** (§5 Finding 2).
- ✅ **Gate: 548 green.**

### Step 8 — *(NOT M8.1)* Bug-fixes from spec §11 + the `windows_username` rename (§6.2)
Only after the extraction is green **and committed**.

---

### 🔴 The spec is wrong about the test project — it must reference **BOTH**

Spec §5 says `TimesheetApp.Tests/ → references Core`, and §9 says *"548 existing tests move to Core."* `[VERIFIED]` **Both are false.**

`TimesheetApp.Tests` (61 test files) splits as:

| Bucket | Files | Needs |
|---|---|---|
| Core-only (Config 1, Data 15, Models 1, Services 20) | **37** | Core |
| **WPF-bound** — `ViewModels/` (13), `Views/` (8), `DependencyInjectionTests` (uses `App.ConfigureServices`), `Services/CurrentTeamServiceTests` (under §4 Option B) | **23** | **WPF** |
| `SmokeTests` | 1 | neither |

`[VERIFIED]` `Tests/Views/` contains **live WPF STA render tests** — `WpfStaCollection.cs` exists solely because *"WPF permits exactly one `System.Windows.Application` per AppDomain"*; `TaskListTabRenderTests`, `TeamFilterLoadTests`, `SettingsMembershipOverlayLoadTests`, `ThemeServiceTests`, `PaletteParityTests`, `SelectUserDialogLoadTests`, `HexToBrushConverterTests` all instantiate real WPF types.

⇒ **`TimesheetApp.Tests` must reference Core AND WPF, and must stay `net8.0-windows`.** It cannot become a Core-only test project without splitting it into two projects — a change the spec does not scope and which is **not** required for M8.1. Leave it as one project referencing both.

---

## 8. `IThemeService` — clean cut, confirmed

`[VERIFIED]` `ThemeService.cs:1` is the **only** real `using System.Windows;` in all 84 Core-bound files. The other 5 files that matched a `System.Windows` grep are **comments boasting about being WPF-free** (`IJournalWarningSink.cs:6`, `ITimeLogService.cs:8`, `TimeLogService.cs:9`, `TraceJournalWarningSink.cs:6`, `UiJournalWarningSink.cs:5`). The spec's "exactly one file" claim is **correct**.

`[VERIFIED]` **Who injects `IThemeService`?** Grepped Services/, Data/, Models/, Config/, ViewModels/, App:
- `App.xaml.cs:48` — resolve + `Apply(config.IsDarkMode)`
- `App.xaml.cs:137` — DI registration
- `ViewModels/SettingsViewModel.cs:33,51` — **optional** ctor param, `IThemeService? theme = null` ("null in tests")

**No service, no repository, no Core-bound type injects `IThemeService`.** Both `IThemeService.cs` and `ThemeService.cs` stay in WPF with zero consequences. Its only test, `Tests/Views/ThemeServiceTests.cs`, is already an STA WPF test in the `Views/` bucket.

`[VERIFIED]` Bonus: `UiJournalWarningSink` **is** genuinely portable despite its name — it is just an `event`; `App.xaml.cs:117` does the `Dispatcher.Invoke` marshalling. It moves to Core safely.

---

## 9. Surprises / corrections to the spec

| # | Spec says | Reality `[VERIFIED]` | Impact |
|---|---|---|---|
| 1 | §5/§9: *"Tests → references Core"*, *"548 tests move to Core"* | **23 of 61 test files need WPF** (13 VM + 8 STA render + DI test + CurrentTeamService). Tests must reference **both** and stay `net8.0-windows`. | **Plan-breaking if unnoticed.** |
| 2 | §5.1: *"No service among the 49 changes"* | **`SetActiveTeamId` is called by 2 Core services** (`CurrentTeamService`, `TeamBootstrapService`). A read-only API `IAppConfig` that throws on setters **kills API startup**. | Needs an explicit M8.2 decision. |
| 3 | — | **4 test files hand-implement `IAppConfig`.** Adding *any* member breaks compilation → all 548 fail. | **Hard constraint.** Rules out `SqliteOptions` on `IAppConfig`. |
| 4 | §5: `Services/ 49 files (all except ThemeService + IThemeService)` | Services/ holds **50** files. 50 − 2 = **48**, not 49. (And Option B leaves 47.) | Cosmetic. |
| 5 | §7.2 implies `DataChangedMessage` is CommunityToolkit-coupled | **`DataChangedMessage.cs` has zero package imports.** It's a bare enum + record. | Makes Core CommunityToolkit-free (§4 Option B). |
| 6 | §5.1: *"Core keeps only the interface [of IAppConfig]"* | `JsonAppConfig` is 100% portable and `new`-ed at **25 call sites in 9 test files**, 8 of them otherwise pure-Core. | Recommend moving it to Core anyway. |
| 7 | — | `DateHelpers` is `internal` in Services/ but **used by 2 ViewModels** → the one true cross-assembly break in the whole extraction. | 1-line fix; would otherwise be a surprise CS0122 mid-move. |
| 8 | — | **`ICurrentTeamService` is injected by `TimeLogService` + `StandupService`** — so the interface is genuinely Core, even though its impl is a desktop-session concept. | Decides §4. |

---

## 10. Risk register

| Risk | Likelihood | Mitigation |
|---|---|---|
| `DateHelpers` CS0122 on first Services move | **High** if unhandled | `InternalsVisibleTo("TimesheetApp")` on Core, added in **Step 1** (before the move). |
| Adding a member to `IAppConfig` → 4 fakes break → 548 red | Medium (tempting during M8.2) | Documented hard rule. `SqliteOptions` is a separate type (§6). |
| Stale `obj/`/`bin/` after moving folders → phantom duplicate-type (CS0101) errors | Medium | `dotnet clean` (or delete `obj/` + `bin/`) between steps if anything looks impossible. A `.vs/` folder exists — close VS to avoid file locks. |
| API `IAppConfig` throws on `SetActiveTeamId` → API won't boot | Medium (M8.2) | Make it a **no-op** on the API impl; replace `ICurrentTeamService` with a scoped, request-bound impl. |
| Big-bang move (all 4 folders at once) → 200 errors, no signal | High if attempted | The 7-step ladder. `dotnet test` is **5 seconds** — there is no excuse to skip a gate. |
| Bug-fixes (spec §11) mixed into the extraction | Medium | Step 8, after the gate is green **and committed**. A pure move that goes red is diagnosable; a move + 3 behaviour changes is not. |

---

## 11. Bottom line

The extraction is **mechanically clean** and the spec's core premise holds up.

`[VERIFIED]` **The decisive fact: zero files in Services/Data/Models/Config reference `TimesheetApp.ViewModels`, `TimesheetApp.Views`, `App.`, or `MainWindow`.** There is no circular dependency to unpick — the layering the June-21 P1 research laid down was actually respected. The cut is a `git mv`, not a refactor.

Three things the spec did not know, in priority order:
1. **The test project must reference WPF too** — 23 of 61 files need it (STA render tests, ViewModel tests, `App.ConfigureServices`). It stays `net8.0-windows`.
2. **`IAppConfig` is frozen** — 4 hand-rolled fakes mean any added member fails the build.
3. **`SetActiveTeamId` is called from Core services**, so the API's config impl cannot be a throw-on-write stub.

And one free win: **Core does not need `CommunityToolkit.Mvvm` at all** — keep `CurrentTeamService`'s impl in WPF, move only its interface and the dependency-free `DataChangedMessage`.
