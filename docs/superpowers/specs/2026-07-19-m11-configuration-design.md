# M11 — Configuration: locations become `IConfiguration`, policy stays writable

**Date:** 2026-07-19
**Status:** Draft — decisions locked in `STATE.md` 2026-07-19, extended by the user's go-live requirement of the same day.
**Prior findings:** `.planning/fast-lane-settings-appsettings.json` (F1–F5; **F3 RESOLVED 2026-07-19**).

---

## 1. What the user actually asked for

> *"Go-live sẽ có 2 kịch bản → chạy với database mới hoàn toàn dựa trên path được set trong appsettings. Nếu path đã có sẵn thì load lên sử dụng chính database đó. Còn không thì chạy mới."*

Two scenarios, one mechanism: **`DbPath` comes from `appsettings.json`. If a database already exists at that path, use it. If not, create it.**

The "create it if missing" half **already works** — `DatabaseInitializer` bootstraps a fresh schema on an absent file. **The "comes from `appsettings.json`" half does not work at all today.** That is F1 and F2, and it is this milestone.

## 2. Why the sequencing is not negotiable

M11 makes the three location keys **required, with no fallback chain**. While the WPF app still exists it reads the same values from `%APPDATA%` through `JsonAppConfig` — two hosts, two sources of truth. The `IDatabaseLocation` bridge that would have reconciled them was **deliberately dropped** on the assumption M10 removes the second host first.

**Therefore: M10 (delete WPF) lands before M11.** Doing M11 first means rebuilding a bridge that was dropped on purpose, to serve a host that is being deleted.

## 3. The cut

`IAppConfig` currently carries **ten** members. They split three ways:

| Key | Goes to | Why |
|---|---|---|
| `DbPath` | **`IConfiguration`, required** | Where the data lives. An operator decision, per host, before the app can do anything. |
| `KeyRingPath` | **`IConfiguration`, required** | Data Protection keys. Must outlive the process; losing them logs everyone out. |
| `ConfigPath` | **`IConfiguration`, required** | The seam that decides everything else. See §5. |
| `BackupFolderPath` | writable store | Policy. Changes at runtime, by a person, through a screen. |
| `AutoBackupEnabled` | writable store | Policy. |
| `BackupKeepCount` | writable store | Policy. |
| `ExportRoot1Path` | writable store | Policy. |
| `ExportRoot2Path` | writable store | Policy. |
| `RetentionEnabled` | writable store | Policy — and destructive; a person must own it. |
| `RetentionMonths` | writable store | Policy. |
| `IsDarkMode` | **client** | Never belonged on the server. Per-user, per-browser. |

### 🔴 3.1 `ArchivePath` is unaccounted for — a gap in the locked decision

`STATE.md` says *"the **7** policy keys (backup / retention / export) stay in the writable store"*. Counting the record at `JsonAppConfig.cs:18-28`: ten members, minus `DbPath`, minus `IsDarkMode`, leaves **eight** — the seven named plus **`ArchivePath`**.

