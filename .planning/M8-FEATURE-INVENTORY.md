# M8 — FEATURE INVENTORY (WPF as-built)

**Ngày:** 2026-07-12
**Nguồn:** `src/TimesheetApp` — WPF .NET 8 · MVVM (CommunityToolkit.Mvvm) · SQLite + Dapper · ClosedXML · schema **v9**
**Mục đích:** (a) design brief để redesign UI, (b) nguồn viết REQUIREMENTS cho bản web (ASP.NET Core 8 + Angular).
**Quy ước:** `[KHÔNG CHẮC]` = chưa xác minh được 100% từ code.

---

## 0. Bản đồ tổng thể

### 0.1 Điều hướng — 7 destination

`MainWindow.xaml:72-104` — sidebar 218px, `ActiveView` là string key.

| Key | Sidebar | Nhóm | View | ViewModel |
|---|---|---|---|---|
| `timesheet` | 📅 Log Work | WORKSPACE | `TimesheetTab.xaml` | `TimesheetViewModel` |
| `backlog` | 📋 Backlog | WORKSPACE | `RequestsTab.xaml` (class **`BacklogsTab`**) | `BacklogsViewModel` (file `RequestsViewModel.cs`) |
| `tasklist` | ✅ Task List | WORKSPACE | `TaskListTab.xaml` | `TaskListViewModel` |
| `dailyreport` | 📝 Daily Report | WORKSPACE | `DailyInputTab` + `DailyBoardTab` | `DailyReportViewModel` |
| `reports` | 📊 Reports | WORKSPACE | `ReportsTab.xaml` | `ReportsViewModel` |
| `users` | 👥 Users | ADMIN | `UsersTab.xaml` | `UsersViewModel` |
| `settings` | ⚙ Settings | ADMIN | `SettingsTab.xaml` | `SettingsViewModel` |

> ⚠️ **Đổi tên chưa xong**: file tên `Requests*` nhưng class là `Backlogs*` (migration v6 Request→Backlog). **Spec web dùng thuật ngữ `Backlog`.**

### 0.2 Vòng đời khởi động (`App.xaml.cs:21-125`)

```
1. ThemeService.Apply(config.IsDarkMode)
2. DatabaseInitializer.InitializeAsync()          // DDL + migration v1→v9 + seed
3. TeamBootstrapService.EnsureBootstrappedAsync() // KHÔNG swallow lỗi
4. BackupService.AutoBackupIfDueAsync()           // best-effort
5. DefaultTaskSyncService.SyncAsync()
6. Nếu có ExportRoot: ExportHubService.BackfillAsync() → (nếu RetentionEnabled) RetentionService
   Ngược lại:         StandupArchive.BackfillMissingWeeks() + TaskListArchive.BackfillMissingMonths()
7. MainViewModel.InitializeAsync() → resolve user → resolve team → scan conflict copy → preload tabs
8. MainWindow.Show()
```
→ **Web**: bước 1/7/8 biến mất; 2–6 thành *migration + hosted service / cron*.

### 0.3 Cross-tab sync (in-process)

`Services/DataChangedMessage.cs` — `WeakReferenceMessenger` broadcast `DataChangedMessage(DataKind)`.
`DataKind` = `Backlogs, Tasks, Users, Logs, Templates, DefaultTasks, Standup, Tags, PcaContacts, Holidays, Teams`.

| Listener | Nghe |
|---|---|
| `TimesheetViewModel:76-81` | Tasks · Templates · DefaultTasks · Backlogs · Holidays |
| `TaskListViewModel:79-87` | Backlogs · Tasks · Logs · Tags · Holidays · PcaContacts |
| `DailyReportViewModel:45-49` | Standup · Users · Backlogs |
| `ReportsViewModel:65-71` | Logs · Users |
| `MainViewModel:91-94` + `CurrentTeamService:32-36` | Teams |

→ **Web**: ứng viên số 1 cho **SignalR hub**, hoặc refetch/invalidate query.
> ⚠️ Hiện messenger **chỉ sync trong 1 process** ⇒ 2 user mở app **không** thấy thay đổi của nhau real-time.

---

# PHẦN A — TỪNG MÀN HÌNH

## A1. Shell — `MainWindow.xaml`

**Mục đích:** khung app — sidebar, chip user, team switcher, 2 banner cảnh báo toàn cục.

```
┌────────────────────┬──────────────────────────────────────┐
│ [W] Worklog        │  ⚠ Banner conflict-copy (amber)      │
│     Desktop · WPF  │  ⛔ Banner journal warning (đỏ) + [X] │
│ WORKSPACE          ├──────────────────────────────────────┤
│  📅 Log Work       │  [icon] Feature Title   subtitle     │
│  📋 Backlog        │                                      │
│  ✅ Task List      │           <content panel>            │
│  📝 Daily Report   │                                      │
│  📊 Reports        │                                      │
│ ADMIN              │                                      │
│  👥 Users          │                                      │
│  ⚙ Settings        │                                      │
│ TEAM [ComboBox ▾]  │                                      │
│ (A) Nhan   ● Active│                                      │
└────────────────────┴──────────────────────────────────────┘
```
Window 1180×760, min 980×620, CenterScreen.

**Dữ liệu:** `CurrentUserName`/`Initial` ← `ICurrentUserService` · `AvailableTeams`/`ActiveTeam` ← `ICurrentTeamService` · `ConflictWarning` ← `SqliteMaintenance.FindConflictCopies` (`MainViewModel:287-294`) · `JournalWarning` ← `UiJournalWarningSink`

**Hành động:** nav RadioButton → `OnActiveViewChanged` reload VM · Team ComboBox → `SetActiveTeamAsync` → persist `%APPDATA%` + broadcast `Teams` → **reset mọi TeamFilter về {team mới}** · Dismiss journal banner

**Trạng thái đặc biệt:**
- Team switcher **ẩn khi user không thuộc team nào** (`ShowTeamSwitcher => AvailableTeams.Count >= 1`); 1 team → indicator read-only
- Avatar màu ổn định theo tên (char-sum hash, 5 màu: `#2563EB #0891B2 #7C3AED #DB2777 #16A34A`)
- Chấm xanh `#22C55E` "Active" — **hardcode, luôn hiện**
- Banner conflict-copy: chỉ hiện khi có `timesheet-<MACHINE>.db` cạnh DB (OneDrive conflict)
- Banner journal: cảnh báo mất toàn vẹn sau bulk-write bị ngắt

---

## A2. Log Work (Timesheet) — `TimesheetTab.xaml` + `TimesheetViewModel`

**Mục đích:** nhập giờ theo tuần (Mon–Fri), group theo Backlog, **auto-save từng ô**.

**Toolbar:** `◀ Prev` | `Week of dd/MM/yyyy` | `Next ▶` · DatePicker jump (snap về Monday) · status `Saving…`/`✓ Saved`/`⚠ error` · `⚡ Smart fill` (disable khi team-view) · `☰ Collapse all` (nhớ qua Settings key `entry.collapseAll`) · chip phải `Week total {N.N}h`

**Filter:** `User:` (Whole team read-only + từng active user) · `Month:` 1-12 · `Year:` (y-2..y+3) · badge amber `Read-only (whole team)`

**Bảng:** grid 7 cột `TASK | MON dd/MM | TUE | WED | THU | FRI | TOTAL`
- **Section band / Backlog** (ToggleButton = collapse): `▾ CODE · Project [Type pill] yyyy-MM [👤 Assignee]` … `{N.N}h`
- **Task row:** `⠿ TaskName | [Mon][Tue][Wed][Thu][Fri] | RowTotal` — ô = TextBox borderless, `UpdateSourceTrigger=LostFocus`
- **Inline cuối group:** `➕ Add task` · `Move to next month ▶` (ẩn với DEFAULT)
- **Trash zone** pinned bottom: `🗑 Drop a task here to delete` (chỉ khi `CanEdit`)
- **Footer:** `DAY TOTALS | … | WeekTotal`

