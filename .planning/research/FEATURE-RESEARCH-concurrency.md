# FEATURE RESEARCH — Optimistic concurrency với Dapper + SQLite

**Phase:** M8.2 (API host, DB, concurrency)
**Agent:** Feature Research (STEP 4, Mode B)
**Date:** 2026-07-12
**Spec under review:** `docs/superpowers/specs/2026-07-12-m8-backend-foundation-design.md` §6.1, §6.1.1, §5.2, §7.1, §9
**Verdict:** Cơ chế cốt lõi của spec (**`row_version` + `rowsAffected == 0` → 409**) là **đúng** và được Microsoft chính thức khuyến nghị. Nhưng **SQL mà spec gợi ý cho `TimeLogs` có một lỗ hổng ghi đè âm thầm**, và **`busy_timeout = 5000` ở §5.2 không hề giới hạn thời gian chờ như spec tưởng**. 6 lỗ hổng — 2 nghiêm trọng.

---

## 0. Thế nào là `[VERIFIED]` trong tài liệu này

Tôi **không** chỉ đọc docs. Tôi dựng một probe .NET 8 dùng **đúng phiên bản package của dự án** (`Dapper 2.1.79`, `Microsoft.Data.Sqlite 8.0.10` — xem `src/TimesheetApp/TimesheetApp.csproj`), tái tạo schema `TimeLogs` thật (kèm `UNIQUE(user_id, task_id, work_date)`), và chạy mọi tình huống.

- Probe source: `<scratchpad>/ConcProbe/{Program.cs, Probe2.cs, Probe3.cs}`
- Output thô: §9

| Tag | Nghĩa |
|---|---|
| `[VERIFIED]` | Tôi đã chạy và quan sát trực tiếp trên đúng stack của dự án |
| `[CITED]` | Có URL tài liệu chính thức |
| `[ASSUMED]` | Suy đoán — chưa kiểm chứng |

**Môi trường đo được** `[VERIFIED]`:

| | |
|---|---|
| `sqlite_version()` bundled trong Microsoft.Data.Sqlite 8.0.10 | **3.41.2** |
| `RETURNING` (cần ≥ 3.35.0) | ✅ có |
| `ON CONFLICT … DO UPDATE … WHERE` (cần ≥ 3.24.0) | ✅ có |
| `SqliteCommand.CommandTimeout` mặc định | **30 giây** |

---

## 1. Tóm tắt điều hành — 6 lỗ hổng

| # | Mức | Lỗ hổng | Ở đâu |
|---|---|---|---|
| **G1** | 🔴 **CRITICAL** | Bảng 4 trường hợp ở §6.1.1 **thiếu trường hợp thứ 5**: `expectedVersion = N` + **row đã bị xoá**. SQL kiểu naive sẽ **INSERT lại (hồi sinh) row, version = 1, rowsAffected = 1 → HTTP 200**. Người dùng B ghi đè lên thao tác *xoá* của người dùng A mà không ai biết. Đây **chính xác** là lớp bug mà cả slice này sinh ra để diệt. | spec §6.1.1 |
| **G2** | 🔴 **CRITICAL** | Carve-out của Smart Fill nói writes của nó "not version-checked". Nếu implementer hiểu thành "không **tăng** `row_version`", thì mọi client đang giữ version cũ **vẫn thấy version của mình hợp lệ** → lần ghi kế tiếp của họ **ghi đè im lặng** kết quả Smart Fill. Carve-out tự tay chọc thủng cơ chế. Luật đúng: **luôn BUMP, chỉ CHECK có chọn lọc.** | spec §6.1.1 |
| **G3** | 🟠 **HIGH** | `busy_timeout = 5000` ở §5.2 **không** giới hạn thời gian chờ. Microsoft.Data.Sqlite **tự retry** busy/locked cho tới `CommandTimeout` (**mặc định 30s**). Đo thực tế: writer bị chặn fail sau **~34 giây**, không phải 5. Một HTTP request treo 34s. | spec §5.2 |
| **G4** | 🟡 MEDIUM | `rowsAffected == 0` **gộp chung** "version cũ" và "row đã bị xoá". Spec hứa 409 kèm "current server-side state" — nhưng **không có state nào** nếu row đã biến mất. `Backlogs` **không có soft-delete** (inventory §C2) nên row *thật sự* bốc hơi được. | spec §6.1, §7.1 |
| **G5** | 🟡 MEDIUM | `BacklogRepository.UpdateAsync` hiện làm: SELECT `before` → UPDATE → N× INSERT `BacklogAudit`, **không có transaction**. Gắn `AND row_version = @expected` vào UPDATE mà không bọc tx → khi 409, **audit rows vẫn được ghi**, mô tả một thay đổi chưa từng xảy ra. | `BacklogRepository.cs:140-192` |
| **G6** | 🟡 MEDIUM | Xoá ô (clear cell) = `DELETE`, nhưng spec **không nói `expectedVersion` đi đường nào**. DELETE-with-body bị RFC 9110 §9.3.5 khuyến cáo ("no generally defined semantics"). | spec §6.1.1 |

**Điều spec làm ĐÚNG** (và tôi đã cố chứng minh là sai nhưng không được):
- `rowsAffected == 0 → conflict` là pattern **Microsoft tự document** (Optimistic Offline Lock) `[CITED]`.
- Chọn **409 + `row_version` trong body** thay vì **412 + ETag/If-Match** là **đúng** cho API này — nhưng vì một lý do spec **không hề nêu** (xem §6).

---

## 2. Q1 — `row_version` với Dapper + SQLite

### 2.1 Dapper trả `rowsAffected` thế nào

`Execute()` / `ExecuteAsync()` gọi `DbCommand.ExecuteNonQuery()`, mà Microsoft.Data.Sqlite hiện thực **chính là `sqlite3_changes()`**. `[VERIFIED]`

| Tình huống | `Execute()` | `ExecuteAsync()` |
|---|---|---|
| version khớp | **1** | **1** |
| version cũ (stale) | **0** | **0** |
| row không tồn tại | **0** | **0** |

