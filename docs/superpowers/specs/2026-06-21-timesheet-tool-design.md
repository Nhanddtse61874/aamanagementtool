# Design Spec — WPF Desktop Timesheet Tool

**Date:** 2026-06-21
**Status:** Approved (brainstorm consolidated)
**Source:** Original approved spec `2026-06-19-timesheet-tool-design.md` (from images) + 4 supplements resolved during brainstorm 2026-06-21.

> This document merges the original 2026-06-19 approved design with the gap-fill decisions made during the 2026-06-21 brainstorm (user identity, TimeLogs/DefaultTasks schema, concurrency model, Main Window). Sections inherited verbatim are marked _(spec)_; new/resolved content is marked _(brainstorm 2026-06-21)_.

---

## 1. Overview _(spec)_

Internal desktop tool thay thế Excel-based timesheet cho team nhỏ (2–5 người). Dữ liệu lưu vào **SQLite file** chia sẻ qua OneDrive/Teams. Output cuối là **Markdown** file làm data thô cho AI collect logs sau này.

---

## 2. Architecture _(spec)_

```
SQLite (.db — shared via OneDrive/Teams)
   ↑↓
Repository Layer   (C# + Dapper)
   ↑↓
Service Layer      (Business Logic, Validation)
   ↑↓
ViewModel Layer    (MVVM — INotifyPropertyChanged)
   ↑↓
WPF UI             (MainWindow + TabControl)
```

**Tech stack:**
- Language: **C# / .NET 8**
- UI: **WPF (MVVM pattern)**
- Database: **SQLite via Dapper**
- Export Excel: **ClosedXML**
- Export Markdown: custom string builder

---

## 3. Database Schema

### 3.1 Tables inherited verbatim _(spec)_

```sql
-- Người dùng
CREATE TABLE Users (
    id        INTEGER PRIMARY KEY AUTOINCREMENT,
    name      TEXT NOT NULL,
    is_active INTEGER NOT NULL DEFAULT 1  -- soft delete
);

-- Request (đầu việc nhận từ PCA/stakeholder)
CREATE TABLE Requests (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    request_code TEXT NOT NULL,
    project      TEXT NOT NULL,
    created_at   TEXT NOT NULL
);

-- Task thuộc Request
CREATE TABLE Tasks (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    request_id  INTEGER NOT NULL REFERENCES Requests(id),
    task_name   TEXT NOT NULL,
    order_index INTEGER NOT NULL DEFAULT 0,
    is_active   INTEGER NOT NULL DEFAULT 1  -- soft delete
);

-- Template sinh task tự động
CREATE TABLE TaskTemplates (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    template_name TEXT NOT NULL,
    task_name     TEXT NOT NULL,
    order_index   INTEGER NOT NULL DEFAULT 0
);
```

### 3.2 Tables resolved during brainstorm _(brainstorm 2026-06-21)_

```sql
-- Users: thêm cột map theo Windows username
-- (Users ở 3.1 được mở rộng thành:)
CREATE TABLE Users (
    id               INTEGER PRIMARY KEY AUTOINCREMENT,
    name             TEXT NOT NULL,
    windows_username TEXT,                    -- map Environment.UserName; NULL = chưa map
    is_active        INTEGER NOT NULL DEFAULT 1
);

-- TimeLogs (bảng cốt lõi — spec gốc bị cắt; đề xuất & duyệt 2026-06-21)
CREATE TABLE TimeLogs (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id    INTEGER NOT NULL REFERENCES Users(id),
    task_id    INTEGER NOT NULL REFERENCES Tasks(id),   -- 1 FK duy nhất
    work_date  TEXT NOT NULL,                            -- 'YYYY-MM-DD'
    hours      REAL NOT NULL,                            -- > 0, tối đa 1 chữ số thập phân
    created_at TEXT NOT NULL,
    UNIQUE(user_id, task_id, work_date)                  -- 1 ô = 1 log; inline edit = upsert
);

-- DefaultTasks: giữ làm "mẫu seed" (Annual Leave, Meeting, Other...)
CREATE TABLE DefaultTasks (
    id          INTEGER PRIMARY KEY AUTOINCREMENT,
    task_name   TEXT NOT NULL,
    order_index INTEGER NOT NULL DEFAULT 0,
    is_active   INTEGER NOT NULL DEFAULT 1
);

-- Settings (key-value cho Database path, N ngày cảnh báo...)
CREATE TABLE Settings (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
```