**Dữ liệu:** `ITimeLogService.GetWeekGroupedAsync(userId, monday)` / `GetWeekGroupedAllUsersAsync` nếu team-view.
Model `WeekBacklogGroup(BacklogId, BacklogCode, Project, Tasks[WeekRow], PeriodMonth, Type, AssigneeName)`.
Lọc tháng: group hiện nếu `PeriodMonth` rỗng **hoặc** == tháng filter ⇒ **DEFAULT luôn hiện ở mọi tháng**.
**Scope: active team ONLY** (`TimeLogService:162, 212-213`).

**Validation 2 tầng:**
- Client (`TimesheetRowVm:46-59`): `>0`, `≤8`, ≤1 decimal → lỗi = viền đỏ, **không ghi DB**
- Server (`TimeLogService.SaveCellAsync:38-56`): `>0` · ≤1 decimal · **không T7/CN** · **không holiday** · cell ≤8h · **tổng ngày (mọi task) ≤8h**

**Hành động:** gõ ô → LostFocus → auto-save · xoá ô = **DELETE row** · Add task (`TaskInputDialog`) · drag `⠿` reorder (**chỉ trong cùng group**) · drag → trash = **soft-delete** (`is_active=0`, TimeLogs giữ) · `Move to next month` → `period_month +1` (**DEFAULT bị chặn**) · `⚡ Smart fill`

**Trạng thái đặc biệt:**
- 🔒 **Team view = read-only toàn bộ** — mất trash/drag/Add, cell không lưu (`SaveCellAsync` trả Ok giả)
- 🎨 **Cột holiday:** nền `HolidayBg`, `IsReadOnly`, watermark **"Holiday"**, tooltip
- 🔴 Footer day-total **> 8h** → chữ đỏ + bold
- Collapse state giữ qua reload

---

## A3. Backlog — `RequestsTab.xaml` (class `BacklogsTab`)

**Mục đích:** CRUD backlog + task của nó; áp template; xem change history.

**Toolbar:** Search (*"Search code or project…"*, **filter in-memory**) · `➕ New backlog` · Filter: `Project ▾` `Type ▾` `Assignee ▾` `Month ▾` (mỗi cái có `All`) + `<TeamFilter>`

**DataGrid** (read-only, single-select):

| Cột | Nội dung |
|---|---|
| CODE · PROJECT · MONTH (`yyyy-MM`) | |
| TYPE | pill teal, ẩn nếu null |
| ASSIGNEE | |
| TEAM | pill teal — **cả cột ẩn nếu chỉ 1 team check** |
| TASKS | số task active |
| (action) | `Edit` |

> ❗ **KHÔNG có Delete backlog ở bất kỳ đâu** (REQ-04 — backlog không soft-delete được).

**Editor overlay** (modal, scrim `#66000000`, card 540×660):

| Field | Create | Edit |
|---|---|---|
| Code · Project (`ARCS/PlusArcs/ARMS/Other`) · Assignee · **Month\*/Year\*** (bắt buộc) · Type (`Continue/Implement/Investigate/IT/Estimate`) | ✅ | ✅ |
| Rough estimate · Official estimate · Note · Add-a-template · Tasks | ✅ | ✅ |
| Start/End date · Internal deadline (PCT) · External deadline (PCA) · Progress% (disabled) · PCA contact · Tags | ✅ | **❌ ẩn** |
| Change history (`field: old → new` + `who · dd/MM HH:mm`) | ❌ | ✅ |

> 🔑 **QUY TẮC NGHIỆP VỤ:** field "operational" (start/end, 2 deadline, progress, PCA, tags) **chỉ nhập lúc CREATE**; khi EDIT bị ẩn → **phải sửa inline trong Task List**.

**Validation:** `SaveNewAsync` — ❗ **bắt buộc ≥1 task**, nếu 0 → *"A backlog must have at least one task."* · stamp `TeamId = activeTeam`. `SaveEditAsync` — **giữ nguyên `TeamId` cũ**; soft-delete task `IsRemoved && ExistingTaskId>0`.
Estimate: rỗng→null; không phải số hoặc <0 → null + error. Progress: int 0–100.
Template select → `ApplyTemplate()`, chống duplicate bằng `_lastAppliedTemplate`.

---

## A4. Task List — `TaskListTab.xaml` + `TaskListViewModel` (811 dòng — phức tạp nhất)

**Mục đích:** theo dõi tiến độ/deadline mọi backlog (trừ DEFAULT) trong 1 tháng; **inline edit** mọi field operational; Gantt; export markdown; continue-next-month.

**Toolbar:** `Month ▾` (0 = **All months**) + `Year ▾` + `<TeamFilter>` · segmented `[Grid][Gantt]` · `Hide/Show chart` · `⬇ Export this month` + status

**GRID = CARD LIST** (không phải DataGrid), nhóm bằng `CollectionViewSource`:
- **Section band** (Expander): `{GroupKey} ({ItemCount})`
- **Adaptive grouping** (`:212-217`): >1 team check → group theo **Team**; ngược lại → **Project**
- Sort: `GroupOrder` (project theo enum ARCS→PlusArcs→ARMS→Other; team alpha) rồi `BacklogCode`

```
┌──────────────────────────────────────────────────────────────────────┐
│ [⚠ Late] [🔥 tag] [⭐ tag]      [✎ Tags]  [↻ Continue]               │ ← tag strip
│ ▸ REQ-1234 (Project)   PCT[▾]  Internal[📅] External[📅] [▓▓░ 60%]  │ ← compact header
│   ── expand ──                                                       │
│   Type[▾] PCA[▾] Start[📅] End[📅]  Logged 12   Estimation 20        │
│   • TaskName   Type[▾] PCT[▾] Status[▾] [✎ Tags]                    │
└──────────────────────────────────────────────────────────────────────┘
```

**Inline edit — commit NGAY:**

| Control | Path | Audit |
|---|---|---|
| PCT / Type / PCA ComboBox | TwoWay → `CommitBacklogEditAsync` | ✅ field-diff |
| **Internal/External DatePicker** | code-behind → **mở `DeadlineNoteDialog` hỏi lý do** → `CommitDeadlineAsync` | ✅ **có `note`** |
| Start/End DatePicker | → `CommitStartEndAsync` — **KHÔNG hỏi lý do** | ✅ (note=null) |
| Progress bar | click → TextBox 0-100; **Enter/click-away = commit, Escape = restore** | ✅ |
| `✎ Tags` | `TagSelectDialog` → mỗi tick commit ngay | ✅ (1 row `tags: "1,3" → "1,3,5"`) |
| Task row Type/PCT/Status/Tags | `UpdateTaskExtendedAsync` / `UpdateTaskStatusAsync` / `SetTaskTagsAsync` | ✅ TaskAudit |
| `↻ Continue` | `BacklogContinuationService.ContinueAsync` | ✅ `continued` |

**🎨 Chip system** (`BuildChips:700-711`) — **1 system chip trước, rồi custom tag theo `Tag.Id`**:

| Chip | Điều kiện | Style |
|---|---|---|
| `⚠ Late` | `ScheduleState.Late` | nền **Danger đỏ**, chữ trắng |
| `⚠ At risk` | `ScheduleState.Warning` | nền `AmberBg`, viền `AmberBorder`, chữ `AmberFg` |
| custom tag | mỗi link | nền = `Tag.Color`, icon emoji, chữ trắng |