`Execute()` và `ExecuteAsync()` **giống hệt nhau** về giá trị trả về `[VERIFIED]`. Không có bẫy async nào ở đây.

> ⚠️ Dapper 2.1.79 **không** document `<returns>` cho `ExecuteAsync` trong XML docs (`Dapper.xml` chỉ có `<summary>`). Nên đây là kiến thức được xác lập bằng đo đạc, không bằng đọc doc.

### 2.2 Có cần `changes()` không? — **KHÔNG**

`SELECT changes()` ngay sau một UPDATE trả **đúng cùng số** với `ExecuteNonQuery()` `[VERIFIED]`. Gọi thêm `changes()` là **một round-trip thừa** và còn **nguy hiểm**: `changes()` gắn với *connection*, nên nếu connection factory trả connection từ pool cho request khác xen giữa, bạn đọc nhầm số. **Dùng thẳng giá trị trả về của Dapper.**

### 2.3 SQL cho update thường (Backlogs, Tasks, StandupIssues, Users, Teams, Tags, PcaContacts)

Spec §6.1 viết đúng, nhưng **nên thêm `RETURNING`** để trả version mới về client trong **một** statement:

```sql
UPDATE Backlogs
   SET backlog_code = @BacklogCode, /* … */,
       row_version  = row_version + 1
 WHERE id = @Id AND row_version = @Expected
RETURNING row_version;
```

```csharp
var newVersion = await c.QueryFirstOrDefaultAsync<long?>(Sql, new { ..., Expected = expectedVersion });
if (newVersion is null) throw new ConcurrencyConflictException("backlog", id);
return newVersion.Value;   // -> API echo về client
```

`row_version` là `NOT NULL`, nên `null` **chỉ có thể** nghĩa là "không có row nào bị đụng" ⇒ conflict. Không mơ hồ. `[VERIFIED]`

### 2.4 Xác nhận từ Microsoft

Trang *Transactions — Microsoft.Data.Sqlite* có hẳn một ví dụ tên **"Optimistic Offline Lock pattern"**, dùng đúng cơ chế của spec `[CITED]`:

```csharp
// UPDATE data SET value = 2, version = $expectedVersion + 1
//  WHERE id = 1 AND version = $expectedVersion
var recordsAffected = updateCommand.ExecuteNonQuery();
if (recordsAffected == 0)
{
    // Concurrent update detected! Rollback savepoint and retry
```
→ <https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/transactions>

Spec §6.1 **không phải là sáng tạo mạo hiểm** — nó là pattern được vendor khuyến nghị.

---

## 3. Q2 — Concurrency trên UPSERT (§6.1.1). **ĐÂY LÀ CHỖ SPEC SAI.**

### 3.1 SQLite **có** hỗ trợ `WHERE` trong `DO UPDATE` — đã xác minh

> "The only use for the WHERE clause at the end of the DO UPDATE is to optionally change the DO UPDATE into a no-op depending on the original and/or new values." `[CITED]` — <https://www.sqlite.org/lang_upsert.html>

Và khi WHERE = false thì **no-op, KHÔNG phải lỗi** `[CITED]` `[VERIFIED]`. Có từ SQLite **3.24.0**; ta đang có **3.41.2**. Gợi ý trong đề bài là đúng.

### 3.2 Nhưng cách viết naive **thủng ở trường hợp thứ 5**

SQL naive (đúng như spec ngụ ý):

```sql
-- ❌ FORM 1 — KHÔNG DÙNG
INSERT INTO TimeLogs(user_id, task_id, work_date, hours, created_at, row_version)
VALUES(@u, @t, @d, @h, @c, 1)
ON CONFLICT(user_id, task_id, work_date) DO UPDATE
   SET hours = excluded.hours, row_version = TimeLogs.row_version + 1
 WHERE TimeLogs.row_version = @expected;
```

Kết quả đo `[VERIFIED]`:

| Trường hợp | rowsAffected | DB sau đó | Đúng? |
|---|---|---|---|
| `expected=NULL`, row **vắng** | 1 | v=1, hours=4 | ✅ INSERT |
| `expected=NULL`, row **có** | 0 | không đổi | ✅ 409 |
| `expected=3`, row **v=3** | 1 | v=4, hours=4 | ✅ UPDATE |
| `expected=3`, row **v=7** | 0 | không đổi | ✅ 409 |
| **`expected=3`, row ĐÃ BỊ XOÁ** | **1** | **v=1, hours=4 — HỒI SINH** | 🔴 **SAI — phải là 409** |

**Tại sao thủng:** không có row ⇒ **không có conflict** ⇒ mệnh đề `DO UPDATE … WHERE` **không bao giờ chạy** ⇒ `INSERT` thuần thành công. Cái `WHERE` chỉ bảo vệ nhánh UPDATE; nó **không bảo vệ nhánh INSERT**.

**Kịch bản thật:** Alice xoá ô Thứ Ba của mình (hoặc admin/manager sửa timesheet người khác — chính là lý do spec quyết định *thêm* `row_version` vào `TimeLogs`). Bob đang mở lưới với version 3, gõ `6.5`, nhấn tab. Row hồi sinh. Thao tác xoá của Alice bốc hơi, **không có 409, không ai được báo**. Đây đúng là "âm thầm ghi đè dữ liệu người khác".

> Trả lời trực tiếp câu hỏi trong đề bài — *"`INSERT … ON CONFLICT DO UPDATE WHERE <false>` trả rowsAffected = 0 — có phân biệt được 'conflict version' với 'không làm gì' không?"*: **Với FORM 1 thì KHÔNG cần phân biệt — vấn đề ngược lại: nó trả về `1` ở đúng cái case mà lẽ ra phải là conflict.** `rowsAffected = 0` luôn luôn = conflict (an toàn); nhưng `rowsAffected = 1` **không** luôn = hợp lệ.

### 3.3 ✅ SQL đúng — chặn ngay ở nhánh INSERT

Ý tưởng: **biến chính cái `INSERT` thành có điều kiện.** Dùng `INSERT … SELECT … WHERE` thay vì `INSERT … VALUES`, để hàng nguồn **không tồn tại** trừ khi niềm tin của client khớp thực tế.