`ArchivePath` is a *location*, not a policy: it is where the legacy flat standup/tasklist archives are written. By the rule this milestone uses (locations are operator decisions, policy is a person's decision) it belongs with `DbPath`. **Flagged rather than silently assigned** — it was missed when the split was written down, and assigning it quietly would hide that.

### 🔴 3.2 After M10, NOTHING writes the policy keys

Verified: `SettingsViewModel.cs:264-266` — the WPF Settings screen — is the **only** code in the product that writes `BackupFolderPath` / `AutoBackupEnabled` / `BackupKeepCount`. Zero writers in the API.

So "the policy keys stay in the writable store" describes a store that, after M10, **has no writer**. Either M11 adds admin routes for them, or those settings become host-file-edit-plus-restart, and *"backup is off and cannot be turned on"* becomes the shipped state. This is not optional garnish — `BackupFolderPath` is null and `AutoBackupEnabled` is false on the config this repo resolves against, so backup is **already** off.

## 4. F1 — `Program.cs:34` uses `||`, so `DbPath` alone is a silent no-op

```csharp
IAppConfig appConfig = string.IsNullOrWhiteSpace(configPath) || string.IsNullOrWhiteSpace(dbPath)
    ? new JsonAppConfig()                       // ← full production defaults, reads %APPDATA%
    : new JsonAppConfig(configPath, dbPath);
```

Setting `TimesheetApp:DbPath` in `appsettings.json` and nothing else falls straight through to the default constructor. The operator gets **no error** — the app starts happily against the wrong database. This is precisely the go-live path the user described, and today it fails silently.

**Fix:** required config with fail-fast. A missing location key must **refuse to start** and name what is missing, printing the legacy `%APPDATA%` value so the operator can copy it across. No fallback chain — the fallback chain *is* the bug.

## 5. F2 — the persisted file outranks the passed argument

```csharp
// JsonAppConfig.cs:57
_dbPath = model?.DbPath ?? defaultDbPath;
```

The `dbPath` argument is only a **default**. If the file at `ConfigPath` exists and carries a `DbPath`, **that file wins**. On any machine that has ever run the app, a `DbPath` in `appsettings.json` is dead weight.

**Decision (controller, 2026-07-19): `appsettings.json` WINS over the persisted store.** It follows directly from the user's go-live description — if the persisted file wins, `DbPath` in `appsettings.json` is meaningless and scenario 1 cannot happen.

⚠️ **Consequence to state out loud:** on a machine that has run the app before, changing `appsettings.json` will now **change which database it opens**. That is the intent, and it is also a foot-gun. The startup log must state the resolved database path unambiguously, every start.

🔴 **This is the seam `STATE.md` flags as having been insufficient for three milestones.** The invariant that saved the project repeatedly — *`ConfigPath` must point at a path that does not exist* — was a **test** discipline. This milestone changes production precedence. They must not be conflated: test isolation is unchanged (see §6).

## 6. F3 — resolved, and it protects the test suites

Proven empirically 2026-07-19 (`09f6e44`), three runs with a positive control: **`WebApplicationFactory.UseSetting` beats `appsettings.json`.** Adding an `appsettings.json` to `TimesheetApp.Api` does **not** retarget the API test suites.

Read the scope limit precisely: that was proven for `WebApplicationFactory`-hosted **tests**. The real API process has no `UseSetting` at all, so there `appsettings.json` *is* the source — which is exactly the mechanism this milestone needs. **Both halves are good news, and they are different facts.**

## 7. F4 and F5 — the documentation is already lying, and the name collides

- **F4:** `AdminBootstrap.cs:143` documents overriding bootstrap credentials *"in appsettings.json"* — a file that does not exist. This milestone makes that true, or the comment must be corrected. It cannot stay as-is.
- **F5:** 🔴 **Two different files will both be called `appsettings.json`** — `%APPDATA%\TimesheetApp\appsettings.json` (a flat POCO written by `JsonAppConfig.Save()`, never read by `builder.Configuration`) and `src/TimesheetApp.Api/appsettings.json` (real ASP.NET Core configuration). Anyone debugging this will open the wrong one. **Rename the writable store** as part of this milestone.

## 8. Out of scope

- Deleting WPF — that is M10 and lands first.
- Moving policy keys to a database table.
- Any change to how `DatabaseInitializer` creates a missing database. It already does the right thing.

## 9. Verification

- Fresh path in `appsettings.json`, no file there → a new database is created at exactly that path, and the startup log says so.
- Existing database at that path → it is opened, not replaced. **Assert row counts survive.**
- A location key missing → the process **refuses to start** and names the missing key.
- A stale `%APPDATA%` store carrying a different `DbPath` → **`appsettings.json` still wins**, and the log says which path won.
- Both API suites still target their own temp databases (regression guard on F3).