> Late **loại trừ** Warning (chỉ 1 system chip).

**Khác:** Progress **0% khi null** (P16) · empty expand: *"Chưa có task nào trong backlog này."* · `↻ Continue` **ẩn khi Month = All** · export khi All → *"Pick a specific month to export."* · cột PROJECT trên card **chỉ hiện khi group-by-Team**

**Gantt** (`Canvas` vẽ tay, `TaskListTab.xaml.cs:214-388`):
- Trục X = **chỉ working day** (bỏ T7/CN + holiday), từ `min(start ?? end)` → `max(deadline_internal ?? end_date)`
- `MinDayWidth=26px`, gutter trái 130px cho `BacklogCode`
- **Bar** = `start_date` → `deadline_internal` (fallback `end_date`); màu **Late=đỏ · Warning=Amber · Normal=Accent teal**
- **Không có start_date** → khung **nét đứt xám mờ** (opacity .55) trải hết plot
- **External deadline (PCA)** → tam giác đỏ chỉ xuống + đường nét đứt đỏ
- Empty → *"No dated backlogs to chart for this month."*
> ✅ **Logic index math (`BuildGantt:314-376`) PORT 1:1 được** — không có pixel nào, chỉ trả `Axis[]` + `Bars[]` với `StartDayIndex`/`SpanWorkingDays`/`ExternalMarkerIndex`.

---

## A5. Daily Report — `DailyInputTab` + `DailyBoardTab`

**Toolbar chung** (ở `MainWindow.xaml:219-229`): `◀` `[DatePicker]` `▶` · `Archive week` · status

### A5a. Input — standup của CHÍNH TÔI

```
[➕ Add entry] [⬇ Quick import]      ← chỉ khi CanEditSelectedDay
┌ Yesterday ──────────────────────┐
│ ⠿ REQ-01 · Task name    [Todo]   │
│   description                    │
│   Deadline: 2026-07-20           │
│   ── ISSUES        [➕ Issue] ── │
│   ⚠ issue text              [✕]  │  amber (chưa có solution)
│      No solution yet [➕ Add solution]
│   ✓ issue text              [✕]  │  green (có solution)
│      Solution [___] [status▾] [Save]
│                   [Delete entry] │
└──────────────────────────────────┘
┌ Today ───────────────────────────┐ … └
🗑 Drop an entry here to delete
```

**🔒 EDIT-LOCK (quy tắc trung tâm)** — `StandupService.cs:34-38`
```csharp
public bool CanEditDay(DateOnly workDate) {
    var today = _clock.Today;
    return workDate == today || workDate == today.AddDays(-1);
}
```
Chỉ **hôm nay + hôm qua**. Khoá → *"This day is locked (only today and yesterday are editable)."*, ẩn Add/Quick-import/trash/drag.
Ngoài lock: **entry chỉ owner sửa được**. **Issues KHÔNG owner-gate** — cố ý (collaborative, DR-04).

**Quick Import** (`StandupService:118-151`): clone entry+issue từ ngày nguồn → ngày hiện tại, **append**, id/order/timestamp mới. Nguồn không đổi. Scope current-user + active team. Trả 0 → *"Nothing to import from that day (or this day is locked)."*

**Drag `⠿`:** reorder; **thả sang section khác = chuyển section**. Guard: owner + edit-lock + cùng ngày.

### A5b. Team board — read-only
- `<TeamFilter>` · 1 **Card / user**: avatar + tên → band `Yesterday`/`Today` → entry `CODE · TaskText · due yyyy-MM-dd` + `[Status pill]` + description + issues
- **Card = MEMBER của các team đã check** (union `UserTeams`), **không phải** mọi active user ⇒ user chưa report vẫn hiện card rỗng = signal *"ai chưa report"*
- teamIds rỗng/0 → **board rỗng** (không leak all-teams)

**🎨 Màu issue (cả 2 tab):**

| Trạng thái | Icon | Nền | Viền | Chữ |
|---|---|---|---|---|
| Chưa có solution | `⚠` | `AmberBg` | `AmberBorder` | `AmberFg` |
| Có solution | `✓` | `BadgeGreenBg` | `ResolvedBorder` | `BadgeGreenFg` |

> Trigger là `HasSolution => !IsNullOrWhiteSpace(SolutionText)` — **không phải** `Status == "resolved"`.

**`Archive week`** → markdown `{yyyyMMdd}_daily.md` (Monday stamp).

---

## A6. Reports — `ReportsTab.xaml`

```
⚠ Missing time logs                                    ← banner amber (MaxH 96)
─────────────────────────────────────────────────────────────────────
Report for:[▾] Week(Mon):[📅] Month:[📅] Project:[▾] <TeamFilter> [⬇ Export to Excel]
─────────────────────────────────────────────────────────────────────
┌WEEK TOTAL┐ ┌AVG / DAY┐ ┌DAYS LOGGED┐ ┌⚠ NOT LOGGED┐   ← 4 stat card
│  32.0h   │ │  8.0h   │ │   4 / 4   │ │      2      │
└──────────┘ └─────────┘ └───────────┘ └─────────────┘
┌ Weekly (by day · ticket · task) ┐  ┌ Drill-down      [Expand all] ┐
│ Date | Ticket | Task | Hours    │  │ ▾ Team — 32.0h               │
├ Monthly (by backlog / task) ────┤  │   ▾ ARCS — 20.0h             │
│ Backlog | Project | Task | Hours│  │     ▾ REQ-01 — 12.0h         │
└─────────────────────────────────┘  │       ▾ Coding — 8.0h        │
                                      │         Mon, 2026-07-06—4.0h │
```

**Filter → auto-load** (không có nút Load). `_autoLoad` chỉ bật sau `LoadUsersAsync()`.

**4 báo cáo** (`ReportAggregator.cs`):

| # | Tên | Shape | Sort |
|---|---|---|---|
| RPT-01 | `WeeklyDayTotals` | 1 row / distinct WorkDate | date |
| RPT-01d | `WeeklyDetailRows` | 1 row / (Date, BacklogCode, Project, TaskName) | date → code → task |
| RPT-02 | `MonthlyBacklogTaskTotals` | 1 row / (BacklogCode, Project, TaskName) | code → task |
| RPT-03 | `BuildProjectTree` | **Team → Project → Backlog → Task → Date** | Ordinal; team null → `"(no team)"` |

> **Cả 2 query INNER JOIN không lọc `is_active`** (XC-06) ⇒ task/user đã soft-delete **vẫn hiện tên** trong báo cáo.

**🐛 QUIRK (khả năng là bug)** — `RecomputeWeeklyStats:131-139`:
```csharp
var span = WeeklyRows.Count == 0 ? 5 : WeeklyRows.Count;
DaysLoggedText = $"{logged} / {span}";
```
`WeeklyRows` chỉ chứa ngày **có** log ⇒ `logged == span` luôn ⇒ **"DAYS LOGGED" hầu như luôn là `N / N`**, không bao giờ `3 / 5`.
→ **Bản web nên sửa: mẫu số = 5 (working day trong tuần).**

**Banner "chưa log"** (RPT-04, `TimeLogService:238-255`): `N` ← Settings key **`chua_log_n_days`** (default **3**). Cửa sổ = N working day gần nhất **tính cả hôm nay**, chỉ bỏ T7/CN, **KHÔNG trừ holiday** (`LastNWorkingDays:270-280` — khác `WorkingDayCalculator`, **inconsistency**). Flag = active user thuộc active team, không có log nào trong cửa sổ. `activeTeamId == 0` → rỗng.