```sql
-- ✅ FORM 2R — DÙNG CÁI NÀY
INSERT INTO TimeLogs (user_id, task_id, work_date, hours, created_at, row_version)
SELECT @UserId, @TaskId, @WorkDate, @Hours, @CreatedAt, 1
 WHERE @Expected IS NULL
    OR EXISTS (SELECT 1 FROM TimeLogs
                WHERE user_id = @UserId AND task_id = @TaskId AND work_date = @WorkDate
                  AND row_version = @Expected)
    ON CONFLICT(user_id, task_id, work_date) DO UPDATE
   SET hours       = excluded.hours,
       row_version = TimeLogs.row_version + 1
 WHERE TimeLogs.row_version = @Expected
RETURNING row_version;
```

Nó chạy đúng cả **5** trường hợp `[VERIFIED]`:

| Client gửi | Row trên server | `SELECT` sinh ra | Đường đi | rowsAffected | `RETURNING` | Kết quả |
|---|---|---|---|---|---|---|
| `NULL` | vắng | 1 row (`@Expected IS NULL`) | INSERT | **1** | `1` | ✅ INSERT, v=1 |
| `NULL` | có (v=1) | 1 row | CONFLICT → `WHERE row_version = NULL` → NULL → no-op | **0** | *(rỗng)* | ✅ **409** |
| `3` | có (v=3) | 1 row (`EXISTS` true) | CONFLICT → `WHERE 3 = 3` → UPDATE | **1** | `4` | ✅ UPDATE, v=4 |
| `3` | có (v=7) | **0 row** (`EXISTS` false) | *không làm gì* | **0** | *(rỗng)* | ✅ **409** |
| `3` | **vắng** | **0 row** (`EXISTS` false) | *không làm gì* | **0** | *(rỗng)* | ✅ **409** ← G1 đã bịt |

Hai điểm tinh tế, đều **đã kiểm chứng**:

1. **`WHERE row_version = NULL` cho ra `NULL`, không phải `TRUE`** — nên nhánh DO UPDATE tự động trở thành no-op khi client nói "ô này trống" nhưng thực tế có row. Không cần viết thêm gì. `[VERIFIED]`
2. **`SELECT <expr-list> WHERE <cond>` không có `FROM`** là hợp lệ trong SQLite và trả 0 hoặc 1 row `[VERIFIED]`. Đồng thời `SELECT` **đã có sẵn `WHERE`**, nên tự động thoả yêu cầu chống nhập nhằng parser mà SQLite docs cảnh báo cho `INSERT…SELECT…ON CONFLICT` `[CITED]`:
   > "The SELECT statement should always include a WHERE clause, even if that WHERE clause is just 'WHERE true'."

**Có race giữa `EXISTS` và `INSERT` không? KHÔNG.** Cả hai nằm trong **một statement** ⇒ một transaction ngầm ⇒ SQLite chỉ cho **một writer** tại một thời điểm. Đã stress-test: **12 thread cùng UPSERT `expectedVersion=NULL` vào cùng một ô rỗng → đúng 1 thắng, 11 nhận 409, 0 exception, DB có đúng 1 row v=1** — lặp lại 3 lần đều PASS `[VERIFIED]`.

### 3.4 Phương án thay thế đã cân nhắc và **loại**