> **Note:** `Settings` table được suy ra từ tab Settings (DB path, N ngày cảnh báo) — lưu dưới dạng key-value. Một số setting cục bộ theo máy (vd current-user mapping) có thể lưu ở app config thay vì DB chia sẻ.

### 3.3 DefaultTask ↔ Tasks unification _(brainstorm 2026-06-21)_

- Khi init DB: tạo sẵn **1 Request ẩn** `request_code = 'DEFAULT'`, `project = 'DEFAULT'`.
- Mỗi dòng `DefaultTasks` (active) được seed thành 1 `Task` thuộc Request `DEFAULT`.
- → Mọi `TimeLog` (việc thường lẫn Annual Leave/Meeting) chỉ trỏ vào `Tasks.id` — **1 FK duy nhất**, query gọn.
- Tab Reports / Export phân biệt nhóm "DEFAULT" bằng `request_code = 'DEFAULT'` → khớp output `### DEFAULT — Annual Leave`.
- Tab Settings sửa `DefaultTasks` → đồng bộ (thêm/ẩn/đổi tên) sang các Task thuộc Request `DEFAULT`.

---

## 4. Concurrency Model _(brainstorm 2026-06-21)_

Shared SQLite qua OneDrive/Teams, **real-time sync nằm ngoài scope v1**. Áp dụng giả định:

- **Single-writer / last-write-wins**: mỗi người nhập phần của mình.
- App mở connection **ngắn** (open → thao tác → close), không giữ khoá file lâu, để OneDrive sync kịp.
- Nếu OneDrive báo file conflict → người dùng tự xử ở mức file. Đây là **rủi ro đã chấp nhận** theo spec.

---

## 5. UI / Tabs

### 5.1 Main Window _(brainstorm 2026-06-21)_

- `MainWindow.xaml` = `TabControl` 5 tab: **Timesheet · Requests · Users · Reports · Settings**.
- **Current user** xác định theo **Windows username**: khi mở app, map `Environment.UserName` → `Users.windows_username`.
  - Có match → set current user, hiển thị tên ở góc trên.
  - Không match → mở `SelectUserDialog` cho chọn 1 lần → lưu mapping vào `Users.windows_username`.

### 5.2 Tab: Timesheet _(spec)_

**Bảng nhập liệu tuần:**
- Hiển thị theo tuần (Mon–Fri), navigation Prev/Next week.
- Rows = Tasks (Default Tasks + Tasks từ active Requests).
- Columns = Mon, Tue, Wed, Thu, Fri.
- Inline edit trực tiếp từng ô (DataGrid editable).
- Mỗi ô hiển thị số giờ, rỗng = 0.
- Tổng cột (tổng giờ/ngày) hiển thị ở footer mỗi cột.

**Validation:**
- Tổng giờ/ngày/user **không được vượt 8h** → hiện warning đỏ, **không save**.
- Không cho phép nhập vào T7, CN (ẩn hoặc disable columns đó).
- Giờ phải là số dương, tối đa 1 chữ số thập phân (0.5, 1.0, 2.5...).

**Smart Input Panel** (panel phụ dưới bảng, 2 mode):