**Export Excel:** `ExportService.ExportExcelAsync(filter)` → sheet "Timesheet", 7 cột **Team | User | Backlog | Project | Task | Date | Hours**. Tên gợi ý `Worklog-{yyyy-MM}-{who}.xlsx`.

---

## A7. Users — `UsersTab.xaml` (52 dòng, đơn giản nhất)

- Input: TextBox (*"New user name…"*) + `➕ Add user`
- DataGrid: **Name** (avatar hash + bold) · **Status** (pill `Active` xanh / `Inactive` xám) · **Deactivate** (disable nếu đã inactive)
- `LoadAsync` = `GetAllAsync()` — **bao gồm inactive**
- `AddUserAsync`: trim → INSERT `is_active=1`, `windows_username=null`
- `DeactivateAsync`: **soft-delete** — TimeLogs giữ nguyên (USR-03)
- ❌ Không Edit tên · không Delete cứng · **không gán team ở đây** (gán team ở Settings → Teams → Members)

---

## A8. Settings — `SettingsTab.xaml` (677 dòng) — 13 section + 3 overlay

| # | Section | Nội dung |
|---|---|---|
| 1 | **Appearance** | ☑ Dark mode → `ThemeService.Apply` (**live, không restart**) |
| 2 | **Database file** | path + `Browse…` + `Apply` → `%APPDATA%\TimesheetApp\appsettings.json` |
| 3 | **Daily report archive** | folder + `Apply` |
| 4 | **Export logs** | **Root1** (Shared/SharePoint): path + `Browse…` + **`Verify`** + `Apply` · **Root2** (Local) · `Export now` + status |
| 5 | **Backup & Restore** | folder · ☑ auto-backup once/day · `Keep last [30]` · `Backup now` · `Refresh` · **list backup** + `Restore` (MessageBox confirm + hỏi đóng app) |
| 6 | **Data retention** ⚠️**DESTRUCTIVE** | ☑ Enable · `Keep last [3] months` · `Preview` (dry-run) · **`Run retention now`** (MessageBox confirm bắt buộc) |
| 7 | **"Not logged" warning** | `Working days [3]` + `Save` → Settings key `chua_log_n_days` |
| 8 | **Task templates** | `➕ New` + list + Edit/Delete → overlay |
| 9 | **Default tasks** | `Sync default tasks` |
| 10 | **Tags** | `➕ New tag` + list (chip màu live) + Edit + **Delete = HARD** |
| 11 | **PCA contacts** | add + rename inline + Deactivate (soft) |
| 12 | **Teams** | add + rename + **`Members`** + Deactivate · `AddTeam` → **tự tạo DEFAULT backlog cho team + sync default tasks** |
| 13 | **Holiday calendar** | lịch 7 cột, click ngày = toggle holiday → broadcast `Holidays` |

**🎨 Màu ô lịch holiday** (ưu tiên tăng dần): out-of-month → transparent + disabled · weekend → `BadgeGrayBg/Fg` · **holiday → `Accent` teal + chữ trắng** (thắng weekend)

**3 overlay modal:**
1. **Template editor** (500px) — tên + list task + `➕ Add task`. Save = **delete-all-then-reinsert** (xử lý rename + reorder 1 phát). Validation: tên bắt buộc, ≥1 task.
2. **Tag editor** (460px) — **live preview chip** + Label + Icon (8 quick-pick `🔥 ⭐ 🐛 ⚠️ 🚀 📌 ✅ 🔍`) + Color (hex + 8 swatch `#0F766E #2563EB #7C3AED #DB2777 #DC2626 #D97706 #16A34A #64748B`). Rỗng → `#64748B`.
3. **Team membership** (420px) — checkbox list active users → `SetMembersAsync` **replace-all**.

---

## A9. Dialogs (8)

| Dialog | Size | Mục đích | Validation |
|---|---|---|---|
| **SmartInputPreviewDialog** | 820×600 | Smart fill: form trái / preview phải | xem §B1 |
| **TaskInputDialog** | 400 | Nhập tên task | rỗng → *"Please enter a task name."* |
| **StandupEntryDialog** | 460 | Thêm standup entry | code + task bắt buộc |
| **StandupIssueDialog** | 420 | Thêm issue | issue text bắt buộc |
| **QuickImportDialog** | 380 | Chọn ngày nguồn (default = **hôm qua**) | phải chọn ngày |
| **SelectUserDialog** | 380 | Chọn/tạo user | ⚠️ **DEAD CODE — §D3** |
| **DeadlineNoteDialog** | 400 | Hỏi **lý do đổi deadline** | không có (`Reason` có thể rỗng) |
| **TagSelectDialog** | 300 | Tick tag (tiếng Việt: *"Chọn tag"* / *"Xong"*) | commit ngay mỗi tick |

> **Chrome chung:** `WindowStyle=None` + `AllowsTransparency` + Border `CornerRadius=12` + `DropShadow`. → **Toàn bộ custom-chrome, dựng lại bằng Angular Material Dialog / CDK Overlay.**

**SmartInputPreviewDialog chi tiết:**
- Trái (330px): `Backlog (search by part of the code)` + `Find` → checkbox list task → `Distribution` radio (`Split evenly` / `Full 8h`) → `From`/`To` DatePicker → `Total hours` (**disable khi Full 8h**) → `Preview` + error đỏ
- Phải: DataGrid `Task | Date | Hours`
- Footer: `Cancel` / `Confirm` (enable khi `CanApply`)

---

## A10. Controls

**`TagPicker`** — ToggleButton `Tags (N) ▾` → Popup: type-to-filter + CheckBox chip màu. Header đếm checked **qua reflection**. Event `TagsCommitted`.
> ⚠️ **Chỉ dùng trong Backlog editor (create mode)**. Task List phải dùng `TagSelectDialog` vì *"An in-grid Popup closes before a checkbox can be ticked"* — hạn chế thuần WPF.

**`TeamFilter`** — `Teams: [Teams (N) ▾]` → Popup checkbox.
- **`ShowFilter => AvailableTeams.Count > 1`** — ẩn hoàn toàn với user 1 team
- **`ShowTeamColumn => CheckedTeamIds.Count > 1`** — driver ẩn/hiện cột TEAM + đổi grouping Task List
- **Default = chỉ active team**
- **Lazy seed** — ctor chạy trước `InitializeAsync`; comment nói raise event trong Initialize gây **stack overflow** do re-enter WPF Measure
- Owner: Backlog · Task List · Reports · Daily Board

**`BindingProxy : Freezable`** — hack để `DataGridColumn` (ngoài visual tree) bind được VM. → **Không cần trên web.**

---

# PHẦN B — NGHIỆP VỤ

## B1. Smart Input / Smart Fill

### ⚠️ CÓ **HAI** IMPLEMENTATION, CHỈ **MỘT** CHẠY THẬT

**(a) `SmartInputService`** — **ĐĂNG KÝ DI NHƯNG KHÔNG BAO GIỜ ĐƯỢC GỌI**. Inject vào `TimesheetViewModel:38` nhưng **không gán field, không dùng**. Có 2 hàm **holiday-aware**: `DistributeEven` (integer-tenths, dư dồn ngày cuối) · `FillFull8h`.

**(b) `SmartInputPanelVm.BuildPlan`** (`:158-211`) — **ĐÂY MỚI LÀ CÁI CHẠY THẬT**:

```
days = WorkingDays(From, To)   // ⚠️ CHỈ bỏ T7/CN — KHÔNG bỏ holiday

── "Split evenly" ──
  totalTenths = round(TotalHours × 10)
  perTaskTenths[i] = totalTenths/N + (i < totalTenths%N ? 1 : 0)   // dư dồn task ĐẦU
  cell[j]          = perTaskTenths[i]/D + (j==D-1 ? perTaskTenths[i]%D : 0)  // dư dồn ngày CUỐI

── "Full 8h" ──  DayCapTenths = 80
  perDay[i] = 80/N + (i < 80%N ? 1 : 0)     // dư dồn task ĐẦU
```
**Toàn bộ số học dùng integer tenths** (tránh float drift — comment "PITFALL §2").

VD: 3 task, Full 8h → perDay = [27, 27, 26] tenths → **2.7 + 2.7 + 2.6 = 8.0h/ngày** ✅

**Luồng Apply:**
```
Find → chọn task → Preview → BuildPlan() → TimeLogService.ValidateSmartFillAsync() → CanApply
Confirm → validate lại
        → DbBackupHelper.BackupAsync()      ← XC-10: BACKUP TRƯỚC BULK WRITE
        → UpsertBatchAsync (1 transaction)  ← SI-05 atomic
        → check journal gone → còn → banner đỏ
        → reload + broadcast Logs
```

**`ValidateSmartFillAsync`** (`TimeLogService:102-133`): ≥1 cell > 0 · mỗi date **không weekend, không holiday** · `(giờ đã lưu ở task KHÔNG check) + (tổng giờ đề xuất của task được check) ≤ 8h` — task được check thì **overwrite**, không cộng dồn.

> 🐛 **HỆ QUẢ:** `BuildPlan.WorkingDays` **không loại holiday** nhưng `ValidateSmartFillAsync` **có** ⇒ range chứa holiday → preview vẫn hiện ô đó nhưng Preview báo lỗi `"{date} is a holiday."` và không cho Apply.
> → **Web nên hợp nhất về 1 nguồn (dùng logic `SmartInputService`).**

`FindBacklogAsync` — `LIKE %term%` trên `backlog_code` OR `project`, **scoped active team**, **loại DEFAULT**.

---

## B2. Timesheet / TimeLog — quy tắc cốt lõi (`DayCap = 8m`)

| Rule | Thông điệp |
|---|---|
| Giờ > 0 | `Hours must be greater than 0.` |
| ≤ 1 decimal | `Hours may have at most 1 decimal place.` |
| Chỉ Mon–Fri | `Time can only be logged Monday–Friday.` |
| Không holiday | `{date} is a holiday.` |
| 1 ô ≤ 8h | `A single cell cannot exceed 8h.` |
| **Tổng ngày (mọi task) ≤ 8h** | `Total for {date} would be {X}h (> 8h).` |
| Làm tròn | `Math.Round(v, 1, AwayFromZero)` |
| Xoá ô | **DELETE row** (không lưu 0) |
| Upsert | `ON CONFLICT(user_id, task_id, work_date) DO UPDATE` |

**`null` (ô trống) ≠ `0`** — semantic distinct.

---

## B3. Multi-team

```
AvailableTeams = UserTeams(user) ∩ Teams(is_active=1)
ActiveTeamId   = config.ActiveTeamId nếu còn valid : AvailableTeams[0] : 0
```
- `ActiveTeamId` persist ở **`%APPDATA%`** — *cố ý KHÔNG để trong DB shared*, vì 2 user cùng DB OneDrive sẽ "giành" active team.
- Nghe `DataKind.Teams` → re-resolve. `_suppressReentry` guard chống feedback loop.
- ⚠️ **KHÔNG raise `ActiveTeamChanged` trong `InitializeAsync`** — comment: **stack-overflow** WPF Measure.

### 🔒 Quy tắc chống leak (**R6**, lặp khắp code)
> `teamIds == null` ⇒ **không lọc** (all teams — legacy).
> `teamIds` **rỗng** hoặc `teamId == 0` ⇒ **match KHÔNG có gì** (không bao giờ = "all").

SQL: `WHERE (@noTeam OR team_id IN @teamIds)`.

| Màn | Scope |
|---|---|
| Log Work · Daily **Input** | **Active team ONLY** |
| Backlog · Task List · Daily **Board** · Reports · Export | TeamFilter (multi) |

**`TeamBootstrapService`** (1 lần lúc start): đã có team → re-run backfill (idempotent) · có business data → tạo `"Architect Improvement"` · ngược lại → `"My Team"`.
Backfill 1 transaction:
```sql
UPDATE Backlogs       SET team_id=@t WHERE team_id IS NULL;
UPDATE StandupEntries SET team_id=@t WHERE team_id IS NULL;
INSERT OR IGNORE INTO UserTeams(user_id, team_id) SELECT id, @t FROM Users;
```

**`DEFAULT` backlog — 1 CÁI / TEAM** (TM-04). Tạo team mới → **tự tạo DEFAULT backlog + sync default tasks**.

---

## B4. Daily Report / Standup

```
StandupEntry(Id, UserId, WorkDate, Section["yesterday"|"today"], BacklogId?, BacklogCode,
             TaskText, Description, Deadline?, Status, OrderIndex, CreatedAt, TeamId?)
StandupIssue(Id, EntryId, IssueText, SolutionText?, Status, OrderIndex, CreatedAt)
```
- Entry `Status` ∈ `Todo | In-process | Done | Pending` · Issue `Status` ∈ `open | pending | resolved`
- **`BacklogId` nullable** — code ad-hoc gõ trong meeting (DR-03). Save → `ResolveBacklogIdAsync`, không tồn tại → `null`
- Issue **CASCADE DELETE** theo entry
- `OrderIndex` per (user, day, section, **team**)
- **Edit-lock**: today + yesterday · **Owner-gate**: entry only · **Issue: ai cũng sửa** (cố ý)

---

## B5. Task List — nghiệp vụ

### `ScheduleStateService.Evaluate` (`:9-45`) — QUY TẮC CẢNH BÁO
```
1. isDone                     → Normal   (Done tắt mọi chip)
2. deadline_internal == null  → Normal
3. today > deadline_internal  → LATE     (Late THẮNG Warning)
4. start_date == null         → Normal
5. estimate == null || <= 0   → Normal
6. workingDays(today+1 .. deadline) > 2  → Normal      ("≤2 ngày tới hạn")
7. total = workingDays(start .. deadline); total <= 0 → Normal
8. elapsed = workingDays(start .. min(today, deadline))
9. behind = loggedHours × total < elapsed × estimate    ← cross-multiply, tránh chia 0
   behind ? WARNING : Normal
```
**"At risk"** = còn ≤2 working day tới internal deadline **VÀ** tiến độ giờ log chậm hơn tiến độ thời gian.

**`isDone`** = `taskList.Count > 0 && taskList.All(t => t.Status == "Done")` → **0 task = KHÔNG Done**.

**Estimate precedence:** `OfficialEstimateHours ?? RoughEstimateHours` (official thắng).

**Logged hours:** `SELECT t.backlog_id, SUM(l.hours) … GROUP BY t.backlog_id` → **ALL-TIME** (không lọc tháng), **không lọc `is_active`** (task soft-delete vẫn tính giờ).

**Working-day math:** `IsWorkingDay(d, holidays) = !Sat/Sun && !holidays.Contains(d)` · `CountWorkingDays` inclusive 2 đầu; `from>to → 0`.

**Progress %:** nhập tay 0–100 (không tự tính). Backlog editor: **disabled**. Task List: **inline editable**. null → **`0%`**.