| Phương án | Vì sao loại |
|---|---|
| **UPDATE trước, INSERT sau, trong 1 tx**; bắt `SQLITE_CONSTRAINT_UNIQUE` (19/**2067** `[VERIFIED]`) làm tín hiệu 409 | Đúng về mặt logic, nhưng: 2 statement, cần tx tường minh, và **dùng exception làm control flow** cho một đường đi *bình thường* (2 người cùng điền một ô). Chậm hơn và ồn hơn. FORM 2R làm được y hệt trong **1 statement, 0 exception**. |
| Giữ FORM 1, rồi phát hiện case-5 bằng cách kiểm tra `RETURNING row_version == 1` khi `@Expected IS NOT NULL` | Suy được (UPDATE với `@Expected ≥ 1` luôn cho version ≥ 2), nhưng **row đã bị INSERT rồi** → phải rollback trong tx. Vòng vo, dễ hỏng. |
| Chỉ dùng `changes()` sau statement | Thừa (§2.2). |

---

## 4. Q3 — Đọc lại version sau khi ghi: **`RETURNING`**

- `RETURNING` có từ **SQLite 3.35.0** `[CITED]`; **Microsoft.Data.Sqlite 8.0.10 bundle SQLite 3.41.2** ⇒ **có** `[VERIFIED]`.
- **Câu hỏi mấu chốt mà docs SQLite KHÔNG trả lời**: `RETURNING` phát ra gì khi `DO UPDATE` bị `WHERE` biến thành no-op? Tôi đã hỏi thẳng tài liệu — nó im lặng. **Đo được: phát ra ĐÚNG 0 ROW.** `[VERIFIED]`

Đây là kết quả **quan trọng nhất về mặt thiết kế** trong mục này, vì nó gộp hai việc vào một statement:

```csharp
var newVersion = await c.QueryFirstOrDefaultAsync<long?>(Form2R, p);
// null      -> conflict -> 409          (không cần đọc rowsAffected)
// có giá trị -> thành công + version mới để echo về client
```

Không cần `SELECT` lại. Không cần round-trip thứ hai. Không có cửa sổ đua giữa write và read-back.

**Bẫy cần tránh:** nếu gọi `Execute()` (ExecuteNonQuery) trên statement có `RETURNING`, **write vẫn xảy ra** và `rowsAffected` vẫn đúng `[VERIFIED]` — nhưng bạn **vứt mất** version mới. Phải dùng `Query*`, không phải `Execute`.

---

## 5. Q4 — Transaction + concurrency. **CHỖ THỨ HAI SPEC SAI.**

### 5.1 `BeginTransaction()` mặc định là **IMMEDIATE** — tin tốt

Nhiều người (kể cả tôi lúc đầu) tưởng `IDbConnection.BeginTransaction()` phát ra `BEGIN` (deferred). **Không phải.**

- `[VERIFIED]`: connection A gọi `BeginTransaction()` **và chưa chạy statement nào**; connection B ghi ngay lập tức nhận `SQLITE_BUSY` ⇒ A **đã giữ write lock từ lúc BEGIN** ⇒ **IMMEDIATE**.
- `[VERIFIED]`: `BeginTransaction(deferred: true)` **tái hiện** được bẫy ⇒ khẳng định `deferred: false` là mặc định.
- `[CITED]`: MS docs — *"Starting with Microsoft.Data.Sqlite version 5.0, transactions **can be** deferred"* (tức là mặc định thì không).
- Overloads có sẵn trong 8.0.10 `[VERIFIED]`: `BeginTransaction()`, `(bool deferred)`, `(IsolationLevel)`, `(IsolationLevel, bool deferred)`.

**Hệ quả:** `TimeLogRepository.UpsertBatchAsync` (Smart Fill) đang dùng `c.BeginTransaction()` ⇒ **đã an toàn**, không dính bẫy nâng cấp khoá. **Không cần đổi sang `BEGIN IMMEDIATE` thủ công.** Trả lời câu hỏi *"`BEGIN IMMEDIATE` có cần không?"*: **không cần viết tay — bạn đã có nó rồi.**

### 5.2 Nhưng: **cấm dùng `deferred: true`**

`[VERIFIED]` — tái hiện bẫy read-then-write upgrade dưới WAL:

```
A: BEGIN DEFERRED; SELECT …        (chụp read snapshot)
B: UPDATE …; (commit)              (snapshot của A thành cũ)
A: UPDATE …                        → SQLITE_BUSY / extended 517 (SQLITE_BUSY_SNAPSHOT)
```

`[CITED]` MS docs cảnh báo đúng điều này:
> "Commands inside a deferred transaction can fail if they cause the transaction to be upgraded from a read transaction to a write transaction while the database is locked. When this happens, the application will need to **retry the entire transaction**."

Và **retry riêng statement là vô ích** — snapshot đã hỏng vĩnh viễn. Phải ROLLBACK toàn bộ tx rồi làm lại.

### 5.3 🔴 G3 — `busy_timeout = 5000` **KHÔNG** giới hạn thời gian chờ

Spec §5.2 liệt kê `busy_timeout: 5000` cho API profile, ngụ ý "writer bị chặn tối đa 5 giây". **Sai.**

`[CITED]` — *Database errors — Microsoft.Data.Sqlite*:
> "Whenever Microsoft.Data.Sqlite encounters a busy or locked error, **it will automatically retry until it succeeds or the command timeout is reached**."
> "The default timeout is **30 seconds**."
→ <https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/database-errors>

Nghĩa là có **HAI** đồng hồ chồng lên nhau: `busy_timeout` (ngủ *bên trong* SQLite) **rồi** vòng retry của Microsoft.Data.Sqlite (tới `CommandTimeout`). Đo thực tế, một writer bị chặn `[VERIFIED]`:

| Cấu hình | Thời gian **thực sự** bị treo trước khi fail |
|---|---|
| **`busy_timeout=5000`, `DefaultTimeout` để mặc định (=30s) — ĐÚNG NHƯ SPEC §5.2** | **33 940 ms** 🔴 |
| `busy_timeout=0`, `DefaultTimeout=30` | 30 054 ms |
| `busy_timeout=5000`, `DefaultTimeout=5` | 5 549 ms |
| **`busy_timeout=1000`, `DefaultTimeout=5`** ← **khuyến nghị** | **5 163 ms** ✅ |
| `busy_timeout=500`, `DefaultTimeout=3` | 3 742 ms |

> Xấp xỉ: **thời gian treo ≈ `busy_timeout` + `DefaultTimeout`.** `busy_timeout` là một lần ngủ **không ngắt được** *bên trong* một lời gọi `sqlite3_step` — vòng retry của M.D.S chỉ kiểm đồng hồ **giữa** các step. Nên đặt `busy_timeout` **lớn hơn** `DefaultTimeout` là vô nghĩa.

Bẫy tệ hơn: `SQLITE_BUSY_SNAPSHOT` (517) **cũng bị M.D.S retry** dù retry là vô vọng ⇒ một deferred-tx upgrade conflict **treo trọn 30 giây rồi mới fail** `[VERIFIED]`. (SQLite lõi thì trả `SQLITE_BUSY` *ngay lập tức* trong tình huống này — nó cố tình **không** gọi busy handler để tránh deadlock `[CITED]` <https://www.sqlite.org/c3ref/busy_handler.html>. Chính lớp M.D.S phía trên mới là thứ retry.)

**Sửa §5.2 — connection profile của API:**

```csharp
var cs = new SqliteConnectionStringBuilder
{
    DataSource     = path,
    Mode           = SqliteOpenMode.ReadWriteCreate,
    Pooling        = true,
    ForeignKeys    = true,
    DefaultTimeout = 5,          // ← THIẾU TRONG SPEC. Không có dòng này: 30 giây.
}.ToString();
// rồi trên mỗi connection:
// PRAGMA journal_mode=WAL; PRAGMA busy_timeout=1000; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;
```

`[VERIFIED]`: `Default Timeout` là **connection-string keyword hợp lệ** (`SqliteConnectionStringBuilder.DefaultTimeout` → `"…;Default Timeout=5;…"`), và nó **chảy thẳng vào `SqliteCommand.CommandTimeout`** ⇒ **mọi lời gọi Dapper tự thừa hưởng**, không phải sửa 14 repository. `[VERIFIED]`: khi không có tranh chấp, nó **không tốn gì** (1000 UPDATE tuần tự: 41ms).

### 5.4 WAL: hai writer đồng thời thì sao?

- **Một-statement autocommit writes serialize sạch sẽ.** `[VERIFIED]`: 8 thread × 50 `UPDATE` lên **cùng một row**, `busy_timeout=5000` → **400 commit, 0 SQLITE_BUSY**, kết quả cuối chính xác. Không mất update nào.
- **Batch tx giữ write lock rất ngắn.** `[VERIFIED]`: 100 row INSERT trong một `BeginTransaction()` = **1 ms** giữ khoá độc quyền (WAL + `synchronous=NORMAL`). ⇒ Smart Fill (`UpsertBatchAsync`) **không** phải vấn đề tranh chấp ở quy mô 10–50 user. Quyết định D3 của spec ("API là process ghi duy nhất") là vững.
- WAL cho phép **reader không bị chặn bởi writer** — đúng như spec kỳ vọng.

---

## 6. Q5 — HTTP contract: 409 + `row_version` **hay** 412 + ETag/If-Match?

Đề bài yêu cầu tôi **đừng chỉ gật đầu với spec**. Tôi đã cố phá nó. Nó đứng vững — nhưng **lý do spec đưa ra thì thiếu**.

### 6.1 RFC 9110 nói gì `[CITED]`

| | |
|---|---|
| **409 Conflict** §15.5.10 | *"The request conflicts with the current state of the target resource."* |
| **412 Precondition Failed** §15.5.13 | *"One or more conditions given in the **request header fields** evaluated to false when tested on the server."* |
| **If-Match** §13.1.1 | Server **MUST** trả 412 khi không khớp. *"The If-Match conditional is useful for preventing the **'lost update' problem** when a client intends to update a resource based on one or more prior observations of that resource."* |
| **428 Precondition Required** | **Không nằm trong RFC 9110** (nó ở RFC 6585) — đừng viện dẫn nhầm. |

→ <https://www.rfc-editor.org/rfc/rfc9110.html>

Nghĩa là: **RFC nêu đích danh If-Match là công cụ chuẩn cho lost-update.** Nếu chỉ đọc RFC, ta sẽ kết luận spec sai.

### 6.2 Nhưng ETag **không lắp được** vào API này — và spec đã tự chốt điều đó mà không nhận ra

**Lý do quyết định:** spec §6.1.1 viết *"`GET /api/timelogs/week` **trả về `row_version` của từng ô**"*. Một response tuần chứa **~35+ ô**, mỗi ô một version.

**HTTP cho bạn đúng MỘT `ETag` cho MỘT response.** Không có cách nào nhét 35 per-cell ETag vào header `ETag`. ⇒ **version bắt buộc phải nằm trong body ở đường đọc.** Không tránh được.

Một khi version đã ở trong body lúc đọc, đưa nó lên header lúc ghi chỉ tạo ra thiết kế **lai** (đọc: body; ghi: header) — **tệ hơn cả hai lựa chọn thuần**.

Các lý do phụ:

1. **Ô timesheet không có URL.** Nó được định danh bằng khoá tự nhiên tổng hợp `(user_id, task_id, work_date)` nằm trong **body** của `PUT /api/timelogs/cell`. ETag là thuộc tính của *representation của một URL*. Muốn dùng If-Match cho ra hồn thì phải đổi thành `PUT /api/timelogs/{userId}/{taskId}/{date}` — một thay đổi kiến trúc thật, chỉ để chiều chuẩn.
2. **412 về mặt ngữ nghĩa là sai** nếu precondition không nằm ở header: §15.5.13 định nghĩa 412 **theo request header fields**. Version trong body ⇒ **409 mới là mã đúng**, không phải "409 là thoả hiệp". **Spec đúng.**
3. **UX yêu cầu body giàu thông tin.** Nút *[Xem thay đổi của họ]* cần giá trị hiện tại của server. 412 theo thông lệ không mang payload hữu ích; 409 thì có. Yêu cầu UX kéo về phía 409.
4. Angular đọc body của 409 trivially (`err.error`); còn quản per-cell ETag trong một lưới là rườm rà.

### 6.3 Trade-off thật, nói thẳng

| | 409 + `row_version` in body (spec) | 412 + ETag/If-Match |
|---|---|---|
| Hợp lưới nhiều ô trong 1 response | ✅ **Bắt buộc phải thế** | ❌ Không thể (1 ETag/response) |
| Đúng chuẩn HTTP | ✅ (409 đúng khi precondition ở body) | ✅ (chuẩn mực kinh điển) |
| Proxy/cache hiểu | ❌ Không | ✅ Có |
| Body conflict giàu thông tin cho UX | ✅ Tự nhiên | ⚠️ Trái thông lệ |
| Hợp `DELETE` (clear cell) | ⚠️ **Vấn đề — xem G6** | ✅ Header đi được với mọi method |
| Đồng nhất một cơ chế toàn API | ✅ | ⚠️ Sẽ lai với đường đọc |

**Kết luận: giữ 409 + `row_version` trong body.** Nhưng spec nên **ghi lý do** (lưới nhiều ô ⇒ ETag không khả thi), chứ không chỉ tuyên bố. Và **chốt một cơ chế duy nhất** — kể cả cho `PUT /api/backlogs/{id}` (nơi ETag *thực sự* lắp được, vì có URL và GET trả một entity). Trộn hai cơ chế trong một API còn tệ hơn chọn cái "kém chuẩn" nhưng nhất quán.

### 6.4 G6 — `DELETE` mang `expectedVersion` bằng đường nào?

Spec: *"Clearing a cell (which is a `DELETE`…) is version-checked the same way"* — nhưng **không nói version đi đâu**. Ba lựa chọn:

| | Đánh giá |
|---|---|
| `DELETE` **có body** | ❌ RFC 9110 §9.3.5: content trong DELETE *"has no generally defined semantics"*; một số proxy strip mất. **Tránh.** |
| `DELETE …?expectedVersion=3` | ⚠️ Dùng được, hơi xấu, nhưng vẫn nhất quán "version nằm trong request". |
| ✅ **Mô hình hoá "xoá" thành `PUT /api/timelogs/cell` với `{ hours: null, expectedVersion: 3 }`** | **Khuyến nghị.** Một endpoint, một cơ chế, một code path. Và nó **khớp sẵn** với ghi chú của spec rằng *"the codebase treats empty and zero as semantically distinct"* — `hours: null` (JSON null) ≠ `hours: 0`. Chính xác cái phân biệt đó. |

SQL cho nhánh xoá (nếu vẫn tách endpoint):
```sql
DELETE FROM TimeLogs
 WHERE user_id=@u AND task_id=@t AND work_date=@d AND row_version=@Expected
RETURNING id;                       -- 0 row -> 409
```
`[VERIFIED]`: version cũ → 0; version khớp → 1; row đã biến mất → 0.
**Câu hỏi mở:** `expectedVersion = null` trên một lệnh xoá nên là **400 Bad Request** (bạn không thể xoá thứ bạn chưa từng thấy), chứ không phải 409. Spec cần chốt.

### 6.5 G4 — Body của 409 phải phân biệt "cũ" với "đã bị xoá"

`rowsAffected == 0` **gộp** hai nguyên nhân `[VERIFIED]`. Nhưng UI cần render hai câu khác nhau:
- *"Chi vừa đổi ô này thành 6.5"* → **[Xem thay đổi của họ]**
- *"Chi vừa **xoá** ô này"* → nút "xem thay đổi của họ" **không có gì để xem**

⇒ handler 409 phải `SELECT` lại row **sau khi** thất bại, rồi:

```jsonc
// 409 Conflict  (application/problem+json — RFC 9457, ASP.NET Core có sẵn ProblemDetails)
{
  "type":   "https://timesheet.local/errors/concurrency-conflict",
  "title":  "Someone else just changed this.",
  "status": 409,
  "resource": "timelog",
  "key":      { "userId": 7, "taskId": 42, "workDate": "2026-07-13" },
  "deleted":  false,
  "current":  { "hours": 6.5, "rowVersion": 4 }   // null khi deleted = true
}
```

**Lưu ý riêng:** spec hứa UX *"Someone else just changed this"* — nhưng **`TimeLogs` không có cột `changed_by`** (inventory §C2), nên **API không thể nói tên ai**. `Backlogs`/`Tasks` thì có (`BacklogAudit.changed_by_name`). Bất đối xứng này cần được chốt: hoặc chấp nhận thông báo vô danh cho timesheet, hoặc thêm cột. **Đừng để lộ ra lúc làm Angular ở M8.4.**

---

## 7. Q6 — Test concurrency **có tính xác định (deterministic)**

### 7.1 Insight cốt lõi: **không cần thread**

Optimistic concurrency **không** phụ thuộc vào chồng lấn thời gian thực. Cửa sổ conflict **chính là con số version cũ**. Nên "hai update đồng thời" tái hiện được **hoàn toàn tuần tự**, **không sleep, không barrier, không flake**:

```csharp
[Fact]
public async Task Two_Concurrent_Updates_Exactly_One_Conflicts()
{
    var repo = NewRepo();                       // temp-file DB, giống 548 test hiện có
    await SeedBacklogAsync(id: 1);              // row_version = 1

    // Alice và Bob CÙNG load màn hình -> cùng thấy version 1
    var aliceSaw = (await repo.GetAsync(1))!.RowVersion;   // 1
    var bobSaw   = (await repo.GetAsync(1))!.RowVersion;   // 1

    // Alice save trước
    var aliceNewVersion = await repo.UpdateAsync(WithProgress(50), expected: aliceSaw);
    Assert.Equal(2, aliceNewVersion);

    // Bob save sau — vẫn cầm version 1
    await Assert.ThrowsAsync<ConcurrencyConflictException>(
        () => repo.UpdateAsync(WithProgress(90), expected: bobSaw));

    // Ghi của Alice còn nguyên; Bob KHÔNG ghi đè
    var final = (await repo.GetAsync(1))!;
    Assert.Equal(50, final.ProgressPercent);
    Assert.Equal(2,  final.RowVersion);
}
```

`[VERIFIED]` — chạy đúng như vậy: alice `rowsAffected=1`, bob `rowsAffected=0`, DB giữ giá trị của Alice. **Đây là test chính.** Nó xác định, nhanh, không bao giờ flake, và nó test đúng **cái invariant** cần test.

### 7.2 Test bảng 5 trường hợp cho UPSERT (chốt chặn G1)

Table-driven, cũng hoàn toàn xác định — chính là bảng ở §3.3. **Phải có case 5** (`expected = N`, row đã bị xoá → 409), nếu không G1 sẽ lặng lẽ quay lại.

### 7.3 Test chống hồi quy cho G2 (Smart Fill)

```csharp
[Fact]
public async Task SmartFill_Bumps_RowVersion_Even_Though_It_Does_Not_Check_It()
{
    await SeedCellAsync(hours: 4m);                       // row_version = 1
    await _repo.UpsertBatchAsync(new[] { Cell(hours: 8m) });   // Smart Fill: ghi đè có chủ đích

    var after = await GetCellAsync();
    Assert.Equal(8m, after.Hours);
    Assert.Equal(2,  after.RowVersion);   // ← NẾU CÒN LÀ 1, cơ chế đã bị chọc thủng

    // và một client cầm version cũ giờ PHẢI nhận conflict
    await Assert.ThrowsAsync<ConcurrencyConflictException>(
        () => _repo.UpsertAsync(Cell(hours: 6m), expectedVersion: 1));
}
```

### 7.4 Test chống hồi quy cho G5 (audit không được ghi khi 409)

```csharp
[Fact]
public async Task Rejected_Update_Writes_No_Audit_Row()
{
    await SeedBacklogAsync(id: 1);                                   // v=1
    await _repo.UpdateAsync(Changed(), expected: 1);                 // v=2, audit +N
    var auditBefore = (await _repo.GetAuditAsync(1)).Count;

    await Assert.ThrowsAsync<ConcurrencyConflictException>(
        () => _repo.UpdateAsync(OtherChange(), expected: 1));        // stale -> 409

    Assert.Equal(auditBefore, (await _repo.GetAuditAsync(1)).Count); // KHÔNG có audit rác
}
```

### 7.5 Test song song thật — chỉ là **lưới an toàn**, không phải test chính

Dùng `Barrier` để N thread cùng lao vào một row, rồi assert **invariant** (đúng 1 thắng), **không** assert thời gian:

```csharp
[Fact]
public async Task Parallel_Writers_Exactly_One_Wins()
{
    await SeedCellAsync();                    // row_version = 1
    const int N = 12;
    using var barrier = new Barrier(N);
    int wins = 0, conflicts = 0;

    await Task.WhenAll(Enumerable.Range(0, N).Select(i => Task.Run(async () =>
    {
        var repo = NewRepo();                 // MỖI thread một connection riêng
        barrier.SignalAndWait();
        try { await repo.UpdateAsync(WithHours(i), expected: 1); Interlocked.Increment(ref wins); }
        catch (ConcurrencyConflictException) { Interlocked.Increment(ref conflicts); }
    })));

    Assert.Equal(1,     wins);
    Assert.Equal(N - 1, conflicts);           // 0 exception nào khác
}
```

`[VERIFIED]` — 3/3 lần chạy: `wins=1, conflicts=11, errors=0`. Cả với `UPDATE` lẫn với `UPSERT expectedVersion=NULL` trên ô rỗng (12 thread → đúng 1 row được tạo).

### 7.6 ⚠️ Bẫy chết người trong test concurrency: `:memory:`

Nếu ai đó viết test concurrency với `Data Source=:memory:`, **mỗi `SqliteConnection` nhận một database RIÊNG**. Hai "writer đồng thời" sẽ ghi vào hai DB khác nhau ⇒ **không bao giờ conflict** ⇒ **test xanh nhưng test rỗng**. Phải dùng **DB file** (hoặc `Cache=Shared`).

**Tin tốt:** 548 test hiện tại **đã** dùng temp-file DB (`Path.GetTempPath()` + `Guid`, xem `DatabaseInitializerTests.cs:25`, `MainViewModelTests.cs:64`, …). ⇒ **Cứ giữ nguyên quy ước đó.** Chỉ cần đừng ai "tối ưu" sang `:memory:`.

### 7.7 Test WAL dưới nhiều writer (spec §9 yêu cầu)

`[VERIFIED]` như một baseline có thể assert: 8 thread × 50 autocommit `UPDATE` lên cùng một row, `busy_timeout=5000` → **400 commit, 0 SQLITE_BUSY**, tổng cộng đúng. Test nên assert **"0 SQLITE_BUSY và tổng đúng"**, không assert thời gian chạy.

---

## 8. Sửa spec — danh sách hành động

| # | Mục spec | Sửa |
|---|---|---|
| **G1** | §6.1.1 | Thêm **hàng thứ 5** vào bảng: `expectedVersion = N` + row **vắng** → **409**. Đổi SQL sang **FORM 2R** (§3.3). Không dùng `INSERT … VALUES … ON CONFLICT DO UPDATE … WHERE` trần. |
| **G2** | §6.1.1 | Ghi rõ thành luật: **"Mọi write vào bảng có version PHẢI tăng `row_version`, kể cả khi không kiểm tra nó."** Áp cho Smart Fill (`UpsertBatchAsync`), retention prune, restore, migration. Carve-out là **không-check**, không phải **không-bump**. |
| **G3** | §5.2 | Thêm dòng **`DefaultTimeout = 5`** vào bảng connection-policy (cột API profile) và hạ `busy_timeout` xuống **1000**. Ghi chú: `busy_timeout` một mình **không** giới hạn được thời gian chờ. |
| **G4** | §6.1, §7.1 | Định nghĩa body 409 có `deleted: bool` + `current: {…} | null`. Ghi nhận `TimeLogs` **không nói được tên ai** đã đổi (không có `changed_by`). |
| **G5** | §6.1 | Bọc `UpdateAsync` (read `before` → versioned UPDATE → audit inserts) trong **một** `BeginTransaction()` và **thoát trước khi ghi audit** nếu `rowsAffected == 0`. (`BeginTransaction()` **đã** là IMMEDIATE ⇒ an toàn.) |
| **G6** | §6.1.1 | Chốt cách `expectedVersion` đi kèm thao tác xoá. Khuyến nghị: bỏ endpoint `DELETE`, mô hình hoá clear = `PUT { hours: null, expectedVersion }`. |
| — | §6.2 (§13) | **`INSERT … SELECT … WHERE … ON CONFLICT`** là construct SQLite-only **mới**. Theo cam kết ở §13 (*"no new SQLite-only construct is introduced in the v10 work without being added to it"*), **phải thêm vào bảng porting surface** ở §13 (Postgres: giữ nguyên; SQL Server: `MERGE`). |
| — | §9 | Bổ sung: test bảng-5-case, test Smart-Fill-bumps-version, test no-audit-on-409, và cảnh báo cấm `:memory:`. |

---

## 9. Phụ lục — output thô của probe

Probe: `<scratchpad>/ConcProbe/` — `Dapper 2.1.79` + `Microsoft.Data.Sqlite 8.0.10`, .NET 8.

```
═══ A. ENVIRONMENT
A1 sqlite_version()            = 3.41.2
A2 Microsoft.Data.Sqlite asm   = 8.0.10.0
A4 journal_mode                = wal
A6 RETURNING supported         = YES (returned row_version=1)
A7 UPSERT DO UPDATE ... WHERE  = YES (parsed + executed)
A8 'SELECT <expr> WHERE <c>' no-FROM = YES (false→0 rows, true→1 rows)

═══ B. DAPPER rowsAffected — plain version-checked UPDATE (spec §6.1)
B1 Execute()      version match   -> rowsAffected=1
B2 Execute()      version STALE   -> rowsAffected=0
B3 ExecuteAsync() version match   -> rowsAffected=1
B4 ExecuteAsync() row MISSING     -> rowsAffected=0   (indistinguishable from stale!)
B5 changes() after a 0-row UPDATE = 0   (ExecuteNonQuery already == changes())
B7 final hours                    = 7   (the STALE write at B2 did NOT land)

═══ C. THE UPSERT — 5 cases x 3 candidate SQL forms (spec §6.1.1)
── FORM 1 (naive)
   C1 expected=NULL, row ABSENT     rowsAffected= 1 -> WROTE        db:[v=1, hours=4 ]  want INSERT   ✅
   C2 expected=NULL, row PRESENT    rowsAffected= 0 -> no-op -> 409 db:[v=1, hours=8 ]  want 409      ✅
   C3 expected=3,    row PRESENT v=3 rowsAffected= 1 -> WROTE       db:[v=4, hours=4 ]  want UPDATE   ✅
   C4 expected=3,    row PRESENT v=7 rowsAffected= 0 -> no-op -> 409 db:[v=7, hours=8 ] want 409      ✅
   C5 expected=3,    row ABSENT      rowsAffected= 1 -> WROTE       db:[v=1, hours=4 ]  want 409      🔴 SILENT RESURRECT
── FORM 2 (guarded)
   C1 …                              rowsAffected= 1 -> WROTE        db:[v=1, hours=4 ] ✅
   C2 …                              rowsAffected= 0 -> no-op -> 409 db:[v=1, hours=8 ] ✅
   C3 …                              rowsAffected= 1 -> WROTE        db:[v=4, hours=4 ] ✅
   C4 …                              rowsAffected= 0 -> no-op -> 409 db:[v=7, hours=8 ] ✅
   C5 expected=3,    row ABSENT      rowsAffected= 0 -> no-op -> 409 db:[no row       ] ✅ GAP CLOSED
── FORM 2R (guarded + RETURNING) — RETURNING emits 0 rows on a no-op DO UPDATE (docs SILENT; verified)
   C1 -> id=1, NEW row_version=1     C2 -> NO ROW -> 409      C3 -> id=1, NEW row_version=4
   C4 -> NO ROW -> 409               C5 -> NO ROW -> 409
C6 Dapper Execute() on a RETURNING stmt: rowsAffected=1, rows in table=1 -> write DID land

═══ D. DELETE (clearing a cell) — version-checked
D1 DELETE stale version -> rowsAffected=0     D2 DELETE match -> rowsAffected=1
D3 DELETE already-gone  -> rowsAffected=0

═══ E. WHAT DOES IDbConnection.BeginTransaction() ACTUALLY EMIT?
E1 B's write got SQLITE_BUSY (5/5) => A's tx is IMMEDIATE
E2 overloads: BeginTransaction() | (Boolean deferred) | (IsolationLevel) | (IsolationLevel, Boolean deferred)

═══ F/I. WAL + busy: WHO GOVERNS THE WAIT?
I0 SqliteCommand default CommandTimeout = 30s   (SqliteConnection.DefaultTimeout = 30s)
I1 busy_timeout=5000, CommandTimeout=30  (SPEC §5.2 AS WRITTEN) -> B failed after  33948ms   🔴
I1 busy_timeout=0,    CommandTimeout=30                          -> B failed after  30054ms
I1 busy_timeout=5000, CommandTimeout=2                           -> B failed after   5538ms
I1 busy_timeout=0,    CommandTimeout=2                           -> B failed after   2017ms
I2 DEFERRED upgrade, CommandTimeout=30s -> SQLITE 5/517 (BUSY_SNAPSHOT) after 30017ms  (hangs 30s for a conflict that can NEVER resolve)
I3 BeginTransaction(deferred:TRUE) upgrade -> 5/517 => deferred:false IS the default
I4 BeginTransaction() [default] -> holds write lock from BEGIN; no upgrade, no 517
I5 100-row batch in ONE BeginTransaction(): 1ms of exclusive write-lock hold
F3 8 threads x 50 autocommit UPDATEs on ONE row: committed=400, SQLITE_BUSY=0, final=400  ✅

═══ G. THE MONEY TEST — 'two concurrent updates -> exactly ONE conflict'
G1 DETERMINISTIC (no threads): alice rowsAffected=1, bob rowsAffected=0; final v=2, hours=alice's  ✅
G2 12 threads all claiming expected=1  -> wins=1, conflicts=11, errors=0, final v=2   (PASS x3)
G3 12 threads UPSERT expected=NULL on SAME empty cell -> wins=1, conflicts=11, errors=0, rows=1, v=1  (PASS x3)

═══ H. UNIQUE-violation code (for the rejected UPDATE-then-INSERT design)
H1 SqliteErrorCode=19, SqliteExtendedErrorCode=2067 ('UNIQUE constraint failed: …')

═══ J. BOUNDING THE WORST-CASE BLOCK FROM THE CONNECTION STRING
J1 SqliteConnectionStringBuilder.DefaultTimeout -> "…;Default Timeout=5;Pooling=True"   (valid keyword)
J2 conn 'Default Timeout=5' -> new SqliteCommand().CommandTimeout = 5s   (flows through to every Dapper call)
J3 busy_timeout=1000 + DefaultTimeout=5  <-- recommended  -> blocked writer fails after 5163ms  ✅
J4 1000 uncontended UPDATEs with DefaultTimeout=5: 41ms total (costs nothing without contention)
```

## 10. Nguồn

- SQLite UPSERT — <https://www.sqlite.org/lang_upsert.html>
- SQLite RETURNING — <https://www.sqlite.org/lang_returning.html>
- SQLite result codes (SQLITE_BUSY 5, BUSY_SNAPSHOT 517, CONSTRAINT_UNIQUE 2067) — <https://www.sqlite.org/rescode.html>
- SQLite busy handler (deadlock ⇒ không gọi handler) — <https://www.sqlite.org/c3ref/busy_handler.html>
- Microsoft.Data.Sqlite — Transactions (deferred; **Optimistic Offline Lock pattern**) — <https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/transactions>
- Microsoft.Data.Sqlite — Database errors (**auto-retry tới command timeout; mặc định 30s**) — <https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/database-errors>
- RFC 9110 §13.1.1 (If-Match), §15.5.10 (409), §15.5.13 (412), §9.3.5 (DELETE body) — <https://www.rfc-editor.org/rfc/rfc9110.html>