- **Mode 1 — Chia đều:**
  ```
  Task: [dropdown]
  Từ ngày: [DatePicker] → Đến ngày: [DatePicker]
  Tổng giờ: [input]
  [Chia đều]
  ```
  Logic: đếm số ngày làm việc trong range (bỏ T7/CN) → chia đều tổng giờ cho số ngày → phần dư dồn vào ngày cuối cùng.
  Ví dụ: `10h / 3 ngày → 3.3h, 3.3h, 3.4h`.

- **Mode 2 — Full 8h:**
  ```
  Task: [dropdown]
  Từ ngày: [DatePicker] → Đến ngày: [DatePicker]
  [Full 8h]
  ```
  Logic: điền 8h vào tất cả ngày làm việc trong range (bỏ T7/CN).

Sau khi apply smart input → **preview** trước khi save.

### 5.3 Tab: Requests _(spec)_

- Danh sách requests có search theo `request_code` hoặc project name.
- **Tạo request:**
  1. Nhập `request_code` + project name.
  2. Chọn template (optional) → auto sinh danh sách tasks.
  3. Custom thêm/bớt/sắp xếp task.
  4. Save.
- **Edit request:** sửa tên, thêm task, soft delete task.
- **Soft delete task:** ẩn khỏi bảng Timesheet, logs vẫn giữ nguyên.

### 5.4 Tab: Users _(spec)_

- Danh sách users: tên + trạng thái Active/Inactive.
- Thêm user: nhập tên → save.
- Soft delete: set `is_active = 0` → ẩn khỏi dropdown chọn user, giữ toàn bộ TimeLogs.

### 5.5 Tab: Reports _(spec)_

- **View tuần:** chọn user + tuần → bảng tổng giờ theo ngày.
- **View tháng:** chọn user + tháng → bảng tổng giờ theo request/task.
- **Drill-down:** Project → Request → Task → Date (giờ chi tiết).
- **Cảnh báo chưa log:**
  - Quét toàn bộ active users.
  - Nếu user chưa có bất kỳ log nào trong N ngày làm việc gần nhất (N config ở Settings).
  - Hiện banner warning: `"[Tên] chưa log trong x ngày"`.

### 5.6 Tab: Settings _(spec)_

| Setting | Mô tả |
|---|---|
| Database path | Đường dẫn file `.db` (có nút Browse) |
| Cảnh báo chưa log | Số ngày N (default: 3) |
| TaskTemplates | Thêm/sửa/xóa template và danh sách tasks |
| DefaultTasks | Thêm/sửa/xóa Annual Leave, Meeting, Other... |

---

## 6. Export _(spec)_

### 6.1 Export Excel
- Format tương thích file Excel cũ (nộp cho PCA/stakeholder).
- Filter theo: user, tháng, project.
- Dùng **ClosedXML**.

### 6.2 Export Markdown
- Output data thô cho AI collect logs sau này.
- Format:

```markdown
# Timesheet — 2026/06

## Nguyen Van A

### REQ-001 — ProjectX
| Date       | Task      | Hours |
|------------|-----------|-------|
| 2026-06-16 | Implement | 4     |
| 2026-06-16 | Review    | 4     |
| 2026-06-17 | Testing   | 3     |

### DEFAULT — Annual Leave
| Date       | Task         | Hours |
|------------|--------------|-------|
| 2026-06-20 | Annual Leave | 8     |

## Tran Thi B
...
```

---

## 7. Business Rules _(spec)_

| Rule | Chi tiết |
|---|---|
| Max giờ/ngày | 8h/user/ngày — hard limit, không save nếu vượt |
| Ngày hợp lệ | Chỉ Mon–Fri — T7/CN không được nhập |
| Soft delete | User và Task chỉ ẩn khỏi UI, không xóa data |
| Smart input rounding | Phần dư (modulo) dồn vào ngày cuối trong range |
| Cảnh báo log | Chỉ tính ngày làm việc (bỏ T7/CN) khi đếm N ngày |

---

## 8. Project Structure (đề xuất) _(spec)_