**Continue to next month** (`BacklogContinuationService`):
```
1. Load backlog nguồn
2. Duplicate guard: cùng code + cùng targetPeriod trong CÙNG TEAM → return 0
3. Clone: Id=0, PeriodMonth=target, Type="Continue", CreatedAt=now
   → GIỮ NGUYÊN mọi field khác (deadline, estimate, progress, note, PCA, assignee, team)
4. Copy backlog tags
5. Copy task CHƯA Done (kèm type/assignee/tags)     ← task Done bị bỏ
6. Audit: field='continued', note="continued from {period}"
7. Backlog gốc KHÔNG đổi
```
> ⚠️ **Khác "Move to next month"** ở Log Work — cái đó **CHUYỂN** (update `period_month`), không clone.

**Tags:** `Tag(Id, Text, Icon, Color, CreatedAt)` — **hard-delete**. N:N qua `BacklogTags` / `TaskTags`. `SetTagsAsync` = **replace-all 1 tx** + **đúng 1 audit row** `field='tags'`.

**Holidays:** `Holiday(Date PK, Description?)` — mark tay ở Settings. Ảnh hưởng: TimeLog validation · working-day math · Gantt axis · cột holiday ở Log Work.

---

## B7. Export / ExportHub / SharePoint

**`ExportService`** (headless):
- **Excel** (ClosedXML): sheet "Timesheet", `Team|User|Backlog|Project|Task|Date|Hours`
- **Markdown**: `# Timesheet — yyyy/MM` → `## Team` → `### User` → `#### {Code} — {Project}` → bảng `| Date | Task | Hours |`. Escape `|` → `\|`. Giờ: `4` không phải `4.0`.

**`ExportHubService`** — cấu trúc:
```
{ExportRoot}/
  {TeamName-sanitized}/
    tasklist/  {yyyyMM}_tasklist.md
    timesheet/ {yyyyMM}_timesheet.md
    daily/     {yyyyMMdd}_daily.md      (Monday stamp)
  db/
    timesheet_{stamp}.db
    prune-snapshots/timesheet_{yyyyMM}_pre-prune_{stamp}.db   ← NEVER auto-pruned
```
- **2 root**: Root1 (Shared/SharePoint) + Root2 (Local) — **mirror**. Root rỗng → skip.
- `ExportNowAsync` = tháng này + tháng trước · tuần này + tuần trước
- `BackfillAsync` = 12 tháng + 12 tuần đã hoàn tất (skip nếu file tồn tại)
- **Không data → không tạo file**
- **Per-root best-effort**: 1 root fail không chặn root kia
- **Dedupe segment** (I-1): 2 team name sanitize ra cùng segment → append `-{teamId}`

**`SharePointDestinationValidator`**:

| Điều kiện | Level |
|---|---|
| rỗng | Error — `Enter a folder path first.` |
| `http(s)://` | **Error** — *"That looks like a web URL. In SharePoint use 'Open in Explorer'…"* |
| write-probe fail | **Error** |
| writable + (UNC / `sharepoint` / `DavWWWRoot` / `OneDrive` / drive type=Network) | **Ok** |
| writable nhưng local thuần | **Warning** — *"files stay on this PC…"* |

`ExportHubService` gọi Verify trước mỗi root: **Error → skip root**; Warning → vẫn export.

> ⚠️ **"SharePoint" hiện chỉ là `File.Copy` vào mapped drive / UNC — KHÔNG có Graph API, KHÔNG có MSAL, KHÔNG có auth.**

---

## B8. Backup — HAI service riêng biệt

| | `DbBackupHelper` (XC-10) | `BackupService` (BK-01..07) |
|---|---|---|
| Trigger | **Tự động trước mỗi bulk-write** (smart-fill, default-task sync, team-backfill, retention) | User bấm / auto-once-per-day |
| Đích | **cạnh DB**: `{db}.{stamp}.bak` | folder user chọn: `timesheet_{stamp}.db` |
| Keep | 10 (const) | `config.BackupKeepCount` (default 30) |
| Restore | ❌ | ✅ (safety-copy `{db}.pre-restore_{stamp}.bak` **trước** khi overwrite) |

**Restore guard:** từ chối restore file DB đang live lên chính nó.
**Journal check (XC-09):** sau bulk-write, `{db}-journal` còn tồn tại → banner đỏ.

---

## B9. Retention / Prune (P12 — DESTRUCTIVE, default OFF)

**Cutoff:** `first-of-this-month − RetentionMonths` → `"yyyy-MM"`. So sánh **lexical string** (hợp lệ vì ISO zero-padded).

**`EnsureRetentionAsync`** — thứ tự guard rất chặt:
```
(a) FindConflictCopies(db) > 0 → ABORT TOÀN BỘ
(b) months <= cutoff có data. Rỗng → "nothing to prune"
(c) FOREACH month (cũ→mới):
      snapshot = PruneArchiver.ArchiveMonthForPruneAsync(y, m)
      NẾU snapshot null || !Exists || Length == 0 → warning + BREAK   ← giữ prefix liên tục
      effectiveCutoff = month
(d) effectiveCutoff null → không prune
(e) DbBackupHelper.BackupAsync()          ← full backup
(f) MỘT transaction, children-first:
      1. DELETE StandupIssues → 2. StandupEntries → 3. TimeLogs
      4. DELETE Tasks   — CHỈ task KHÔNG còn TimeLog, thuộc backlog prunable non-DEFAULT
      5a. DELETE BacklogAudit của đúng bộ backlog sắp xoá (FK NOT NULL RESTRICT)
      5. DELETE Backlogs — prunable, non-DEFAULT, KHÔNG còn Task
      6. DELETE BacklogTags orphan
(g) Settings["retention.pruned_through"] = effectiveCutoff
(h) journal-gone check
```
**Bảo toàn:** backlog **spanning** (còn TimeLog trong window) **sống sót** · **DEFAULT của mọi team KHÔNG BAO GIỜ bị prune** · audit của backlog sống sót giữ nguyên.

> ⚠️ **Không có ExportRoot ⇒ `ArchiveMonthForPruneAsync` trả null ⇒ KHÔNG BAO GIỜ prune.**

---

## B10. Theme (light/dark)

`ThemeService` swap **ResourceDictionary tại chỗ** trong `Application.Current.Resources.MergedDictionaries`. Mọi màu consume qua **`{DynamicResource}`** ⇒ đổi live, không restart.

**~40 design token** (2 file cùng key: `Palette.Light.xaml` / `Palette.Dark.xaml`):

| Nhóm | Key | Light | Dark |
|---|---|---|---|
| Accent | `Accent` | `#0F766E` | `#0D9488` |
| | `AccentHover` / `AccentPressed` / `AccentSoft` | `#115E59` / `#134E4A` / `#F0FDFA` | `#14B8A6` / `#0F766E` / `#134E4A` |
| Surface | `WindowBg` / `Surface` | `#E6EAEF` / `#FFFFFF` | `#0F172A` / `#1E293B` |
| | `Border` / `BorderStrong` | `#E3E8EE` / `#D4DAE2` | `#334155` / `#475569` |
| | `HeaderBg` | `#F1F5F9` | `#172033` |
| Text | `TextPrimary` / `TextSecondary` | `#1F2937` / `#64748B` | `#E2E8F0` / `#94A3B8` |
| Status | `Danger` / `DangerSoft` / `DangerBorder` | `#DC2626` / `#FEE2E2` / `#FECACA` | `#EF4444` / `#7F1D1D` / `#B91C1C` |
| | `Disabled` / **`HolidayBg`** | `#94A3B8` / **`#D5DAE1`** | `#64748B` / **`#334155`** |
| Sidebar | `SidebarBg` / `NavText` / `NavHover` / `SectionLabel` | `#F8FAFC` / `#475569` / `#EEF2F8` / `#94A0AE` | `#0B1220` / `#CBD5E1` / `#1E293B` / `#64748B` |
| Table | `TableHeaderBg` / `TableHeaderText` / `GroupHeaderBg` / `AltRowBg` / `StatCardBg` | `#F4F6F9` / `#5B6675` / `#EEF2F7` / `#F8FAFC` / `#FBFCFE` | `#172033` / `#94A3B8` / `#1E293B` / `#172033` / `#1E293B` |
| Badge | `BadgeGreenBg/Fg` | `#DCFCE7` / `#15803D` | `#14532D` / `#86EFAC` |
| | `BadgeGrayBg/Fg` | `#EEF1F5` / `#64748B` | `#334155` / `#CBD5E1` |
| Amber | `AmberBg` / `AmberBorder` / `AmberFg` | `#FFF7ED` / `#FDE9C8` / `#B45309` | `#422006` / `#854D0E` / `#FCD34D` |
| | `ResolvedBorder` / `TrackBg` | `#BBF7D0` / `#E8ECF1` | `#166534` / `#334155` |

