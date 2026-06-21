# Stack Research — WPF Desktop Timesheet Tool (.NET 8)

**Date:** 2026-06-21
**Spec:** `docs/superpowers/specs/2026-06-21-timesheet-tool-design.md`
**Stack:** C# / .NET 8 · WPF (MVVM) · SQLite via Dapper · ClosedXML · custom Markdown builder
**Deployment shape:** single shared `.db` file on OneDrive/Teams, 2–5 users, single-writer / last-write-wins.

Tag legend: `[VERIFIED]` = standard/confident knowledge · `[CITED]` = backed by source URL · `[ASSUMED]` = inference, not certain.

---

## 1. WPF MVVM on .NET 8 — project setup & MVVM approach

### csproj
SDK-style project. Target framework must use the Windows-specific TFM, not bare `net8.0`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
```

- `net8.0-windows` (NOT `net8.0`) is required for WPF — `<UseWPF>true</UseWPF>` only works on the Windows TFM. `[VERIFIED]`
- `<OutputType>WinExe</OutputType>` suppresses the console window for a GUI app. `[VERIFIED]`
- WPF is Windows-only; this matches spec section 9 (no Mac/cross-platform). `[VERIFIED]`

### MVVM: CommunityToolkit.Mvvm vs hand-rolled INPC — **Recommendation: CommunityToolkit.Mvvm**
The spec text says "MVVM — INotifyPropertyChanged", but that describes the *pattern*, not a mandate to hand-roll it. For 6 ViewModels, `CommunityToolkit.Mvvm` is the right call.

- Latest version is **8.4.2**, published by Microsoft / .NET Foundation, targets .NET Standard 2.0 (works on .NET 8). `[CITED]` (https://www.nuget.org/packages/CommunityToolkit.Mvvm)
- It ships Roslyn **source generators** that remove INPC boilerplate: `[ObservableProperty]` on a field generates the full property + change notification; `[RelayCommand]` on a method generates an `ICommand`. Classes using them must be `partial`. `[CITED]` (https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/generators/overview)
- Base class `ObservableObject` provides `SetProperty` / `OnPropertyChanged`. `[VERIFIED]`
- Why prefer it over hand-rolled: each of the 6 VMs (Timesheet grid, Requests, Users, Reports, Settings) has many bound properties; hand-rolling INPC is ~8 lines per property and a frequent source of "forgot to raise PropertyChanged" bugs. The toolkit is compile-time generated (no runtime reflection cost). `[VERIFIED]`
- Hand-rolled INPC is only justified if you want **zero dependencies**. Not worth it here. `[ASSUMED]`

**DI note:** A small app does not need a DI container. `Microsoft.Extensions.DependencyInjection` is optional; manual wiring of Repository → Service → ViewModel in `App.xaml.cs` is acceptable and simpler for 2–5 users. `[ASSUMED]`

---

## 2. Dapper + SQLite provider, connection lifetime, journal mode (the OneDrive problem)

### Provider: **Microsoft.Data.Sqlite** (pin to the 8.x line for a .NET 8 app)
- Use **Microsoft.Data.Sqlite**, not System.Data.SQLite. It is Microsoft's actively-maintained, lightweight ADO.NET provider, designed to back data-access libraries including Dapper. `[CITED]` (https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/compare)
- It bundles the native SQLite binary (via SQLitePCLRaw) — no separate native DLL juggling, unlike System.Data.SQLite. `[VERIFIED]`
- **Version pin:** the latest is **10.0.9** but the 10.x line targets .NET 10. For a **.NET 8** app pin to the latest **8.0.x** servicing release (e.g. `8.0.10`) so the package's own TFM matches your runtime. `[CITED]` (8.0.10 exists: https://www.nuget.org/packages/Microsoft.Data.Sqlite.Core/8.0.10 ; 10.0.9 latest: https://www.nuget.org/packages/microsoft.data.sqlite/)
- Dapper latest is **2.1.79**; works with any ADO.NET `IDbConnection` including `SqliteConnection`. `[CITED]` (https://www.nuget.org/packages/Dapper/)
- System.Data.SQLite (1.0.119) also works on .NET 8 but is only worth it if you need its extra ADO.NET surface; you don't. Microsoft.Data.Sqlite is the cleaner default for new code. `[CITED]` (https://learn.microsoft.com/en-us/answers/questions/2284458/)

### Connection string
```
Data Source=C:\path\to\timesheet.db
```
`Microsoft.Data.SqliteConnectionStringBuilder` keys: `DataSource`, `Mode` (`ReadWriteCreate` default), `Cache` (`Default`/`Shared`/`Private`), `Pooling` (default true), `Foreign Keys`. `[CITED]` (https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/connection-strings)

- **Enable foreign keys per connection** — SQLite enforces FKs only when `PRAGMA foreign_keys = ON` (off by default). The schema has real FKs (Tasks→Requests, TimeLogs→Users/Tasks), so run the pragma right after opening, OR set `Foreign Keys=True` in the connection string. `[VERIFIED]`

### Connection lifetime — **open-short / close-fast (matches spec §4)**
The spec mandates short connections so OneDrive can sync. Concrete pattern:

- Create a fresh `SqliteConnection` per repository operation (or per unit of work), `using` it, let it dispose. Do **not** hold a long-lived static connection. `[VERIFIED]`
- Connection **pooling is on by default** in Microsoft.Data.Sqlite. A pooled connection that is "closed" may keep the OS file handle open in the pool, which can fight OneDrive's sync. For this OneDrive scenario consider **`Pooling=False`** so `Dispose()` truly releases the file handle, letting OneDrive upload. `[CITED]` (pooling behavior: https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/connection-strings) `[ASSUMED]` (that disabling it helps OneDrive specifically — inference, the perf cost is negligible at this scale).
- Wrap multi-statement writes (e.g. DefaultTasks→Tasks sync, smart-input batch) in an explicit transaction so a partial write can't leave inconsistent rows. `[VERIFIED]`

### Journal mode — **use DELETE (rollback journal), NOT WAL, because of OneDrive** ⚠️ KEY FINDING
This is the most important stack decision and it pushes **against** WAL:

- WAL mode creates persistent sidecar files: `<db>-wal` and `<db>-shm`. These MUST stay consistent with the main `.db`. Cloud sync (OneDrive/Dropbox) syncs the three files independently and at different times; if OneDrive uploads/downloads them out of step, or drops the `-wal`/`-shm`, the database can become **"database disk image is malformed"** / corrupt. The SQLite project and multiple field reports explicitly warn against SQLite-in-cloud-synced-folders, and WAL's extra sidecar files make it worse. `[CITED]` (https://sqlite.org/howtocorrupt.html ; OneDrive/Dropbox field reports: https://synopse.info/forum/viewtopic.php?id=5542 ; https://github.com/Infomaniak/desktop-kDrive/issues/1476)
- **DELETE (the default rollback-journal mode)** keeps everything in the single `.db` file between transactions — the `-journal` file is transient and gone when no write is in progress. With short open/close connections, OneDrive almost always sees just the one `.db` file at rest, which is the safest state to sync. `[VERIFIED]` / `[ASSUMED]` (that DELETE is materially safer for OneDrive than WAL — strong inference grounded in the sidecar-file mechanism above).
- Net guidance: **`PRAGMA journal_mode=DELETE;` (or `TRUNCATE`)**, single-writer, short connections, full checkpoint implicit because there's no WAL. Do NOT set WAL.
- **Honest caveat to surface in the plan:** SQLite-over-OneDrive is fundamentally a "tolerated risk" architecture (spec §4 / §9 already accept file-level conflicts). No journal mode makes concurrent multi-writer fully safe. DELETE + single-writer + short connections is the *least bad* option, not a guarantee. If corruption tolerance later proves too low, the real fix is a tiny shared host (LiteDB server / a network share with locking / a hosted SQLite) — out of v1 scope. `[ASSUMED]`

---

## 3. ClosedXML on .NET 8

- Latest version **0.105.0**, targets .NET Standard 2.0 → compatible with .NET 8. `[CITED]` (https://www.nuget.org/packages/closedxml/)
- Basic write API: `[CITED]` (https://docs.closedxml.io/en/latest/)
  ```csharp
  using var wb = new XLWorkbook();
  var ws = wb.Worksheets.Add("Timesheet");   // returns IXLWorksheet
  ws.Cell("A1").Value = "Date";
  ws.Cell(2, 1).Value = someDate;            // (row, col) overload, 1-based
  ws.Cell("C2").Value = 4.0;                 // numeric
  ws.Column(1).AdjustToContents();
  wb.SaveAs(@"C:\out\timesheet.xlsx");
  ```
- For tabular data, `ws.Cell("A1").InsertTable(enumerable)` or `InsertData(enumerable)` writes a whole collection in one call — useful for the per-user/per-request export. `[VERIFIED]`
- **Gotcha — `System.IO.Packaging` / `System.Memory` assembly conflicts on .NET 8:** some users hit `FileNotFoundException`/version mismatch for `System.Memory` or `DocumentFormat.OpenXml` transitive deps. Fix is to let NuGet restore the full transitive graph (don't copy a bare DLL) and, if it surfaces, add an explicit matching `DocumentFormat.OpenXml` package reference. `[CITED]` (https://learn.microsoft.com/en-us/answers/questions/2113609/)
- **Gotcha — memory:** ClosedXML loads the whole workbook in memory. Irrelevant at timesheet scale (a few hundred rows), but note it if exports ever grow large. `[VERIFIED]`
- `.Value` is a `XLCellValue` struct in 0.10x (since ~0.97) — assign typed values directly (string/double/DateTime/bool); avoid the old `SetValue<T>` patterns from pre-0.97 tutorials. `[ASSUMED]` (API has shifted across versions; verify against 0.105 docs during impl).

---

## 4. Schema init + idempotent migration pattern

Single-file SQLite that may or may not already exist. Pattern:

### 4a. Idempotent table creation
- Run every `CREATE TABLE IF NOT EXISTS ...` on startup. Safe whether the file is brand-new or pre-existing. `[VERIFIED]`
- Wrap the whole init in one transaction. `[VERIFIED]`
- `Microsoft.Data.Sqlite` auto-creates the `.db` file on first connect when `Mode=ReadWriteCreate` (default). `[VERIFIED]`

### 4b. Seeding the hidden DEFAULT request + DefaultTasks (spec §3.3)
Order matters and must be idempotent:
1. Ensure the hidden request exists:
   `INSERT INTO Requests(request_code, project, created_at) SELECT 'DEFAULT','DEFAULT',@now WHERE NOT EXISTS (SELECT 1 FROM Requests WHERE request_code='DEFAULT');` `[VERIFIED]`
2. Seed `DefaultTasks` rows (Annual Leave, Meeting, Other…) only if the table is empty (so a user who renamed/hid them isn't overwritten on next launch):
   `INSERT INTO DefaultTasks(...) SELECT ... WHERE NOT EXISTS (SELECT 1 FROM DefaultTasks);` `[ASSUMED]` (seed-once-if-empty is the safe interpretation of "seed mẫu"; confirm against UX).
3. Sync each active `DefaultTasks` row into a `Task` under the DEFAULT request (insert-if-missing keyed by task_name + the DEFAULT request id). This is the §3.3 unification, and must also run as the ongoing sync when Settings edits DefaultTasks. `[VERIFIED]`

### 4c. Versioned migrations for a pre-existing file
- Use SQLite's built-in **`PRAGMA user_version`** as a lightweight migration counter — no extra table, no migration library needed for an app this size. `[VERIFIED]`
  - Read `PRAGMA user_version;` at startup. If `< target`, run the pending step scripts in order inside a transaction, then `PRAGMA user_version = <target>;`. `[VERIFIED]`
- `CREATE TABLE IF NOT EXISTS` handles additive *tables*; for additive *columns* on an existing table (e.g. the brainstorm-added `Users.windows_username`), `ALTER TABLE ... ADD COLUMN` is needed — SQLite has no `ADD COLUMN IF NOT EXISTS`, so guard it by checking `PRAGMA table_info(Users)` or by gating on `user_version`. `[VERIFIED]`
- Keep migrations **forward-only and additive** (the OneDrive shared file means an old client could open a newer DB — additive columns with defaults keep old clients working / backward-compatible). `[ASSUMED]` (good practice given the shared-file multi-version reality).

---

## 5. Recommended NuGet package list (rough current versions, June 2026)

| Package | Suggested version | Purpose | Confidence |
|---|---|---|---|
| `CommunityToolkit.Mvvm` | **8.4.2** | MVVM source generators (`[ObservableProperty]`, `[RelayCommand]`) | `[CITED]` nuget.org |
| `Dapper` | **2.1.79** | micro-ORM over ADO.NET | `[CITED]` nuget.org |
| `Microsoft.Data.Sqlite` | **8.0.x** (latest 8.0 servicing, e.g. 8.0.10) for a .NET 8 app | SQLite ADO.NET provider (native bundled) | `[CITED]` nuget.org (10.0.9 is latest overall but targets .NET 10) |
| `ClosedXML` | **0.105.0** | Excel (.xlsx) export | `[CITED]` nuget.org |
| `Microsoft.Extensions.DependencyInjection` | 8.0.x | OPTIONAL — only if you want a DI container | `[ASSUMED]` optional |

- Markdown export: **no package** — custom `StringBuilder` per spec §6.2. `[VERIFIED]`
- Pin all Microsoft.Extensions.* (if used) to the **8.0.x** band to match `net8.0-windows`. `[VERIFIED]`
- Avoid pulling 10.x of Microsoft.* packages into a .NET 8 app — version-skew warnings / runtime mismatch. `[ASSUMED]`

---

## Key risks to carry into the plan

1. **OneDrive + SQLite is the dominant architectural risk.** Use journal_mode=DELETE (not WAL), short connections, optionally `Pooling=False`, single-writer. Document it as accepted risk; it is not corruption-proof. `[CITED]`/`[ASSUMED]`
2. **FK enforcement is off by default** — enable per connection or the schema's FKs silently don't protect anything. `[VERIFIED]`
3. **Version-pin Microsoft.* to 8.0.x** for a net8.0 app despite 10.x being "latest." `[CITED]`
4. **Migrations forward-only/additive** via `PRAGMA user_version` because old clients may open a newer shared file. `[VERIFIED]`/`[ASSUMED]`

## Sources
- https://www.nuget.org/packages/CommunityToolkit.Mvvm
- https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/generators/overview
- https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/compare
- https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/connection-strings
- https://www.nuget.org/packages/microsoft.data.sqlite/
- https://www.nuget.org/packages/Microsoft.Data.Sqlite.Core/8.0.10
- https://www.nuget.org/packages/Dapper/
- https://www.nuget.org/packages/closedxml/
- https://docs.closedxml.io/en/latest/
- https://learn.microsoft.com/en-us/answers/questions/2113609/
- https://sqlite.org/howtocorrupt.html
- https://synopse.info/forum/viewtopic.php?id=5542
- https://github.com/Infomaniak/desktop-kDrive/issues/1476
- https://learn.microsoft.com/en-us/answers/questions/2284458/