```
TimesheetApp/
├── Data/
│   ├── Database.cs              -- SQLite connection, init schema
│   └── Repositories/
│       ├── UserRepository.cs
│       ├── RequestRepository.cs
│       ├── TaskRepository.cs
│       ├── TimeLogRepository.cs
│       └── SettingsRepository.cs
├── Services/
│   ├── TimeLogService.cs        -- business logic, validation
│   ├── SmartInputService.cs     -- chia giờ tự động
│   └── ExportService.cs         -- Excel + Markdown export
├── ViewModels/
│   ├── MainViewModel.cs
│   ├── TimesheetViewModel.cs
│   ├── RequestsViewModel.cs
│   ├── UsersViewModel.cs
│   ├── ReportsViewModel.cs
│   └── SettingsViewModel.cs
├── Views/
│   ├── MainWindow.xaml
│   ├── Tabs/
│   │   ├── TimesheetTab.xaml
│   │   ├── RequestsTab.xaml
│   │   ├── UsersTab.xaml
│   │   ├── ReportsTab.xaml
│   │   └── SettingsTab.xaml
│   └── Dialogs/
│       └── SelectUserDialog.xaml
└── Models/
    ├── User.cs
    ├── Request.cs
    ├── Task.cs
    ├── TimeLog.cs
    └── TaskTemplate.cs
```

---

## 9. Out of Scope (v1) _(spec)_

- Authentication / login có password.
- Multi-tenant / cloud database.
- Mobile / cross-platform (Mac).
- Real-time sync (concurrent write conflict resolution).
- Notification system (email/Teams alert).

---

## Appendix — Brainstorm decisions (2026-06-21)

1. **Project identity:** Folder `AgentArchitectureManagement` là container; app build bên trong là `TimesheetApp` đúng theo spec.
2. **User identity:** map theo Windows username (`Environment.UserName`) → cột `Users.windows_username`; fallback `SelectUserDialog` 1 lần.
3. **TimeLogs:** 1 FK `task_id` duy nhất; DefaultTasks gộp vào `Tasks` qua Request ẩn `DEFAULT`.
4. **Concurrency:** single-writer / last-write-wins, connection ngắn; conflict ở mức file là rủi ro chấp nhận.

## Appendix — Resolved decisions after research (2026-06-21, STEP 4)

**Tech (hội tụ từ research):**
- CommunityToolkit.Mvvm 8.4.2 · Dapper 2.1.79 · Microsoft.Data.Sqlite **8.0.x** (pin 8.x) · ClosedXML 0.105.0 · DI = Microsoft.Extensions.DependencyInjection (bare ServiceCollection in App.xaml.cs).
- **`PRAGMA journal_mode=DELETE`** (KHÔNG WAL) — tránh corruption sidecar trên OneDrive. Kèm: connection ngắn, `Pooling=False`, `Foreign Keys=True`, single-writer, advisory edit-lock, phát hiện conflict-copy lúc startup.
- Migrations qua `PRAGMA user_version` (forward-only/additive).

**Policy/scope (user quyết 2026-06-21):**
1. Markdown DEFAULT header: **nhóm theo tên task** → `### DEFAULT — Annual Leave` (khớp ví dụ spec).
2. Smart Input vào ô đã có giờ: **ghi đè** (upsert), preview + validate tổng ngày ≤ 8h sau merge.
3. Cảnh báo "chưa log N ngày": **tính cả hôm nay**.
4. Request trong v1: **không soft-delete** (chỉ Task soft-delete được).
5. 8h validation: enforce **per-cell (đỏ ngay) + per-save (tổng ngày)**.
6. Banner hiển thị số **N** cấu hình (không tính gap thực).
7. Rename DefaultTask: coi như **soft-delete + insert mới** (giữ TimeLogs), flag UAT.
8. Lưu giờ kiểu **REAL**, round 1 chữ số thập phân trước khi upsert.