**Style key** (`Theme.xaml`, 783 dòng): `Card` `StatCard` `StatLabel` `StatValue` `TableContainer` `FeatureTitle` `SectionTitle` `FieldLabel` `FieldHint` `Muted` `SidebarSection` `NavItem` `GhostButton` `MiniGhostButton` `DangerButton` `DangerGhostButton` `ToolbarButton` `ToolbarGhostButton` `ToolbarGhostToggle` `MiniGhostToggle` `TaskIconButton` `CompactComboBox` `CompactDatePicker` `FlatProgressBar` `SoonPill` `FontBase`

> 💡 **Map thẳng sang CSS custom properties** (`--color-accent`, …) + `[data-theme="dark"]`.

---

## B11. CurrentUserService — xác định user

```csharp
ResolveAsync():
   name = Environment.UserName                       // Windows account
   user = UserRepository.GetByWindowsUsernameAsync(name)
   user == null ? NeedsSelection : Resolved
```

**AUTO-PROVISION** (`MainViewModel:265-285`): `NeedsSelection` → tạo user mới với `displayName = Environment.UserName` → `SetWindowsUsernameAsync(newId, winName)` → map.
Sau đó `InitializeActiveTeamAsync`: `config.ActiveTeamId > 0` → `Teams.AddMemberAsync` ⇒ **user mới tự động join active team.**

> ⚠️ `selectUser` delegate **KHÔNG BAO GIỜ ĐƯỢC GỌI** — comment: *"retained as an unused fallback seam"* ⇒ **`SelectUserDialog` là DEAD CODE.**
> 🔴 **KHÔNG có mật khẩu, KHÔNG có session, KHÔNG có role/permission.** Windows identity = danh tính duy nhất. **Mọi user làm được mọi thứ — kể cả xoá team, chạy retention (destructive).**

---

# PHẦN C — SCHEMA

## C1. `PRAGMA user_version` = **9**

| v | Nội dung |
|---|---|
| 1 | Baseline |
| 2 | `Requests` + `start_date, end_date, period_month, status` |
| 3 | Normalize project free-text → enum (`%plus%`→PlusArcs, `%arms%`→ARMS, `%arc%`→ARCS, else Other) |
| 4 | `Requests.assignee_user_id` |
| 5 | Daily Report (step rỗng để bump version) |
| 6 | **RENAME `Requests`→`Backlogs`**, `request_code`→`backlog_code`, `status`→`type`, `RequestAudit`→`BacklogAudit`, `Tasks.request_id`→`backlog_id`, `Tasks.status` NOT NULL DEFAULT 'Todo' |
| 7 | `Backlogs` + `deadline_internal, deadline_external, rough_estimate_hours, official_estimate_hours, progress_percent, note, pca_contact_id` |
| 8 | `Backlogs.team_id`, `StandupEntries.team_id` |
| 9 | `Tasks.type`, `Tasks.assignee_user_id`, `BacklogAudit.note` |

Seed: `EnsureDefaultBacklog` (`DEFAULT`/`DEFAULT`) · `SeedDefaultTasksIfEmpty` (**chỉ khi rỗng**) → `Annual Leave`, `Meeting`, `Other`

## C2. Bảng (16)

```
Users           id · name · windows_username(NULL) · is_active
Teams           id · name · is_active · created_at
UserTeams       (user_id, team_id) PK                 ← N:N, không FK inline
Backlogs        id · backlog_code · project · created_at
                · start_date · end_date · period_month(yyyy-MM) · type
                · assignee_user_id (PCT)              ← không FK
                · deadline_internal · deadline_external
                · rough_estimate_hours REAL · official_estimate_hours REAL
                · progress_percent INT · note
                · pca_contact_id · team_id            ← không FK
                ❗ KHÔNG có is_active — Backlog KHÔNG soft-delete được
BacklogAudit    id · backlog_id FK→Backlogs (NOT NULL, RESTRICT) · field
                · old_value · new_value · changed_by_user_id · changed_by_name
                · changed_at · note (v9: lý do đổi deadline)
Tasks           id · backlog_id FK · task_name · order_index
                · is_active · status(DEFAULT 'Todo') · type · assignee_user_id
TaskAudit       id · task_id · field · old/new_value · changed_by_* · changed_at
TimeLogs        id · user_id FK · task_id FK · work_date · hours REAL · created_at
                ❗ UNIQUE(user_id, task_id, work_date)   ← natural key cho upsert
TaskTemplates   id · template_name · task_name · order_index
DefaultTasks    id · task_name · order_index · is_active
Settings        key PK · value                        ← KV shared trong DB
StandupEntries  id · user_id FK · work_date · section · backlog_id(NULL)
                · backlog_code · task_text · description · deadline · status
                · order_index · created_at · team_id
                IDX ix_standup_user_date, ix_standup_date
StandupIssues   id · entry_id FK ON DELETE CASCADE
                · issue_text · solution_text(NULL) · status(DEFAULT 'open')
                · order_index · created_at
Tags            id · text · icon · color · created_at  ← HARD delete
BacklogTags     (backlog_id, tag_id) PK               ← N:N
TaskTags        (task_id, tag_id) PK                  ← N:N
PcaContacts     id · name · is_active                 ← soft delete
Holidays        holiday_date PK (yyyy-MM-dd) · description
```

**❗ KHÔNG có cột `version` / `rowversion` / `updated_at` ở BẤT KỲ bảng nào** ⇒ **không có optimistic concurrency** ⇒ **lost update âm thầm.**

### Enum (hardcode C#, không phải table)

| Enum | Giá trị |
|---|---|
| `BacklogProjects` | `ARCS, PlusArcs, ARMS, Other` |
| `BacklogType` | `Continue, Implement, Investigate, IT, Estimate` |
| `TaskStatus` / `StandupStatus` | `Todo, In-process, Done, Pending` |
| `StandupIssueStatus` | `open, pending, resolved` |
| `StandupSection` | `yesterday, today` |
| `ScheduleState` | `Normal, Warning, Late` |

### Quy ước lưu trữ
- **Ngày/giờ = TEXT ISO**: `work_date`/`deadline` = `yyyy-MM-dd`; `created_at`/`changed_at` = `yyyy-MM-ddTHH:mm:ssZ` (UTC)
- **`hours` = REAL** — bind `double`, narrow về `decimal` ở boundary (XC-01)
- **`period_month` = `yyyy-MM`** — so sánh lexical (retention dựa vào đây)
- Settings key: `chua_log_n_days` (3) · `retention.pruned_through` · `entry.collapseAll`

### 🔴 Connection policy — CỰC KỲ QUAN TRỌNG
```csharp
// SqliteConnectionFactory.cs
Mode = ReadWriteCreate;  Pooling = FALSE;  ForeignKeys = true;
PRAGMA journal_mode = DELETE;   // KHÔNG WAL — tránh -wal/-shm sync lệch qua OneDrive
```
> ⇒ **Toàn bộ kiến trúc data hiện tại được thiết kế quanh việc DB SQLite nằm trên OneDrive shared folder.**
> Lên server: **XC-08 / XC-09 / XC-10 / Pooling=false / journal_mode=DELETE đều thành rác — xoá hết. Bật WAL + pooling.**

---

# PHẦN D — PHỤ THUỘC WPF/WINDOWS (không port thẳng)

## D1. File system

| Chỗ | Web |
|---|---|
| `IAppConfig` → `%APPDATA%\...\appsettings.json` | DB `UserPreferences` table hoặc localStorage |
| DB path `%USERPROFILE%\Documents\...\timesheet.db` | connection string |
| `SqliteMaintenance.FindConflictCopies` (XC-08) | OneDrive-specific → **bỏ hẳn** |
| `SqliteMaintenance.IsJournalGone` (XC-09) | → **bỏ hẳn** |
| `DbBackupHelper` / `BackupService` (`File.Copy`) | server-side backup job |
| `ExportHubService` / `PruneArchiver` (`File.WriteAllText`) | blob storage / download endpoint |
| `SharePointDestinationValidator` (`DriveInfo`, UNC, write-probe) | **Microsoft Graph API** thay mapped drive |
| `PathSanitizer` (`Path.GetInvalidFileNameChars()`) | Windows-specific |
| `OpenFileDialog`/`OpenFolderDialog`/`SaveFileDialog` | `<input type=file>` / download link |

## D2. Windows identity
`Environment.UserName` là **danh tính DUY NHẤT**. KHÔNG password, KHÔNG session, KHÔNG role/permission.
→ **Web BẮT BUỘC có auth layer mới.**

## D3. Dead code / seam chưa dùng

| Item | Trạng thái |
|---|---|
| **`SelectUserDialog`** | Wire ở `App.xaml.cs:222-229` nhưng `selectUser` **không bao giờ được gọi** ⇒ **UI này không bao giờ xuất hiện** |
| **`ISmartInputService`** | Đăng ký DI + inject `TimesheetViewModel:38` nhưng **không gán field, không dùng**. Math thật ở `SmartInputPanelVm.BuildPlan` |
| `TimesheetViewModel.SaveCommand` | Không có nút Save trên UI (auto-save thay thế) |
| `TagPicker.TagsCommitted` | Chỉ dùng ở Backlog editor |

## D4. In-process messaging
`WeakReferenceMessenger` — synchronous, in-process, không persist. Chỉ sync trong 1 process.
→ **Web: SignalR** (real-time cross-user) hoặc refetch/invalidate.

## D5. ResourceDictionary theming → **CSS custom properties + `[data-theme]`**, palette map 1:1 (§B10)

## D6. Modal dialog blocking
`dialog.ShowDialog()` **chặn thread** rồi đọc property.
→ Web: async Promise/Observable. Mọi flow `if (dlg.ShowDialog()==true) { … }` phải viết lại thành async.

## D7. Drag & drop
`DragDrop.DoDragDrop` + custom format (`"timesheetTask"`, `"standupEntry"`) + trash zone.
→ Web: **CDK DragDrop** (Angular). Behavior giữ nguyên được.

## D8. Gantt vẽ tay trên `Canvas` → SVG. **Logic index math PORT 1:1** (không có pixel).

## D9. Hack WPF cần bỏ

| Hack | Lý do tồn tại |
|---|---|
| `BindingProxy : Freezable` | `DataGridColumn` ngoài visual tree |
| `TeamFilterViewModel` lazy-seed | raise event trong `InitializeAsync` → **stack overflow** WPF Measure |
| `CurrentTeamService` không raise `ActiveTeamChanged` trong Initialize | như trên |
| Task List DatePicker/ComboBox **OneWay + commit từ code-behind** | *"CellTemplate write-back bug"* trong DataGrid |
| `TagSelectDialog` thay `TagPicker` popup ở Task List | in-grid Popup đóng trước khi tick được |
| **9 re-entrancy guard flags** (`_suppressCommit`, `_suppressSelfReload`, `_suppressDeadlineChange`, `_suppressReentry`, `_suppressTotals`, `_loadingTheme`, `_suppressProgressCommit`, `_suppressChange`, `_autoLoad`) | hệ quả của two-way binding + messenger đồng bộ |
| Scrollbar cưỡng bức `Visible` + margin 25px | căn cột với header/footer |

## D10. Chỗ khác cần chú ý
- **N+1 query**: `TaskListViewModel:267` — `GetTagIdsAsync(t.Id)` trong vòng lặp → web nên batch
- **Filter in-memory**: Backlog tab load **toàn bộ** rồi filter client → server-side filter + paging
- **Không có paging ở bất kỳ đâu**
- **`TimeLogService.LastNWorkingDays` KHÔNG trừ holiday** (khác `WorkingDayCalculator`) — **inconsistency**
- **Culture lẫn lộn**: string tiếng Việt lẫn tiếng Anh (*"Đã continue…"*, *"Chưa có task nào…"*, *"Chọn tag"*, *"Xong"*) → web nên **i18n** đàng hoàng
- Không có soft-delete cho Backlog — chỉ retention mới xoá được (cứng)

---

# PHẦN E — CHECKLIST CHO SPEC WEB

| Domain | Endpoint gợi ý | Ghi chú |
|---|---|---|
| Auth | Windows Auth (Negotiate) qua IIS | **MỚI HOÀN TOÀN** |
| Users | `GET/POST /users` · `PATCH /users/{id}/deactivate` | soft-delete |
| Teams | CRUD + `PUT /teams/{id}/members` (replace-all) | tạo team → auto DEFAULT backlog + sync default tasks |
| Backlogs | `GET /backlogs?teamIds&search&project&type&assignee&month` · `POST` (≥1 task) · `PUT` (giữ teamId) · `GET /{id}/audit` | **không có DELETE** |
| Tasks | nested · soft-delete · reorder · `PATCH type/assignee/status/tags` (audit) | |
| TimeLogs | `PUT /timelogs` (upsert natural key) · `DELETE` · `POST /timelogs/smart-fill/preview` + `/apply` | validation §B2 |
| Standup | `GET /standup?date&teamIds` · CRUD entries/issues · `POST /quick-import` · `POST /reorder` | **edit-lock today+yesterday**, owner-gate entry, issue collaborative |
| TaskList | `GET /tasklist?year&month&teamIds` → rows + `ScheduleState` + Gantt model · `POST /backlogs/{id}/continue` | |
| Reports | `GET /reports/weekly\|monthly\|tree\|missing-logs` · `GET /reports/export.xlsx` | |
| Settings | tags · pca-contacts · templates · holidays · warning-days · theme | |
| Ops | export · backup · retention (preview/run) | **cân nhắc admin-only — hiện ai cũng chạy được** |

## Requirement mới cần quyết định

1. **Authentication + Authorization/role** — hiện **KHÔNG có gì**
2. **Optimistic concurrency** (`row_version` + 409 Conflict) — hiện **lost update âm thầm**
3. **Real-time sync** (SignalR) hay refetch-on-action?
4. **Export** → download endpoint hay vẫn ghi ra SharePoint (qua Graph API)?
5. **Retention/Backup** → giữ ở app hay chuyển sang DB ops?
6. **Sửa 2 bug đã phát hiện:** `DAYS LOGGED` mẫu số (§A6) · holiday trong Smart Fill preview (§B1)
7. **Hợp nhất** `SmartInputService` vs `SmartInputPanelVm.BuildPlan` về 1 nguồn
