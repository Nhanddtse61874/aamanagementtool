

========== DESIGN 1 ==========
[VERIFIED] All key files read. Now I have a complete picture of the codebase. Here is the concrete design:

---

## Schema v9 â€” Concrete Design

### 1. Migration Block (append to `migrations[]` in `DatabaseInitializer.cs`)

Current array has indices 0-7 (v1 through v8). Append index 8 (v8â†’v9). [VERIFIED: `SchemaVersion = 8`; array length = 8; last step is index 7.]

**`SchemaVersion` bump:** `private const long SchemaVersion = 9;`

**Migration step (index 8):**

```csharp
// v9 -> P13 Task List Operations & History. All additive.
// Tasks gains type + assignee_user_id (nullable; existing rows default NULL, unaffected).
// TaskTags: join table mirroring BacklogTags (no inline FKs, same pattern).
// TaskAudit: per-task field history, mirroring BacklogAudit structure.
// BacklogAudit gains note TEXT NULL (deadline-change reason from B2 Note popup).
static (c, t) => c.Execute(
    @"ALTER TABLE Tasks ADD COLUMN type             TEXT;
      ALTER TABLE Tasks ADD COLUMN assignee_user_id INTEGER;
      CREATE TABLE IF NOT EXISTS TaskTags (
          task_id INTEGER NOT NULL,
          tag_id  INTEGER NOT NULL,
          PRIMARY KEY (task_id, tag_id)
      );
      CREATE TABLE IF NOT EXISTS TaskAudit (
          id                  INTEGER PRIMARY KEY AUTOINCREMENT,
          task_id             INTEGER NOT NULL,
          field               TEXT    NOT NULL,
          old_value           TEXT,
          new_value           TEXT,
          changed_by_user_id  INTEGER,
          changed_by_name     TEXT,
          changed_at          TEXT    NOT NULL
      );
      ALTER TABLE BacklogAudit ADD COLUMN note TEXT;", transaction: t),
```

Place this immediately after the v8 entry (line 268) and before the closing `};`.

---

### 2. Model Changes (`Models/Entities.cs`)

**a) `TaskItem` record â€” add two nullable fields** [VERIFIED current: `record TaskItem(int Id, int BacklogId, string TaskName, int OrderIndex, bool IsActive, string Status = "Todo")`]:

```csharp
public sealed record TaskItem(
    int Id, int BacklogId, string TaskName, int OrderIndex, bool IsActive,
    string Status = "Todo",
    string? Type = null,            // v9: task-level type (mirrors Backlog.Type)
    int? AssigneeUserId = null);    // v9: task-level PCT (mirrors Backlog.AssigneeUserId)
```

C# positional records allow default-valued trailing params; existing `new TaskItem(id, bid, name, ord, active, status)` call-sites keep compiling without change. [VERIFIED: existing callers in `TaskRepository.MapTask` and `InsertAsync` use positional â€” they only set 6 params, the new 2 default to null.]

**b) New `TaskAuditEntry` record â€” mirrors `BacklogAuditEntry`** [VERIFIED: `BacklogAuditEntry` at line 40-42]:

```csharp
// P13 per-task field history (schema v9); mirrors BacklogAuditEntry.
public sealed record TaskAuditEntry(
    int Id, int TaskId, string Field, string? OldValue, string? NewValue,
    int? ChangedByUserId, string? ChangedByName, DateTimeOffset ChangedAt);
```

**c) `BacklogAuditEntry` â€” NO model change needed.** The `note` column is write-only from the repo (passed as a param); `GetAuditAsync` can be extended later to expose it if a UI history panel needs it. For P13, the model stays as-is to avoid breaking existing consumers. [VERIFIED: `BacklogAuditEntry` does not have a `Note` property; `GetAuditAsync` returns it without the note field currently â€” additive SQL SELECT can be added if needed separately.]

---

### 3. Repository Changes

#### 3a. `ITaskRepository` â€” new methods

```csharp
// v9 (P13-B3): task-level type/assignee update + audit.
Task UpdateTypeAssigneeAsync(int taskId, string? type, int? assigneeUserId,
    int? changedByUserId = null, string? changedByName = null);

// v9 task tag links (mirrors BacklogRepository tag methods).
Task<IReadOnlyList<int>> GetTagIdsAsync(int taskId);
Task SetTagsAsync(int taskId, IReadOnlyList<int> tagIds,
    int? changedByUserId = null, string? changedByName = null);  // audited (B3)
Task<IReadOnlyList<TaskAuditEntry>> GetAuditAsync(int taskId);
```

`UpdateAsync` [VERIFIED current: only updates `task_name`, `order_index`, `status`] also needs to audit `status` changes for B3. Options:
- Either extend existing `UpdateAsync(TaskItem, changedBy...)` with audit writes, or
- Keep `UpdateAsync` as-is for non-audited name/order edits and add `UpdateStatusAsync(int taskId, string status, ...)` for the B3 dropdown.

Recommendation: add `UpdateStatusAsync` as a distinct method â€” it's the only status change path that needs audit, and it avoids touching all existing `UpdateAsync` callers. Add it to `ITaskRepository`.

#### 3b. `TaskRepository` â€” implementation sketch for `UpdateTypeAssigneeAsync`

```csharp
public async Task UpdateTypeAssigneeAsync(int taskId, string? type, int? assigneeUserId,
    int? changedByUserId = null, string? changedByName = null)
{
    using var c = _factory.Create();

    var before = await c.QuerySingleOrDefaultAsync<TaskRaw>(
        "SELECT id, backlog_id, task_name, order_index, is_active, status, type, assignee_user_id FROM Tasks WHERE id = @id;",
        new { id = taskId });
    if (before is null) return;

    await c.ExecuteAsync(
        "UPDATE Tasks SET type = @type, assignee_user_id = @uid WHERE id = @id;",
        new { type, uid = assigneeUserId, id = taskId });

    var now = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    async Task LogAsync(string field, string? oldV, string? newV)
    {
        if (string.Equals(oldV ?? "", newV ?? "", StringComparison.Ordinal)) return;
        await c.ExecuteAsync(
            @"INSERT INTO TaskAudit(task_id, field, old_value, new_value,
                changed_by_user_id, changed_by_name, changed_at)
              VALUES(@tid, @field, @old, @new, @uid, @uname, @at);",
            new { tid = taskId, field, old = oldV, @new = newV,
                  uid = changedByUserId, uname = changedByName, at = now });
    }

    await LogAsync("type", before.type, type);

    if (before.assignee_user_id != (assigneeUserId.HasValue ? (long?)assigneeUserId.Value : null))
    {
        string? NameOf(int? id) => id is null ? null
            : c.QuerySingleOrDefault<string?>("SELECT name FROM Users WHERE id = @id;", new { id });
        await LogAsync("assignee",
            NameOf(before.assignee_user_id is { } b ? (int)b : null), NameOf(assigneeUserId));
    }
}
```

`TaskRaw` needs two new nullable fields to support the pre-read: `public string? type { get; set; }` and `public long? assignee_user_id { get; set; }`.

`MapTask` needs to pass these to the record: `new TaskItem((int)r.id, (int)r.backlog_id, r.task_name, (int)r.order_index, r.is_active != 0, r.status ?? "Todo", r.type, r.assignee_user_id is { } a ? (int)a : null)`.

All existing SELECT queries in `TaskRepository` that don't select `type`/`assignee_user_id` will Dapper-map to `null` (SQLite columns not in SELECT â†’ Dapper leaves them at their default), so `GetActiveByBacklogAsync`, `GetActiveForTimesheetAsync`, etc. still work â€” they just return `Type=null, AssigneeUserId=null`. [ASSUMED: Dapper leaves unmatched properties at their default value for class-based raw types, which is correct behavior for nullable reference/value types.]

#### 3c. `IBacklogRepository.UpdateAsync` â€” add `note` param for deadline audit

```csharp
Task UpdateAsync(Backlog backlog, int? changedByUserId = null, string? changedByName = null,
    string? auditNote = null);  // B2: note/reason stored when Internal/External deadline changes
```

In `BacklogRepository.UpdateAsync`, the `LogAsync` local function currently inserts without `note`. Change the INSERT to include the `note` column, passing `auditNote` only when the field being audited is `deadline_internal` or `deadline_external`, null otherwise:

```csharp
async Task LogAsync(string field, string? oldV, string? newV, string? note = null)
{
    if (string.Equals(oldV ?? "", newV ?? "", StringComparison.Ordinal)) return;
    await c.ExecuteAsync(
        @"INSERT INTO BacklogAudit(backlog_id, field, old_value, new_value,
            changed_by_user_id, changed_by_name, changed_at, note)
          VALUES(@bid, @field, @old, @new, @uid, @uname, @at, @note);",
        new { bid = backlog.Id, field, old = oldV, @new = newV,
              uid = changedByUserId, uname = changedByName, at = now, note });
}

// Then at the call sites for the two deadline fields:
await LogAsync("deadline_internal", ..., note: auditNote);
await LogAsync("deadline_external", ..., note: auditNote);
// All other fields: LogAsync("type", ...) â€” note defaults to null
```

[VERIFIED: `BacklogAudit` INSERT at line 147-151 does NOT include `note`; must be updated.]

#### 3d. `SetTagsAsync` on `BacklogRepository` â€” add audit

[VERIFIED: current `SetTagsAsync` at line 199-213 writes no audit.] Add audit writes inside the transaction:

```csharp
public async Task SetTagsAsync(int backlogId, IReadOnlyList<int> tagIds,
    int? changedByUserId = null, string? changedByName = null)
{
    using var c = _factory.Create();
    using var tx = c.BeginTransaction();

    // Capture old set for audit diff
    var oldIds = (await c.QueryAsync<long>(
        "SELECT tag_id FROM BacklogTags WHERE backlog_id = @bid ORDER BY tag_id;",
        new { bid = backlogId }, tx)).Select(i => (int)i).ToHashSet();
    var newIds = tagIds.Distinct().ToHashSet();

    await c.ExecuteAsync("DELETE FROM BacklogTags WHERE backlog_id = @bid;", new { bid = backlogId }, tx);
    foreach (var tagId in newIds)
        await c.ExecuteAsync("INSERT INTO BacklogTags(backlog_id, tag_id) VALUES(@bid, @tid);",
            new { bid = backlogId, tid = tagId }, tx);

    if (changedByUserId.HasValue && !oldIds.SetEquals(newIds))
    {
        var now = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        await c.ExecuteAsync(
            @"INSERT INTO BacklogAudit(backlog_id, field, old_value, new_value,
                changed_by_user_id, changed_by_name, changed_at)
              VALUES(@bid, 'tags', @old, @new, @uid, @uname, @at);",
            new { bid = backlogId, old = string.Join(",", oldIds.OrderBy(x => x)),
                  @new = string.Join(",", newIds.OrderBy(x => x)),
                  uid = changedByUserId, uname = changedByName, at = now }, tx);
    }

    tx.Commit();
}
```

This changes the `IBacklogRepository.SetTagsAsync` signature to add optional `changedByUserId`/`changedByName` params (with defaults = null, so existing callers break nothing).

---

### 4. Files and Symbols â€” Complete List

| File | Change |
|---|---|
| `src/TimesheetApp/Data/DatabaseInitializer.cs` | `SchemaVersion` 8â†’9; append migration index 8 to `migrations[]` |
| `src/TimesheetApp/Models/Entities.cs` | `TaskItem` +`Type` +`AssigneeUserId`; new `TaskAuditEntry` record |
| `src/TimesheetApp/Data/Repositories/ITaskRepository.cs` | Add `UpdateTypeAssigneeAsync`, `UpdateStatusAsync`, `GetTagIdsAsync`, `SetTagsAsync`, `GetAuditAsync` |
| `src/TimesheetApp/Data/Repositories/TaskRepository.cs` | Implement new methods; extend `TaskRaw` with `type`/`assignee_user_id`; extend `MapTask`; extend existing SELECT queries to include `type, assignee_user_id` |
| `src/TimesheetApp/Data/Repositories/IBacklogRepository.cs` | `UpdateAsync` +`auditNote` param; `SetTagsAsync` +`changedByUserId`/`changedByName` params |
| `src/TimesheetApp/Data/Repositories/BacklogRepository.cs` | `UpdateAsync` body: `LogAsync` gains `note` param, deadline calls pass `auditNote`; `SetTagsAsync` body: add audit diff write; `AuditRaw` +`note` field |
| (new) `src/TimesheetApp/Data/Repositories/ITaskAuditRepository.cs` | Optional â€” could keep audit methods on `ITaskRepository` instead; no separate file needed unless scope grows |

---

### 5. Risks

**Additive-only safety:** All schema changes are `ALTER TABLE ... ADD COLUMN` (nullable) or `CREATE TABLE IF NOT EXISTS`. SQLite does not support `ADD COLUMN ... NOT NULL` without a DEFAULT [VERIFIED: existing migrations follow this exact pattern throughout]. No existing row is disturbed.

**Existing callers of `TaskRepository.InsertAsync`:** [VERIFIED: `InsertAsync` at line 66-74] The INSERT SQL does not list `type`/`assignee_user_id`, so it omits them and SQLite uses NULL â€” correct. No change needed to `InsertAsync`.

**`TaskItem` record positional extension:** Adding trailing defaulted params is source-compatible with all existing `new TaskItem(...)` positional call-sites. However, if any test uses `{ }` object initializer or deconstruction on all 6 fields, those will silently ignore the new fields â€” acceptable [ASSUMED: no deconstruction patterns found without reading tests, but the pattern is low-risk since new fields are optional].

**`IBacklogRepository.UpdateAsync` signature change:** The `auditNote` param has a default of `null`, so all existing call-sites (`RequestsViewModel.SaveEditAsync`, etc.) compile unchanged. The note only flows into audit rows for the two deadline fields when explicitly passed.

**`SetTagsAsync` signature change:** Same pattern â€” two new optional params default to null. Existing callers in `RequestsViewModel`/`RequestEditorViewModel` that call `SetTagsAsync(id, tagIds)` keep compiling; they just won't write audit rows (acceptable for the Create path, where no prior value exists).

**`BacklogAudit.note` column:** The existing `GetAuditAsync` SELECT does not fetch `note`. The `AuditRaw` class needs a `public string? note { get; set; }` field, and `GetAuditAsync` SELECT needs `note` added, or the column stays read-only from audit queries. For P13 the note is write-only (B2 shows it in the popup before saving); displaying it in a history panel is out of scope. Risk: if the SELECT is not extended, the data is stored but never shown â€” document this as a known gap for the next phase.

**Seeder:** `SeedDefaultTasksIfEmpty` [VERIFIED: line 296-311] only touches `DefaultTasks`, not `Tasks` â€” unaffected. `EnsureDefaultBacklog` only touches `Backlogs` â€” unaffected.

**`GetTagIdsForAllAsync` on `ITaskRepository`:** Likely needed for the TaskList grid to avoid N+1 when loading task tags. Mirror `BacklogRepository.GetTagIdsForAllAsync` exactly, querying `TaskTags` instead of `BacklogTags`.

========== DESIGN 2 ==========
I have all the verified information I need. Here is the complete Group A concrete design:

---

# Group A â€” Backlog Editor Design (REQ-A1 / A2 / A3 / A4)

## Verified baseline (all [VERIFIED] from reading source)

- [VERIFIED] `BacklogEditorViewModel` (RequestEditorViewModel.cs) has `IsEditMode { get; private init; }`. `ForCreate` sets it `false`, `ForEdit` sets it `true`.
- [VERIFIED] The XAML editor overlay (`RequestsTab.xaml`) has one shared form for both modes â€” no field gating today. The "Tracking" block (lines 220-311) contains deadlines, estimates, Progress, PCA contact, and Tags (checkbox ItemsControl).
- [VERIFIED] Progress field: `TextBox Width="70" HorizontalAlignment="Left"` inside a `StackPanel Grid.Column="0"` in a 3-col Grid (`Width="Auto"` / spacer / `Width="*"`). The `Width="70"` + `Auto` column makes it physically narrow but the column also floats; that is the layout bug (REQ-A3).
- [VERIFIED] Tags: `ItemsControl` with checkbox per `TagPickVm`, inline checkboxes only (lines 291-310). No popup/dropdown.
- [VERIFIED] `BacklogEditorViewModel` has `TagPicks: ObservableCollection<TagPickVm>` and `CheckedTagIds` computed property. `TagPickVm` has `Tag` and `IsChecked`.
- [VERIFIED] `TeamFilter.xaml` pattern: `ToggleButton` ("Teams (N)") + `Popup IsOpen="{Binding IsChecked, ElementName=...}" StaysOpen="False"` + `ItemsControl` of checkboxes inside a shadow `Border`.
- [VERIFIED] `ToolbarGhostToggle` style exists in Theme.xaml (line 690) â€” safe to reuse for Tags toggle.
- [VERIFIED] `ComboBoxSearch` attached behavior exists and handles type-to-filter for ComboBoxes, but it is a ComboBox behavior â€” NOT applicable to a Popup-based multi-select. For the tag-dropdown, type-to-filter is a separate filter TextBox inside the Popup (same approach needed, since tags are chips not strings).
- [VERIFIED] `BacklogsViewModel.SaveNewAsync` passes `Editor.ProgressPercent` into `InsertAsync` (line 202). `SaveEditAsync` passes `Editor.ProgressPercent` into `UpdateAsync` (line 228).
- [VERIFIED] The "Tracking" border wraps ALL of: deadlines, estimates, Progress, PCA, Note, Tags as one section.
- [VERIFIED] `HexToBrush` converter and `Segoe UI Emoji` font are already used for tag chip rendering (lines 297-307).

---

## Decision: Reusable control vs inline for Tags dropdown

**Decision: inline pattern inside RequestsTab.xaml, not a new UserControl.** Rationale: the Tags dropdown is editor-only (not a filter or grid column), the VM binding (`TagPicks`, `CheckedTagIds`) already lives on `BacklogEditorViewModel`, and extracting a UserControl would require a DependencyProperty to pass the `ObservableCollection<TagPickVm>` and the header count â€” more complexity than inline XAML for a single use site. The inline pattern mirrors exactly what TeamFilter.xaml does, without the overhead of a codebehind class.

---

## 1. Mode-driven field visibility/enable

### Mechanism

`IsEditMode` is `bool`, already on the VM and set at construction â€” it never changes after the editor opens. Use `DataTrigger` on `Visibility` and `IsEnabled` in XAML styles/triggers. No new VM properties needed.

### Field table per mode

| Field | CREATE | EDIT |
|---|---|---|
| Code | Visible + Enabled | Visible + Enabled |
| Project | Visible + Enabled | Visible + Enabled |
| Assignee | Visible + Enabled | Visible + Enabled |
| Month/Year/Type | Visible + Enabled | Visible + Enabled |
| Start/End date | Visible + Enabled | Visible + Enabled |
| Rough/Official Estimate | Visible + Enabled | Visible + Enabled |
| Note | Visible + Enabled | Visible + Enabled |
| Tags | Visible + Enabled | Visible + Enabled |
| **Progress %** | Visible + **Disabled** (grayed) | **Collapsed** |
| **Internal deadline** | Visible + Enabled | **Collapsed** |
| **External deadline** | Visible + Enabled | **Collapsed** |
| **PCA contact** | Visible + Enabled | **Collapsed** |

The "Tracking" section border itself stays visible in both modes (it still contains estimates, note, tags). Only the four operational sub-fields hide/disable.

### XAML approach

Wrap each operational field in a `StackPanel` (it is already inside a StackPanel in most cases). Apply a `Style` with a default `Visibility=Visible` and a `DataTrigger` on `IsEditMode=True` â†’ `Visibility=Collapsed`. For Progress specifically: use `IsEnabled` trigger instead (default enabled, `IsEditMode=False` â†’ disabled with a gray override).

Example pattern for each collapsed-in-edit field:
```xml
<StackPanel>
    <StackPanel.Style>
        <Style TargetType="StackPanel">
            <Setter Property="Visibility" Value="Visible"/>
            <Style.Triggers>
                <DataTrigger Binding="{Binding IsEditMode}" Value="True">
                    <Setter Property="Visibility" Value="Collapsed"/>
                </DataTrigger>
            </Style.Triggers>
        </Style>
    </StackPanel.Style>
    <!-- label + control here -->
</StackPanel>
```

For Progress (disabled-in-create, collapsed-in-edit), the TextBox needs two separate triggers: one `DataTrigger` on `IsEditMode=False` â†’ `IsEnabled=False`, and the parent `StackPanel` collapses when `IsEditMode=True`. Actually simpler: Progress sits in a Grid cell as `Grid.Column="0"`. The whole Progress `StackPanel` gets `Visibility=Collapsed` when `IsEditMode=True`, and the TextBox inside gets a `Style` with `IsEnabled=False` trigger when `IsEditMode=False`:

```xml
<!-- Progress StackPanel -->
<StackPanel Grid.Column="0">
    <StackPanel.Style>
        <Style TargetType="StackPanel">
            <Setter Property="Visibility" Value="Visible"/>
            <Style.Triggers>
                <DataTrigger Binding="{Binding IsEditMode}" Value="True">
                    <Setter Property="Visibility" Value="Collapsed"/>
                </DataTrigger>
            </Style.Triggers>
        </Style>
    </StackPanel.Style>
    <TextBlock Text="Progress %" Style="{StaticResource FieldLabel}"/>
    <TextBox Text="{Binding ProgressText, UpdateSourceTrigger=PropertyChanged}">
        <TextBox.Style>
            <Style TargetType="TextBox" BasedOn="{StaticResource {x:Type TextBox}}">
                <Style.Triggers>
                    <DataTrigger Binding="{Binding IsEditMode}" Value="False">
                        <Setter Property="IsEnabled" Value="False"/>
                    </DataTrigger>
                </Style.Triggers>
            </Style>
        </TextBox.Style>
    </TextBox>
</StackPanel>
```

When `IsEnabled=False`, Theme.xaml already sets `Background=Disabled` brush (line 101 in TextBox template implicit style). That gives the visual gray. No new brush needed.

---

## 2. REQ-A3: Fix Progress field layout

### Current problem [VERIFIED]

The Progress Grid has columns `Width="Auto"` / `Width="16"` / `Width="*"`. Progress is in `Grid.Column="0"` with the TextBox having `Width="70" HorizontalAlignment="Left"`. The PCA contact ComboBox is in `Grid.Column="2"` with no width constraint. When `Width="Auto"` is on column 0, the column takes only as much space as the content. The TextBox `Width="70"` is fine in isolation, but the `StackPanel` wrapping it has no explicit width, so the column is 70px while the surrounding sibling columns (estimate pairs) use `Width="*"` and span full width. The "larger" appearance is likely that the Progress box does not align to the left edge of the estimates above it â€” it appears narrow and floating compared to its row's right half (PCA ComboBox).

### Fix

Change the Tracking grid for Progress+PCA from `Width="Auto"` / spacer / `Width="*"` to `Width="*"` / `Width="16"` / `Width="*"` (matching the estimates row above it). Remove `Width="70"` from the TextBox and instead apply `MaxWidth="90"` so it does not stretch to fill 50% of 540px. This aligns the column edge with the estimate TextBoxes above:

```xml
<!-- manual progress % + PCA contact â€” FIXED layout -->
<Grid Margin="0,0,0,10">
    <Grid.ColumnDefinitions>
        <ColumnDefinition Width="*"/>
        <ColumnDefinition Width="16"/>
        <ColumnDefinition Width="*"/>
    </Grid.ColumnDefinitions>
    <StackPanel Grid.Column="0">   <!-- Progress â€” collapsed in edit, disabled in create -->
        ...
        <TextBox MaxWidth="90" HorizontalAlignment="Left"
                 Text="{Binding ProgressText, UpdateSourceTrigger=PropertyChanged}"/>
    </StackPanel>
    <StackPanel Grid.Column="2">   <!-- PCA â€” collapsed in edit -->
        ...
    </StackPanel>
</Grid>
```

`MaxWidth="90" HorizontalAlignment="Left"` keeps the box a sensible narrow width, left-anchored within its now `Width="*"` column, and vertically aligns with the estimate TextBoxes above.

---

## 3. REQ-A4: Tags multi-select dropdown (mirrors TeamFilter pattern)

### VM changes needed

`BacklogEditorViewModel` already has `TagPicks` and `CheckedTagIds`. One new property needed for the header label and one for type-to-filter:

```csharp
// Add to BacklogEditorViewModel

// Tags dropdown state
[ObservableProperty] private string _tagFilterText = string.Empty;

// Header label "Tags (N) â–¾" â€” recalculated when any IsChecked changes
public string TagsHeaderText => $"Tags ({CheckedTagIds.Count})";
```

When a `TagPickVm.IsChecked` changes, `TagsHeaderText` must notify. `TagPickVm.OnIsCheckedChanged` needs to call back to the parent VM. The cleanest approach without an event: replace the current `TagPickVm` construction loop to pass a callback:

```csharp
// In BacklogEditorViewModel constructor, replace:
foreach (var t in tags ?? Array.Empty<Tag>())
    TagPicks.Add(new TagPickVm(t, false));

// With:
foreach (var t in tags ?? Array.Empty<Tag>())
    TagPicks.Add(new TagPickVm(t, false, () => OnPropertyChanged(nameof(TagsHeaderText))));
```

Update `TagPickVm` to accept and invoke the callback:

```csharp
public sealed partial class TagPickVm : ObservableObject
{
    private readonly Action? _onChanged;

    public TagPickVm(Tag tag, bool isChecked, Action? onChanged = null)
    {
        Tag = tag;
        _isChecked = isChecked;
        _onChanged = onChanged;
    }

    public Tag Tag { get; }
    [ObservableProperty] private bool _isChecked;
    partial void OnIsCheckedChanged(bool _) => _onChanged?.Invoke();
}
```

`TagFilterText` property drives a `CollectionViewSource` filter inline in XAML (via a converter) â€” but that is complex. Simpler: expose a `FilteredTagPicks` computed property and recalculate it when `TagFilterText` changes:

```csharp
// In BacklogEditorViewModel

[ObservableProperty] private string _tagFilterText = string.Empty;

partial void OnTagFilterTextChanged(string? _) => OnPropertyChanged(nameof(FilteredTagPicks));

public IEnumerable<TagPickVm> FilteredTagPicks =>
    string.IsNullOrWhiteSpace(TagFilterText)
        ? TagPicks
        : TagPicks.Where(t => t.Tag.Text.Contains(TagFilterText.Trim(),
              StringComparison.OrdinalIgnoreCase));

public string TagsHeaderText => $"Tags ({CheckedTagIds.Count})";
```

Also add `TagsHeaderText` to `OnPropertyChanged` when tags are pre-checked in `ForEdit` (after the foreach loop that sets `IsChecked`):
```csharp
// after the checkedTagIds foreach in ForEdit:
vm.OnPropertyChanged(nameof(vm.TagsHeaderText));
```

### XAML â€” replace the Tags ItemsControl with a Popup-based dropdown

Replace lines 290-310 in RequestsTab.xaml (the `<TextBlock Text="Tags" .../>` + `<ItemsControl .../>`) with:

```xml
<!-- REQ-A4: Tags multi-select dropdown (mirrors TeamFilter pattern) -->
<TextBlock Text="Tags" Style="{StaticResource FieldLabel}"/>
<DockPanel Margin="0,0,0,10" HorizontalAlignment="Left">
    <ToggleButton x:Name="TagsToggle"
                  Style="{StaticResource ToolbarGhostToggle}"
                  Content="{Binding TagsHeaderText}"
                  Padding="10,4"/>
    <Popup IsOpen="{Binding IsChecked, ElementName=TagsToggle}"
           StaysOpen="False"
           PlacementTarget="{Binding ElementName=TagsToggle}"
           Placement="Bottom"
           AllowsTransparency="True"
           PopupAnimation="Fade"
           MinWidth="200" MaxHeight="260">
        <Border Background="{StaticResource Surface}"
                BorderBrush="{StaticResource Border}"
                BorderThickness="1" CornerRadius="8"
                Padding="8,8" Margin="0,4,0,0">
            <Border.Effect>
                <DropShadowEffect Color="#0F172A" Opacity="0.22"
                                  BlurRadius="16" ShadowDepth="2" Direction="270"/>
            </Border.Effect>
            <StackPanel>
                <!-- type-to-filter -->
                <TextBox Text="{Binding TagFilterText, UpdateSourceTrigger=PropertyChanged}"
                         Margin="0,0,0,6" Padding="6,4">
                    <TextBox.Style>
                        <Style TargetType="TextBox" BasedOn="{StaticResource {x:Type TextBox}}">
                            <Style.Triggers>
                                <!-- placeholder -->
                                <DataTrigger Binding="{Binding TagFilterText}" Value="">
                                    <Setter Property="ToolTip" Value="Filter tagsâ€¦"/>
                                </DataTrigger>
                            </Style.Triggers>
                        </Style>
                    </TextBox.Style>
                </TextBox>
                <!-- scrollable chip checklist -->
                <ScrollViewer VerticalScrollBarVisibility="Auto" MaxHeight="190">
                    <ItemsControl ItemsSource="{Binding FilteredTagPicks}">
                        <ItemsControl.ItemTemplate>
                            <DataTemplate>
                                <CheckBox IsChecked="{Binding IsChecked, Mode=TwoWay}"
                                          Margin="0,3" VerticalContentAlignment="Center">
                                    <Border CornerRadius="10" Padding="9,2" Margin="2,0,0,0"
                                            HorizontalAlignment="Left" VerticalAlignment="Center"
                                            Background="{Binding Tag.Color,
                                                Converter={StaticResource HexToBrush}}">
                                        <StackPanel Orientation="Horizontal">
                                            <TextBlock Text="{Binding Tag.Icon}"
                                                       Margin="0,0,5,0"
                                                       FontFamily="Segoe UI Emoji"
                                                       VerticalAlignment="Center"/>
                                            <TextBlock Text="{Binding Tag.Text}"
                                                       FontWeight="SemiBold"
                                                       Foreground="White"
                                                       VerticalAlignment="Center"/>
                                        </StackPanel>
                                    </Border>
                                </CheckBox>
                            </DataTemplate>
                        </ItemsControl.ItemTemplate>
                    </ItemsControl>
                </ScrollViewer>
            </StackPanel>
        </Border>
    </Popup>
</DockPanel>
```

Key design choices:
- `ToggleButton` + `Popup` â€” exact same binding trick as TeamFilter. No codebehind needed.
- `StaysOpen="False"` closes on outside click, same as TeamFilter.
- The filter TextBox binds `TagFilterText` on the VM; `FilteredTagPicks` recomputes synchronously on each keystroke. No CollectionViewSource needed.
- Chip rendering is identical to the existing checkbox ItemsControl (same converter, same Segoe UI Emoji font, same colors) â€” just moved inside the Popup.
- `MaxHeight="260"` on the Popup + `MaxHeight="190"` on the inner ScrollViewer caps the dropdown.
- `DockPanel HorizontalAlignment="Left"` keeps the toggle from stretching full width.

---

## 4. Complete surgical edit list for Group A

### File 1: `src/TimesheetApp/ViewModels/RequestEditorViewModel.cs`

1. Add `Action? onChanged = null` parameter to `TagPickVm` constructor; add `partial void OnIsCheckedChanged` calling it.
2. Add `[ObservableProperty] private string _tagFilterText = string.Empty;` to `BacklogEditorViewModel`.
3. Add `partial void OnTagFilterTextChanged` â†’ `OnPropertyChanged(nameof(FilteredTagPicks))`.
4. Add `FilteredTagPicks` computed property.
5. Add `TagsHeaderText` computed property.
6. In the `BacklogEditorViewModel` constructor loop that builds `TagPicks`, pass `() => OnPropertyChanged(nameof(TagsHeaderText))`.
7. In `ForEdit`, after the `checkedTagIds` foreach, call `vm.OnPropertyChanged(nameof(vm.TagsHeaderText))`.

### File 2: `src/TimesheetApp/Views/Tabs/RequestsTab.xaml`

1. **Deadlines grid (lines 227-241)**: wrap each `StackPanel` (Internal deadline col 0, External deadline col 2) with a style that collapses when `IsEditMode=True`.

2. **Progress+PCA grid (lines 261-281)**:
   - Change column definitions from `Auto / 16 / *` to `* / 16 / *`.
   - Progress `StackPanel` (col 0): add collapse-in-edit trigger; TextBox gets `MaxWidth="90" HorizontalAlignment="Left"` (remove `Width="70"`); TextBox also gets disable-in-create trigger.
   - PCA `StackPanel` (col 2): add collapse-in-edit trigger.

3. **Tags section (lines 290-310)**: replace `TextBlock "Tags"` + `ItemsControl` with the new `TextBlock "Tags"` + `DockPanel` containing `ToggleButton` + `Popup` as shown above.

### No changes to:
- `BacklogRepository.cs` â€” `UpdateAsync` already audits all fields including those now hidden in edit mode; `SaveEditAsync` in `BacklogsViewModel` sends whatever is on the VM (fields not visible in edit mode retain their loaded values since `ForEdit` sets them, and the XAML hides them but does NOT clear their bindings â€” values are preserved and passed through unchanged). This is the correct behavior: hiding a field in the editor should NOT zero it out.
- `BacklogsViewModel.cs` â€” `SaveEditAsync` already passes `Editor.DeadlineInternal`, `Editor.DeadlineExternal`, `Editor.ProgressPercent`, `Editor.PcaContactId`. Since those are loaded in `ForEdit` and the hidden fields remain bound (just not visible), they round-trip unchanged. No logic change needed.

---

## Sketch of tag dropdown (text representation)

```
[ Tags (3) â–¾ ]         â† ToggleButton, ToolbarGhostToggle style
        â†“ (popup opens below)
â”Œâ”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”
â”‚ [Filter tagsâ€¦      ]â”‚  â† TextBox binding TagFilterText
â”‚â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”‚
â”‚ â˜‘ ðŸ”¥ Hot      (red)â”‚
â”‚ â˜‘ âš¡ Urgent  (amber)â”‚
â”‚ â˜‘ âœ… Done   (green)â”‚
â”‚ â˜ ðŸ“Œ Pinned  (teal) â”‚
â”‚ ...                 â”‚  â† ScrollViewer, MaxHeight=190
â””â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”˜
  StaysOpen=False â†’ click outside closes
```

The header text "Tags (3)" updates reactively because each checkbox check calls the `onChanged` callback â†’ `OnPropertyChanged(TagsHeaderText)` on the VM.

========== DESIGN 3 ==========
Good â€” `TaskListRow.BacklogId` is the first positional field [VERIFIED]. The row VM has `BacklogId => Row.BacklogId` available.

Now the `PctAssigneeName` and `PcaContactName` are strings, not entity references. For the editable dropdowns we need the actual `User` and `PcaContact` objects. The `TaskListViewModel.LoadAsync` already loads `userNames` dict and `pcaNames` dict, but discards the full entity list. It needs to retain those lists and pass them to each `TaskListRowVm`.

Also the Backlog entity's `AssigneeUserId` and `PcaContactId` are needed for the commit â€” the row VM currently only stores the name string, not the id. We need to either store the ids in `TaskListRowVm` directly or re-fetch the backlog in the commit command.

**Cleanest approach: store `AssigneeUserId` and `PcaContactId` as fields in `TaskListRow`** (additive record field) â€” or store them in `TaskListRowVm` directly. The read-model is a sealed record; adding nullable int fields is clean and non-breaking.

Add to `TaskListRow`:
```csharp
public sealed record TaskListRow(
    int BacklogId, string BacklogCode, string Project, string? Type,
    string? PctAssigneeName, string? PcaContactName,
    DateOnly? DeadlineInternal, DateOnly? DeadlineExternal, DateOnly? StartDate,
    int? ProgressPercent, decimal LoggedHours, decimal? EstimateHours,
    ScheduleState ScheduleState, IReadOnlyList<Tag> Tags, IReadOnlyList<TaskItem> Tasks,
    // v9 inline-edit ids (nullable â€” needed for commit without re-fetching the backlog)
    int? AssigneeUserId = null, int? PcaContactId = null);
```

And in `LoadAsync` pass `b.AssigneeUserId` and `b.PcaContactId`:
```csharp
var row = new TaskListRow(
    b.Id, b.BacklogCode, b.Project, b.Type, pctName, pcaName,
    b.DeadlineInternal, b.DeadlineExternal, b.StartDate,
    b.ProgressPercent, logged, estimate, state, tags, tasks,
    b.AssigneeUserId, b.PcaContactId);   // v9 addition
```

---

### 5. TYPE column â€” one editable column, fully sketched

**XAML (TYPE column in TaskListTab.xaml):**

Replace the existing `DataGridTemplateColumn Header="TYPE"` (currently read-only pill, lines 233-244) with:

```xml
<DataGridTemplateColumn Header="TYPE" Width="110">
  <DataGridTemplateColumn.CellTemplate>
    <DataTemplate>
      <!-- Read view: teal pill, same as before -->
      <Border Background="{StaticResource AccentSoft}" CornerRadius="9" Padding="8,2"
              HorizontalAlignment="Left" VerticalAlignment="Center"
              Visibility="{Binding EditType, Converter={StaticResource NullToCollapsedConverter}}">
        <TextBlock Text="{Binding EditType}" FontSize="11" FontWeight="SemiBold"
                   Foreground="{StaticResource Accent}"/>
      </Border>
    </DataTemplate>
  </DataGridTemplateColumn.CellTemplate>
  <DataGridTemplateColumn.CellEditingTemplate>
    <DataTemplate>
      <!-- Edit view: simple dropdown of BacklogType.All, bound to EditType -->
      <ComboBox ItemsSource="{Binding Types}"
                SelectedItem="{Binding EditType, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"
                IsEditable="False"/>
    </DataTemplate>
  </DataGridTemplateColumn.CellEditingTemplate>
</DataGridTemplateColumn>
```

When the user selects a new item and tabs/clicks away, WPF commits the `CellEditingTemplate` binding, writing `EditType` on the `TaskListRowVm`. A `partial void OnEditTypeChanged(string? value)` handler in `TaskListRowVm` triggers the commit command.

---

### 6. Commit command â€” `CommitBacklogFieldAsync`

Lives on `TaskListViewModel`. Receives the row + field name as context, fetches the full Backlog, patches the changed field, and calls `UpdateAsync`.

**Constructor addition â€” inject `ICurrentUserService`:**
```csharp
private readonly ICurrentUserService _currentUser;

public TaskListViewModel(
    IBacklogRepository backlogs, ITaskRepository tasks, ITimeLogRepository timeLogs,
    ITagRepository tagsRepo, IPcaContactRepository pcaContacts, IUserRepository users,
    IHolidayRepository holidays, IWorkingDayCalculator calc, IScheduleStateService schedule,
    ITaskListArchiveService archive, IClock clock, ICurrentUserService currentUser,
    IMessenger? messenger = null, ICurrentTeamService? currentTeam = null)
```

`ICurrentUserService` is already registered as singleton in `App.cs` (line 156). `TaskListViewModel` is transient (line 209). The DI container injects it automatically once added to the constructor â€” no `App.cs` registration change needed.

**The commit command (on `TaskListViewModel`):**
```csharp
[RelayCommand]
public async Task CommitTypeAsync(TaskListRowVm row)
{
    var backlog = await _backlogs.GetByIdAsync(row.BacklogId);
    if (backlog is null) return;

    var updated = backlog with { Type = row.EditType };
    await _backlogs.UpdateAsync(updated,
        _currentUser.Current?.Id, _currentUser.Current?.Name);
    _messenger.Send(new DataChangedMessage(DataKind.Backlogs));
}
```

For PCT (Assignee), PCA, Progress the pattern is the same â€” one `[RelayCommand]` per field, each fetching the backlog, patching the one field, and calling `UpdateAsync`. Tags require `SetTagsAsync` in addition.

**Tag commit:**
```csharp
[RelayCommand]
public async Task CommitTagsAsync(TaskListRowVm row)
{
    // SetTagsAsync replaces the whole tag set; diff-audit is NOT built into SetTagsAsync today.
    // REQ-B1 requires tag-change auditing. Approach: manually write a BacklogAudit row before calling SetTagsAsync.
    var oldIds = await _backlogs.GetTagIdsAsync(row.BacklogId);
    var newIds = row.EditTagPicks.Where(t => t.IsChecked).Select(t => t.Tag.Id).ToList();

    // Diff: compute added/removed for audit
    var added = newIds.Except(oldIds).ToList();
    var removed = oldIds.Except(newIds).ToList();
    if (added.Count > 0 || removed.Count > 0)
    {
        // Write one BacklogAudit row summarising the tag change
        await _backlogs.AuditTagChangeAsync(
            row.BacklogId, oldIds, newIds,
            _currentUser.Current?.Id, _currentUser.Current?.Name);
    }
    await _backlogs.SetTagsAsync(row.BacklogId, newIds);
    _messenger.Send(new DataChangedMessage(DataKind.Tags));
}
```

`AuditTagChangeAsync` is a new method on `IBacklogRepository` / `BacklogRepository` that writes one `BacklogAudit` row with `field="tags"`, `old_value="id1,id2"`, `new_value="id3,id4"`.

---

### 7. Internal/External Deadline â€” Note popup (REQ-B2)

**When the user changes `INTERNAL` or `EXTERNAL` and commits the cell, the command must first show a modal note popup.**

The popup is a simple `Window` dialog (same pattern as `StandupEntryDialog`):

**New file: `Views/Dialogs/DeadlineNoteDialog.xaml`:**
```xml
<Window x:Class="TimesheetApp.Views.Dialogs.DeadlineNoteDialog"
        Title="Reason for deadline change" Width="400" SizeToContent="Height"
        WindowStartupLocation="CenterOwner" ShowInTaskbar="False"
        WindowStyle="None" AllowsTransparency="True" ResizeMode="NoResize"
        Background="Transparent">
  <Border Background="{StaticResource Surface}" CornerRadius="12"
          BorderBrush="{StaticResource Border}" BorderThickness="1" Margin="12">
    <Border.Effect>
      <DropShadowEffect Color="#0F172A" Opacity="0.3" BlurRadius="24" ShadowDepth="4" Direction="270"/>
    </Border.Effect>
    <DockPanel>
      <Border DockPanel.Dock="Top" Background="{StaticResource HeaderBg}" CornerRadius="12,12,0,0"
              Padding="20,14" BorderBrush="{StaticResource Border}" BorderThickness="0,0,0,1">
        <TextBlock Text="Reason for deadline change" FontWeight="Bold" FontSize="16"/>
      </Border>
      <Border DockPanel.Dock="Bottom" Background="{StaticResource HeaderBg}" CornerRadius="0,0,12,12"
              Padding="20,12" BorderBrush="{StaticResource Border}" BorderThickness="0,1,0,0">
        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
          <Button Content="Cancel" Style="{StaticResource GhostButton}" Padding="18,6"
                  Margin="0,0,8,0" IsCancel="True"/>
          <Button Content="Save" Padding="22,6" IsDefault="True" Click="OnSave"/>
        </StackPanel>
      </Border>
      <StackPanel Margin="20,16">
        <TextBlock Text="Note (reason for change)" Style="{StaticResource FieldLabel}"/>
        <TextBox x:Name="NoteBox" AcceptsReturn="True" TextWrapping="Wrap"
                 MinHeight="72" MaxHeight="160" VerticalScrollBarVisibility="Auto"
                 Margin="0,0,0,4"/>
        <TextBlock x:Name="ErrorText" Foreground="{StaticResource Danger}"
                   FontSize="12" Visibility="Collapsed"/>
      </StackPanel>
    </DockPanel>
  </Border>
</Window>
```

**`DeadlineNoteDialog.xaml.cs`:**
```csharp
public partial class DeadlineNoteDialog : Window
{
    public string? Note { get; private set; }

    public DeadlineNoteDialog() { InitializeComponent(); }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(NoteBox.Text))
        {
            ErrorText.Text = "Please enter a reason.";
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        Note = NoteBox.Text.Trim();
        DialogResult = true;
    }
}
```

**Commit command for INTERNAL deadline (on `TaskListViewModel`):**
```csharp
[RelayCommand]
public async Task CommitDeadlineInternalAsync(TaskListRowVm row)
{
    var backlog = await _backlogs.GetByIdAsync(row.BacklogId);
    if (backlog is null) return;

    // Only prompt if the value actually changed
    if (backlog.DeadlineInternal == row.EditDeadlineInternal) return;

    // Open the note popup on the UI thread â€” command is async but WPF dispatcher is fine here
    // because RelayCommand executes on the UI thread.
    var dialog = new DeadlineNoteDialog { Owner = Application.Current.MainWindow };
    if (dialog.ShowDialog() != true)
    {
        // User cancelled â€” revert the edit in the row VM
        row.EditDeadlineInternal = backlog.DeadlineInternal;
        return;
    }

    var updated = backlog with { DeadlineInternal = row.EditDeadlineInternal };
    await _backlogs.UpdateAsync(updated,
        _currentUser.Current?.Id, _currentUser.Current?.Name,
        note: dialog.Note);
    _messenger.Send(new DataChangedMessage(DataKind.Backlogs));
}
```

Same pattern for `CommitDeadlineExternalAsync`. The `note` flows into `BacklogAudit.note` via the patched `LogAsync` function above.

---

### 8. Tags â€” multi-select dropdown in the grid cell

Tags use the same popup + ToggleButton pattern as `TeamFilter.xaml`. In the CellEditingTemplate:

```xml
<DataGridTemplateColumn Header="TAGS" Width="1.5*" MinWidth="150">
  <DataGridTemplateColumn.CellTemplate>
    <DataTemplate>
      <!-- Existing chip display (unchanged) -->
      <ItemsControl ItemsSource="{Binding Chips}" ItemTemplate="{StaticResource ChipTemplate}"
                    VerticalAlignment="Center">
        <ItemsControl.ItemsPanel>
          <ItemsPanelTemplate><WrapPanel/></ItemsPanelTemplate>
        </ItemsControl.ItemsPanel>
      </ItemsControl>
    </DataTemplate>
  </DataGridTemplateColumn.CellTemplate>
  <DataGridTemplateColumn.CellEditingTemplate>
    <DataTemplate>
      <StackPanel Orientation="Horizontal">
        <ToggleButton x:Name="TagsToggle" Content="{Binding TagsHeaderText}"
                      Style="{StaticResource ToolbarGhostToggle}" Padding="10,4"/>
        <Popup IsOpen="{Binding IsChecked, ElementName=TagsToggle}" StaysOpen="False"
               PlacementTarget="{Binding ElementName=TagsToggle}" Placement="Bottom"
               AllowsTransparency="True" PopupAnimation="Fade">
          <Border Background="{StaticResource Surface}" BorderBrush="{StaticResource Border}"
                  BorderThickness="1" CornerRadius="8" Padding="10,8" Margin="0,4,0,0" MinWidth="160">
            <Border.Effect>
              <DropShadowEffect Color="#0F172A" Opacity="0.22" BlurRadius="16" ShadowDepth="2" Direction="270"/>
            </Border.Effect>
            <ItemsControl ItemsSource="{Binding EditTagPicks}">
              <ItemsControl.ItemTemplate>
                <DataTemplate>
                  <CheckBox Content="{Binding Tag.Text}" IsChecked="{Binding IsChecked, Mode=TwoWay}"
                            Margin="0,3" VerticalContentAlignment="Center"/>
                </DataTemplate>
              </ItemsControl.ItemTemplate>
            </ItemsControl>
          </Border>
        </Popup>
      </StackPanel>
    </DataTemplate>
  </DataGridTemplateColumn.CellEditingTemplate>
</DataGridTemplateColumn>
```

`TagsHeaderText` on `TaskListRowVm`:
```csharp
public string TagsHeaderText =>
    $"Tags ({EditTagPicks.Count(t => t.IsChecked)})";
```

On `Popup.Closed` (via an event in code-behind or a behavior), call `CommitTagsCommand.Execute(this)`. Since the Popup close happens inside the DataGrid cell editing template, the cleanest hook is the `TagPickVm.IsCheckedChanged` callback â€” each toggle raises a command on the row VM that the row VM then bubbles to the parent via a callback set by the owning `TaskListViewModel`.

Simpler alternative: when the DataGrid `CellEditingEnding` event fires for the TAGS column, the code-behind calls `vm.CommitTagsCommand.Execute(row)`. This is the same approach used for other inline commits via `CellEditingEnding`.

---

### 9. CellEditingEnding â€” wiring commits from code-behind

The DataGrid doesn't naturally call commands when a cell finishes editing. The cleanest WPF pattern is the `CellEditingEnding` event in `TaskListTab.xaml.cs`:

```csharp
private async void OnCellEditingEnding(object sender, DataGridCellEditingEndingEventArgs e)
{
    if (e.EditAction != DataGridEditAction.Commit) return;
    if (DataContext is not TaskListViewModel vm) return;
    if (e.Row.Item is not TaskListRowVm row) return;

    // Column header text drives dispatch (safe â€” we control the headers)
    var header = (e.Column as DataGridTemplateColumn)?.Header?.ToString();
    switch (header)
    {
        case "TYPE":     await vm.CommitTypeCommand.ExecuteAsync(row); break;
        case "PCT":      await vm.CommitAssigneeCommand.ExecuteAsync(row); break;
        case "PCA":      await vm.CommitPcaCommand.ExecuteAsync(row); break;
        case "INTERNAL": await vm.CommitDeadlineInternalCommand.ExecuteAsync(row); break;
        case "EXTERNAL": await vm.CommitDeadlineExternalCommand.ExecuteAsync(row); break;
        case "PROGRESS": await vm.CommitProgressCommand.ExecuteAsync(row); break;
        // TAGS are committed via popup close, not here
    }
}
```

Wire in XAML: `<DataGrid ... CellEditingEnding="OnCellEditingEnding">`.

---

### 10. PCT column â€” ComboBox with ComboBoxSearch (type-to-filter)

```xml
<DataGridTemplateColumn Header="PCT" Width="*" MinWidth="92">
  <DataGridTemplateColumn.CellTemplate>
    <DataTemplate>
      <TextBlock Text="{Binding PctAssigneeName}" VerticalAlignment="Center"/>
    </DataTemplate>
  </DataGridTemplateColumn.CellTemplate>
  <DataGridTemplateColumn.CellEditingTemplate>
    <DataTemplate>
      <ComboBox ItemsSource="{Binding AllUsers}"
                SelectedItem="{Binding EditAssignee, Mode=TwoWay}"
                DisplayMemberPath="Name" TextSearch.TextPath="Name"
                beh:ComboBoxSearch.Enabled="True"
                ToolTip="Type to filter users"/>
    </DataTemplate>
  </DataGridTemplateColumn.CellEditingTemplate>
</DataGridTemplateColumn>
```

Requires `xmlns:beh="clr-namespace:TimesheetApp.Views.Behaviors"` in the UserControl â€” [VERIFIED it's already used in RequestsTab.xaml line 278, need to add to TaskListTab.xaml].

---

### 11. PROGRESS column â€” numeric TextBox in edit mode

```xml
<DataGridTemplateColumn Header="PROGRESS" Width="120">
  <DataGridTemplateColumn.CellTemplate>
    <DataTemplate>
      <!-- existing read display, unchanged -->
      <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
        <ProgressBar Width="60" Height="8" Minimum="0" Maximum="100"
                     Value="{Binding EditProgressPercent, Mode=OneWay, FallbackValue=0, TargetNullValue=0}"
                     Foreground="{StaticResource Accent}"
                     Visibility="{Binding HasProgress, Converter={StaticResource BoolToVisibleConverter}}"/>
        <TextBlock Text="{Binding ProgressText}" Margin="6,0,0,0" VerticalAlignment="Center" FontSize="12"/>
      </StackPanel>
    </DataTemplate>
  </DataGridTemplateColumn.CellTemplate>
  <DataGridTemplateColumn.CellEditingTemplate>
    <DataTemplate>
      <TextBox Text="{Binding EditProgressText, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"
               ToolTip="0â€“100"/>
    </DataTemplate>
  </DataGridTemplateColumn.CellEditingTemplate>
</DataGridTemplateColumn>
```

`EditProgressText` on `TaskListRowVm`:
```csharp
[ObservableProperty] private string? _editProgressText;
partial void OnEditProgressTextChanged(string? value)
{
    if (int.TryParse(value?.Trim(), out var p) && p is >= 0 and <= 100)
        EditProgressPercent = p;
}
```

`ProgressText` and `HasProgress` are updated to use `EditProgressPercent` instead of `Row.ProgressPercent` (since we now track the mutable version).

---

### 12. LoadAsync â€” pass lookup lists to rows

In `TaskListViewModel.LoadAsync`, after building `userNames` and `pcaNames`:
```csharp
var allUsers = new[] { BacklogEditorViewModel.Unassigned }
    .Concat(await _users.GetAllAsync()).ToList();
var allPcaContacts = new[] { BacklogEditorViewModel.NoPcaContact }
    .Concat(await _pcaContacts.GetAllAsync()).ToList();
var allTagsList = (await _tagsRepo.GetAllAsync()).ToList();
```

Then in `TaskListRowVm` construction:
```csharp
var rowVm = new TaskListRowVm(row, teamName, showTeam);
rowVm.AllUsers = allUsers;
rowVm.AllPcaContacts = allPcaContacts;
rowVm.AllTags = allTagsList;
// Initialize editable tag picks from the row's current tags
foreach (var tag in allTagsList)
    rowVm.EditTagPicks.Add(new TagPickVm(tag, row.Tags.Any(t => t.Id == tag.Id)));
// Initialize editable selection fields
rowVm.EditAssignee = allUsers.FirstOrDefault(u => u.Id == row.AssigneeUserId)
    ?? BacklogEditorViewModel.Unassigned;
rowVm.EditPcaContact = allPcaContacts.FirstOrDefault(p => p.Id == row.PcaContactId)
    ?? BacklogEditorViewModel.NoPcaContact;
rowVm.EditType = row.Type;
rowVm.EditDeadlineInternal = row.DeadlineInternal;
rowVm.EditDeadlineExternal = row.DeadlineExternal;
rowVm.EditProgressPercent = row.ProgressPercent;
rowVm.EditProgressText = row.ProgressPercent?.ToString();
```

---

### Summary of all files to touch / create

**Modified files:**
1. `e:\Learning\AAM 2nd\aamanagementtool\src\TimesheetApp\Data\DatabaseInitializer.cs` â€” bump `SchemaVersion` 8â†’9; append v9 migration step (BacklogAudit `+note`, Tasks `+type/+assignee_user_id`, `TaskTags`, `TaskAudit` DDL).
2. `e:\Learning\AAM 2nd\aamanagementtool\src\TimesheetApp\Data\Repositories\IBacklogRepository.cs` â€” add `note` param to `UpdateAsync`; add `AuditTagChangeAsync`.
3. `e:\Learning\AAM 2nd\aamanagementtool\src\TimesheetApp\Data\Repositories\BacklogRepository.cs` â€” implement `note` in `LogAsync`; add `AuditTagChangeAsync`; update `AuditRaw` with `note` field.
4. `e:\Learning\AAM 2nd\aamanagementtool\src\TimesheetApp\Models\ReadModels.cs` â€” add `AssigneeUserId`, `PcaContactId` to `TaskListRow` record (last two optional positional params).
5. `e:\Learning\AAM 2nd\aamanagementtool\src\TimesheetApp\ViewModels\TaskListViewModel.cs` â€” inject `ICurrentUserService`; update `LoadAsync` to populate editable fields on rows; add 6 `[RelayCommand]` commit methods (`CommitTypeAsync`, `CommitAssigneeAsync`, `CommitPcaAsync`, `CommitDeadlineInternalAsync`, `CommitDeadlineExternalAsync`, `CommitProgressAsync`, `CommitTagsAsync`).
6. `e:\Learning\AAM 2nd\aamanagementtool\src\TimesheetApp\ViewModels\TaskListViewModel.cs` (continued) â€” `TaskListRowVm` gains mutable `[ObservableProperty]` fields + `EditTagPicks` + `AllUsers`/`AllPcaContacts`/`AllTags` lists + `TagsHeaderText` + updated `ProgressText`/`HasProgress` computed props.
7. `e:\Learning\AAM 2nd\aamanagementtool\src\TimesheetApp\Views\Tabs\TaskListTab.xaml` â€” remove `IsReadOnly="True"` from DataGrid; replace TYPE/PCT/PCA/INTERNAL/EXTERNAL/PROGRESS columns with `DataGridTemplateColumn` with CellTemplate+CellEditingTemplate; replace TAGS column CellEditingTemplate; add `beh:` namespace; add `CellEditingEnding` handler.
8. `e:\Learning\AAM 2nd\aamanagementtool\src\TimesheetApp\Views\Tabs\TaskListTab.xaml.cs` â€” add `OnCellEditingEnding` handler routing to VM commands.
9. `e:\Learning\AAM 2nd\aamanagementtool\src\TimesheetApp\App.xaml.cs` â€” `ConfigureServices`: add `ICurrentUserService` to `TaskListViewModel` transient registration (already singleton-registered; DI resolves it automatically as long as the constructor param is declared).

**New files:**
10. `e:\Learning\AAM 2nd\aamanagementtool\src\TimesheetApp\Views\Dialogs\DeadlineNoteDialog.xaml`
11. `e:\Learning\AAM 2nd\aamanagementtool\src\TimesheetApp\Views\Dialogs\DeadlineNoteDialog.xaml.cs`

**Note on `App.xaml.cs` DI:** `TaskListViewModel` is registered as `sc.AddTransient<TaskListViewModel>()` [VERIFIED line 209]. Since `ICurrentUserService` is already a registered singleton, adding it to the constructor is sufficient â€” the DI container resolves it automatically. No extra `App.cs` line needed beyond the constructor change.

---

### Current-user source [VERIFIED]

`ICurrentUserService.Current` (property, type `User?`) set during login flow via `MainViewModel.InitializeAsync` â†’ `ResolveAsync`. After login it holds `User { Id, Name, ... }`. In `TaskListViewModel`:
```csharp
_currentUser.Current?.Id    // changedByUserId
_currentUser.Current?.Name  // changedByName
```
If `Current` is null (user not resolved, should not happen post-login), both are null â€” `UpdateAsync` accepts nulls gracefully [VERIFIED at BacklogRepository.cs line 150-151].

========== DESIGN 4 ==========
I now have a complete verified picture of the codebase. Here is the B3 domain design:

---

## B3 Design: Editable Task Sub-rows + TaskAudit

### Verified facts [VERIFIED]

- `TaskItem` record (Entities.cs:51) has: `Id, BacklogId, TaskName, OrderIndex, IsActive, Status`. No `type` or `assignee_user_id`. [VERIFIED]
- `TaskStatus.All` (Entities.cs:47-48): `{ "Todo", "In-process", "Done", "Pending" }`. The "All" entry in the prompt description is NOT in the code â€” that is a filter sentinel used only in UI filter controls, not a task status value. [VERIFIED]
- `ITaskRepository` (ITaskRepository.cs) has `UpdateAsync(TaskItem)` which writes `task_name`, `order_index`, `status` only â€” no `type` or `assignee_user_id`. [VERIFIED]
- `TaskRepository.UpdateAsync` SQL: `UPDATE Tasks SET task_name = @TaskName, order_index = @OrderIndex, status = @Status WHERE id = @Id;` [VERIFIED]
- No `TaskAudit` table exists in DDL or migrations[] (DatabaseInitializer.cs:200-268). [VERIFIED]
- No `TaskTags` table exists. [VERIFIED]
- Current `RowDetailsTemplate` (TaskListTab.xaml:286-311) binds `ItemsSource="{Binding Tasks}"` where `Tasks` is `IReadOnlyList<TaskItem>` from the read-model. The template is read-only: TaskName text + Status chip only. [VERIFIED]
- `TaskListRowVm` exposes `IReadOnlyList<TaskItem> Tasks => Row.Tasks` (TaskListViewModel.cs:321). [VERIFIED]
- `BacklogRepository.UpdateAsync` audits to `BacklogAudit` with `INSERT INTO BacklogAudit(backlog_id, field, ...)` (BacklogRepository.cs:147-151). [VERIFIED]
- `ICurrentUserService` is in DI; `Func<int>` resolves current user id (App.xaml.cs:195-198). [VERIFIED]
- `ITagRepository.GetAllAsync()` returns all tags (ITagRepository.cs:8). [VERIFIED]
- `IUserRepository` is in DI as singleton (App.xaml.cs:146). [VERIFIED]
- `TeamFilter` popup pattern: `ToggleButton` + `Popup IsOpen` + `ItemsControl` of `CheckBox` items (TeamFilter.xaml). [VERIFIED]

---

### 1. Schema (v9) â€” dependency on schema-repos domain

B3 needs these tables from the schema-repos domain â€” do NOT redefine DDL here, reference what that domain delivers:

- `Tasks.type TEXT NULL` â€” migrate v8->v9
- `Tasks.assignee_user_id INTEGER NULL` â€” migrate v8->v9
- `TaskTags(task_id, tag_id) PK(task_id, tag_id)` â€” new table
- `TaskAudit(id, task_id, field, old_value, new_value, changed_by_user_id, changed_by_name, changed_at TEXT NOT NULL)` â€” new table

The v9 migration step (index 8 in `migrations[]`) and `SchemaVersion` bump to 9 are owned by the schema-repos domain. B3 only consumes, never defines.

---

### 2. New entity + audit record in Entities.cs

File: `src/TimesheetApp/Models/Entities.cs`

Add after `TaskItem` (after line 51):

```csharp
// v9 (P13 B3): task-level field history, mirrors BacklogAuditEntry.
public sealed record TaskAuditEntry(
    int Id, int TaskId, string Field, string? OldValue, string? NewValue,
    int? ChangedByUserId, string? ChangedByName, DateTimeOffset ChangedAt);
```

The `TaskItem` record itself does NOT gain `Type` or `AssigneeUserId` as constructor params yet in this domain â€” that change belongs to the schema-repos domain which bumps the entity. B3's `TaskRowVm` carries them as separate observable properties loaded fresh from DB, keeping `TaskItem` immutable-record-safe.

Actually, the schema-repos domain must extend `TaskItem`. B3 depends on receiving an extended `TaskItem` with `Type` and `AssigneeUserId`. For this design, assume schema-repos delivers:

```csharp
// Modified by schema-repos domain:
public sealed record TaskItem(
    int Id, int BacklogId, string TaskName, int OrderIndex, bool IsActive,
    string Status = "Todo",
    string? Type = null,          // v9
    int? AssigneeUserId = null);  // v9
```

B3 reads these extended fields in `TaskRowVm`.

---

### 3. ITaskRepository additions

File: `src/TimesheetApp/Data/Repositories/ITaskRepository.cs`

Add three methods:

```csharp
// v9 (P13 B3): update type + assignee_user_id on a task (called from TaskRowVm commit commands).
Task UpdateExtendedAsync(int taskId, string? type, int? assigneeUserId,
    int? changedByUserId, string? changedByName);

// v9 (P13 B3): replace all tag links for a task in one transaction.
Task SetTaskTagsAsync(int taskId, IReadOnlyList<int> tagIds,
    int? changedByUserId, string? changedByName);

// v9 (P13 B3): update status (audited).
Task UpdateStatusAsync(int taskId, string status,
    int? changedByUserId, string? changedByName);

// v9 (P13 B3): get current tag ids for a task.
Task<IReadOnlyList<int>> GetTagIdsAsync(int taskId);
```

These four methods are implemented in `TaskRepository.cs`. Each writes one or more rows to `TaskAudit` for changed fields, mirroring `BacklogRepository.UpdateAsync`'s `LogAsync` local function pattern.

---

### 4. TaskRepository implementations

File: `src/TimesheetApp/Data/Repositories/TaskRepository.cs`

Pattern for `UpdateStatusAsync`:
```csharp
public async Task UpdateStatusAsync(int taskId, string status,
    int? changedByUserId, string? changedByName)
{
    using var c = _factory.Create();
    var old = await c.QuerySingleOrDefaultAsync<string?>(
        "SELECT status FROM Tasks WHERE id = @id;", new { id = taskId });
    if (string.Equals(old ?? "", status, StringComparison.Ordinal)) return;
    await c.ExecuteAsync(
        "UPDATE Tasks SET status = @s WHERE id = @id;", new { s = status, id = taskId });
    var now = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ",
        System.Globalization.CultureInfo.InvariantCulture);
    await c.ExecuteAsync(
        @"INSERT INTO TaskAudit(task_id, field, old_value, new_value,
            changed_by_user_id, changed_by_name, changed_at)
          VALUES(@tid, 'status', @old, @new, @uid, @uname, @at);",
        new { tid = taskId, old, @new = status,
              uid = changedByUserId, uname = changedByName, at = now });
}
```

`UpdateExtendedAsync` and `SetTaskTagsAsync` follow the same pattern: read-before-write, diff, write, insert audit rows. `SetTaskTagsAsync` audits as `"tags"` field with comma-joined old/new tag id lists (mirrors how backlog tag changes will be audited in B1).

---

### 5. TaskRowVm â€” the per-task editable VM

File: `src/TimesheetApp/ViewModels/TaskListViewModel.cs` (append at bottom, same file as `TaskListRowVm`)

```csharp
/// One editable task sub-row inside a TaskListRowVm's expanded detail.
public sealed partial class TaskRowVm : ObservableObject
{
    private readonly ITaskRepository _tasks;
    private readonly Func<int> _currentUserId;
    private readonly Func<string?> _currentUserName;
    private readonly IMessenger _messenger;

    public TaskRowVm(
        TaskItem task,
        IReadOnlyList<string> availableTypes,   // BacklogType.All
        IReadOnlyList<User> availableUsers,     // for PCT dropdown
        IReadOnlyList<Tag> availableTags,       // all tags
        IReadOnlyList<int> currentTagIds,       // loaded from TaskTags
        ITaskRepository tasks,
        Func<int> currentUserId,
        Func<string?> currentUserName,
        IMessenger messenger)
    {
        _tasks = tasks;
        _currentUserId = currentUserId;
        _currentUserName = currentUserName;
        _messenger = messenger;

        TaskId = task.Id;
        TaskName = task.TaskName;
        _selectedStatus = task.Status;
        _selectedType = task.Type;
        _selectedAssigneeUserId = task.AssigneeUserId;

        AvailableTypes = availableTypes;
        AvailableUsers = availableUsers;
        AvailableTags = availableTags
            .Select(t => new TagCheckItem(t, currentTagIds.Contains(t.Id)))
            .ToList();

        // propagate check changes -> header text
        foreach (var tc in AvailableTags)
            tc.PropertyChanged += (_, _) => OnPropertyChanged(nameof(TagsHeaderText));
    }

    public int TaskId { get; }
    public string TaskName { get; }
    public IReadOnlyList<string> AvailableTypes { get; }
    public IReadOnlyList<User> AvailableUsers { get; }
    public IReadOnlyList<TagCheckItem> AvailableTags { get; }

    public string TagsHeaderText =>
        AvailableTags.Count(t => t.IsChecked) is var n and > 0
            ? $"Tags ({n}) â–¾" : "Tags â–¾";

    [ObservableProperty] private string? _selectedType;
    [ObservableProperty] private int? _selectedAssigneeUserId;
    [ObservableProperty] private string _selectedStatus = "Todo";

    partial void OnSelectedTypeChanged(string? value) =>
        _ = CommitTypeAsync(value);
    partial void OnSelectedAssigneeUserIdChanged(int? value) =>
        _ = CommitAssigneeAsync(value);
    partial void OnSelectedStatusChanged(string value) =>
        _ = CommitStatusAsync(value);

    private async Task CommitStatusAsync(string status)
    {
        await _tasks.UpdateStatusAsync(TaskId, status,
            _currentUserId(), _currentUserName());
        _messenger.Send(new DataChangedMessage(DataKind.Tasks));
    }

    private async Task CommitTypeAsync(string? type)
    {
        await _tasks.UpdateExtendedAsync(TaskId, type, _selectedAssigneeUserId,
            _currentUserId(), _currentUserName());
        _messenger.Send(new DataChangedMessage(DataKind.Tasks));
    }

    private async Task CommitAssigneeAsync(int? userId)
    {
        await _tasks.UpdateExtendedAsync(TaskId, _selectedType, userId,
            _currentUserId(), _currentUserName());
        _messenger.Send(new DataChangedMessage(DataKind.Tasks));
    }

    // Called from TagCheckItem.IsChecked changes via a RelayCommand or direct binding flush.
    [RelayCommand]
    private async Task FlushTagsAsync()
    {
        var checkedIds = AvailableTags.Where(t => t.IsChecked).Select(t => t.TagId).ToList();
        await _tasks.SetTaskTagsAsync(TaskId, checkedIds,
            _currentUserId(), _currentUserName());
        _messenger.Send(new DataChangedMessage(DataKind.Tags));
    }
}

/// One tag in the task sub-row tags popup (mirrors TeamFilterVm's team-check item).
public sealed partial class TagCheckItem : ObservableObject
{
    public TagCheckItem(Tag tag, bool isChecked)
    {
        TagId = tag.Id;
        Text = tag.Text;
        Icon = tag.Icon;
        Color = tag.Color;
        _isChecked = isChecked;
    }

    public int TagId { get; }
    public string Text { get; }
    public string? Icon { get; }
    public string? Color { get; }
    [ObservableProperty] private bool _isChecked;
}
```

**Current user name lookup:** `Func<string?>` can be `() => _currentUserService.Current?.Name` â€” inject `ICurrentUserService` into `TaskListViewModel` and pass `() => _currentUserService.Current?.Name` into `TaskRowVm`.

---

### 6. TaskListViewModel â€” load extended task data

File: `src/TimesheetApp/ViewModels/TaskListViewModel.cs`

In `LoadAsync()`, after building each `TaskListRowVm`, load the tag ids per task and pass them when constructing `TaskRowVm` items. The `TaskListRowVm` gains a `TaskRowVms` property:

```csharp
public IReadOnlyList<TaskRowVm> TaskRowVms { get; private set; } = Array.Empty<TaskRowVm>();
```

This requires `TaskListViewModel` to inject `ICurrentUserService` (already injectable via DI â€” it is registered as singleton). Pass `allUsers` (already loaded) and `allTags` (already loaded as `tagsById`) into each `TaskRowVm`.

The construction site in `LoadAsync`, inside the `foreach` loop after `var tasks = ...`:

```csharp
// Load per-task tag ids for the task sub-rows.
var taskTagIds = new Dictionary<int, IReadOnlyList<int>>();
foreach (var t in tasks)
    taskTagIds[t.Id] = await _tasks.GetTagIdsAsync(t.Id);

var taskRowVms = tasks.Select(t => new TaskRowVm(
    t,
    BacklogType.All,
    allUsers,
    allTagsList,
    taskTagIds.TryGetValue(t.Id, out var tids) ? tids : Array.Empty<int>(),
    _tasks, _currentUserId, _currentUserName, _messenger)).ToList();
```

`allUsers` is `(await _users.GetAllAsync())` (already loaded in `LoadAsync` for `userNames` dict â€” cast to list). `allTagsList` is `tagsById.Values.OrderBy(t => t.Id).ToList()`.

---

### 7. Sub-row XAML template (RowDetailsTemplate replacement)

File: `src/TimesheetApp/Views/Tabs/TaskListTab.xaml`

Replace the current `DataGrid.RowDetailsTemplate` (lines 286-311) with:

```xml
<DataGrid.RowDetailsTemplate>
  <DataTemplate>
    <Border Background="{StaticResource HeaderBg}" Padding="10,6" Margin="34,0,0,0">
      <StackPanel>
        <!-- empty-state -->
        <TextBlock Text="ChÆ°a cÃ³ task nÃ o trong backlog nÃ y."
                   Foreground="{StaticResource TextSecondary}" FontStyle="Italic" FontSize="12"
                   Visibility="{Binding HasNoTasks, Converter={StaticResource BoolToVisibleConverter}}"/>
        <!-- editable task rows -->
        <ItemsControl ItemsSource="{Binding TaskRowVms}">
          <ItemsControl.ItemTemplate>
            <DataTemplate DataType="{x:Type vm:TaskRowVm}">
              <Grid Margin="0,3" Height="30">
                <Grid.ColumnDefinitions>
                  <ColumnDefinition Width="2.5*" MinWidth="160"/>  <!-- TaskName -->
                  <ColumnDefinition Width="110"/>                   <!-- TYPE dropdown -->
                  <ColumnDefinition Width="130"/>                   <!-- PCT dropdown -->
                  <ColumnDefinition Width="120"/>                   <!-- TAGS popup -->
                  <ColumnDefinition Width="110"/>                   <!-- STATUS dropdown -->
                </Grid.ColumnDefinitions>
                <!-- TaskName (read-only) -->
                <TextBlock Grid.Column="0" Text="{Binding TaskName}"
                           VerticalAlignment="Center" TextTrimming="CharacterEllipsis" Margin="0,0,8,0"/>
                <!-- TYPE -->
                <ComboBox Grid.Column="1" ItemsSource="{Binding AvailableTypes}"
                          SelectedItem="{Binding SelectedType, Mode=TwoWay}"
                          Padding="6,2" VerticalContentAlignment="Center"
                          Style="{StaticResource CompactComboBox}"/>
                <!-- PCT (assignee) -->
                <ComboBox Grid.Column="2"
                          ItemsSource="{Binding AvailableUsers}"
                          SelectedValuePath="Id"
                          DisplayMemberPath="Name"
                          SelectedValue="{Binding SelectedAssigneeUserId, Mode=TwoWay}"
                          Padding="6,2" VerticalContentAlignment="Center"
                          Style="{StaticResource CompactComboBox}"/>
                <!-- TAGS popup (mirrors TeamFilter pattern) -->
                <StackPanel Grid.Column="3" Orientation="Horizontal" VerticalAlignment="Center">
                  <ToggleButton x:Name="TagsToggle" Padding="8,2"
                                Content="{Binding TagsHeaderText}"
                                Style="{StaticResource ToolbarGhostToggle}"/>
                  <Popup IsOpen="{Binding IsChecked, ElementName=TagsToggle}" StaysOpen="False"
                         PlacementTarget="{Binding ElementName=TagsToggle}" Placement="Bottom"
                         AllowsTransparency="True" PopupAnimation="Fade">
                    <Border Background="{StaticResource Surface}" BorderBrush="{StaticResource Border}"
                            BorderThickness="1" CornerRadius="8" Padding="10,8" MinWidth="160"
                            Margin="0,4,0,0">
                      <Border.Effect>
                        <DropShadowEffect Color="#0F172A" Opacity="0.22" BlurRadius="16"
                                          ShadowDepth="2" Direction="270"/>
                      </Border.Effect>
                      <StackPanel>
                        <ItemsControl ItemsSource="{Binding AvailableTags}">
                          <ItemsControl.ItemTemplate>
                            <DataTemplate DataType="{x:Type vm:TagCheckItem}">
                              <CheckBox IsChecked="{Binding IsChecked, Mode=TwoWay}"
                                        Margin="0,3" VerticalContentAlignment="Center">
                                <StackPanel Orientation="Horizontal">
                                  <TextBlock Text="{Binding Icon}" FontFamily="Segoe UI Emoji"
                                             Margin="0,0,4,0" VerticalAlignment="Center"
                                             Visibility="{Binding Icon, Converter={StaticResource NullToCollapsedConverter}}"/>
                                  <TextBlock Text="{Binding Text}" VerticalAlignment="Center"/>
                                </StackPanel>
                              </CheckBox>
                            </DataTemplate>
                          </ItemsControl.ItemTemplate>
                        </ItemsControl>
                        <!-- flush tags on popup close (button at bottom) -->
                        <Button Content="Apply" Margin="0,6,0,0"
                                Command="{Binding FlushTagsCommand}"
                                Style="{StaticResource ToolbarButton}"/>
                      </StackPanel>
                    </Border>
                  </Popup>
                </StackPanel>
                <!-- STATUS -->
                <ComboBox Grid.Column="4"
                          ItemsSource="{x:Static vm:TaskStatusSource.All}"
                          SelectedItem="{Binding SelectedStatus, Mode=TwoWay}"
                          Padding="6,2" VerticalContentAlignment="Center"
                          Style="{StaticResource CompactComboBox}"/>
              </Grid>
            </DataTemplate>
          </ItemsControl.ItemTemplate>
        </ItemsControl>
      </StackPanel>
    </Border>
  </DataTemplate>
</DataGrid.RowDetailsTemplate>
```

Note: `vm:TaskStatusSource.All` is a small static helper class in the VM file so XAML can access it without a converter:

```csharp
// XAML-accessible source for the Status ComboBox (static, not an instance property).
public static class TaskStatusSource
{
    public static IReadOnlyList<string> All => TaskStatus.All;
}
```

`{StaticResource CompactComboBox}` â€” if this style does not exist in `Theme.xaml`, use `{x:Null}` and rely on the default DataGrid-child ComboBox appearance, or define a minimal `CompactComboBox` style in `Theme.xaml` as a `BasedOn="{StaticResource {x:Type ComboBox}}"` with `VerticalContentAlignment=Center` and `Padding=4,2`. Verify Theme.xaml for existing compact combo styles before adding.

---

### 8. Files and symbols summary

| File | Change |
|---|---|
| `Models/Entities.cs` | Add `TaskAuditEntry` record after `TaskItem` |
| `Data/Repositories/ITaskRepository.cs` | Add `UpdateExtendedAsync`, `UpdateStatusAsync`, `SetTaskTagsAsync`, `GetTagIdsAsync` |
| `Data/Repositories/TaskRepository.cs` | Implement all 4 new methods with read-before-write audit pattern |
| `ViewModels/TaskListViewModel.cs` | Add `TaskRowVm`, `TagCheckItem`, `TaskStatusSource` classes; extend `TaskListRowVm` with `TaskRowVms` property; extend `TaskListViewModel.LoadAsync` to load task tag ids + construct `TaskRowVm` list; inject `ICurrentUserService` |
| `Views/Tabs/TaskListTab.xaml` | Replace `RowDetailsTemplate` (lines 286-311) with editable sub-row template |
| `App.xaml.cs` | No DI changes needed â€” `ITaskRepository` already registered; `ICurrentUserService` already registered |
| `Views/Theme/Theme.xaml` | Verify/add `CompactComboBox` style if absent |

---

### 9. Status values for the dropdown

`TaskStatus.All` [VERIFIED] = `{ "Todo", "In-process", "Done", "Pending" }`. All four apply to task sub-rows. There is no "All" filter value in the entity â€” that is only a UI sentinel used elsewhere. The STATUS ComboBox binds to these four strings exactly.

---

### 10. Commit command sketch

```
git add src/TimesheetApp/Models/Entities.cs \
        src/TimesheetApp/Data/Repositories/ITaskRepository.cs \
        src/TimesheetApp/Data/Repositories/TaskRepository.cs \
        src/TimesheetApp/ViewModels/TaskListViewModel.cs \
        src/TimesheetApp/Views/Tabs/TaskListTab.xaml
git commit -m "feat(B3): editable task sub-rows â€” PCT/TYPE/TAG/Status dropdowns + TaskAudit

- TaskRowVm: per-task ObservableObject with SelectedType/AssigneeUserId/Status;
  each property change commits via new ITaskRepository methods + audits to TaskAudit
- TagCheckItem: mirrors TeamFilter's check-item pattern for task-level tag popup
- TaskListRowVm gains TaskRowVms loaded from extended TaskItem (v9 type/assignee cols)
  and GetTagIdsAsync per task
- RowDetailsTemplate replaced with 5-column mini-grid (name, type, pct, tags, status)
- All task field changes write to TaskAudit (mirrors BacklogAudit field-level pattern)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Key design decisions

1. `TaskRowVm` carries `Type`/`AssigneeUserId` as separate observable properties rather than mutating the immutable `TaskItem` record â€” keeps the record pure and avoids rebuilding the full row on every edit.

2. Tag flush is explicit (Apply button in popup) rather than on each checkbox toggle â€” avoids one DB write per checkbox click and mirrors the deliberate save pattern used in the backlog tag editor. An alternative is to trigger `FlushTagsAsync` on popup close via a `Popup.Closed` event in code-behind, but the explicit button is simpler and avoids the code-behind hook.

3. `UpdateExtendedAsync` writes both `type` and `assignee_user_id` in one UPDATE, with individual audit rows only for actually-changed fields â€” matches BacklogRepository's per-field diff pattern exactly.

4. `DataChangedMessage(DataKind.Tasks)` is sent after every commit so `TaskListViewModel`'s existing messenger subscription (`DataKind.Tasks` triggers `LoadAsync`) causes a full grid reload â€” consistent with the existing pattern, avoids stale display.

5. The `RowDetailsTemplate` `ItemsSource` switches from `{Binding Tasks}` (type `IReadOnlyList<TaskItem>`) to `{Binding TaskRowVms}` (type `IReadOnlyList<TaskRowVm>`). The `HasNoTasks` guard remains on the outer `TaskListRowVm` and is unaffected.

========== DESIGN 5 ==========
Now I have everything needed. Here is the complete verified analysis and exact design:

---

GROUP C â€” Log Work Holiday Cells

Current state [VERIFIED]:
- `HolidayCellBorder` style is at `TimesheetTab.xaml` lines 29â€“36. It fires on `DataTrigger Tag=True` and sets `Background="{StaticResource HeaderBg}"` â€” the same `#F1F5F9` used by the week-navigation chip and the column-header strip. No "Holiday" placeholder exists.
- Each day Border (Monâ€“Fri, lines 322â€“355) sets `Style="{StaticResource HolidayCellBorder}"` and `Tag="{Binding DataContext.MonIsHoliday ...}"`. The child is a single `TextBox` (`GridDayBox`) with `IsReadOnly` bound to `{...MonReadOnly}`. `MonReadOnly` [VERIFIED, TimesheetViewModel.cs:132] is `IsReadOnly || MonIsHoliday`, so a holiday cell is read-only.
- `HeaderBg` is `#F1F5F9` [VERIFIED, Theme.xaml:26]. `TableHeaderBg` is `#F4F6F9` [VERIFIED, Theme.xaml:508]. `GroupHeaderBg` is `#EEF2F7` [VERIFIED, Theme.xaml:510]. None of these are distinctly darker â€” they're all near-identical light grays. The design needs a new, clearly darker value.

Existing theme brushes (for contrast reference) [VERIFIED, Theme.xaml lines 13â€“31, 503â€“518]:
- `HeaderBg` #F1F5F9, `TableHeaderBg` #F4F6F9, `GroupHeaderBg` #EEF2F7, `Border` #E3E8EE, `BorderStrong` #D4DAE2, `Disabled` #94A3B8.

Design: `HolidayBg` = `#C8D0DA` (noticeably darker than BorderStrong #D4DAE2, clearly distinct at a glance).

Files and changes:

1. `e:/Learning/AAM 2nd/aamanagementtool/src/TimesheetApp/Views/Theme/Theme.xaml` â€” add after line 26 (after the `HeaderBg` line):
```xml
<SolidColorBrush x:Key="HolidayBg" Color="#C8D0DA"/>
```

2. `e:/Learning/AAM 2nd/aamanagementtool/src/TimesheetApp/Views/Tabs/TimesheetTab.xaml` â€” replace `HolidayCellBorder` style (lines 29â€“36) with:
```xml
<Style x:Key="HolidayCellBorder" TargetType="Border">
  <Style.Triggers>
    <DataTrigger Binding="{Binding RelativeSource={RelativeSource Self}, Path=Tag}" Value="True">
      <Setter Property="Background" Value="{StaticResource HolidayBg}"/>
      <Setter Property="ToolTip" Value="Holiday â€” not a working day"/>
    </DataTrigger>
  </Style.Triggers>
</Style>
```

3. Each of the five day-column Borders (lines 322, 329, 336, 343, 350) currently contains only a `TextBox`. Replace the content with a `Grid` that overlays the existing `TextBox` with a "Holiday" placeholder `TextBlock` visible when Tag=True. Pattern (same for all five â€” only the `*IsHoliday` / `*ReadOnly` binding names differ):
```xml
<Border Grid.Column="1" BorderBrush="{StaticResource Border}" BorderThickness="1,0,0,0"
        Style="{StaticResource HolidayCellBorder}"
        Tag="{Binding DataContext.MonIsHoliday, RelativeSource={RelativeSource AncestorType=UserControl}}">
  <Grid>
    <TextBox Style="{StaticResource GridDayBox}"
             IsReadOnly="{Binding DataContext.MonReadOnly, RelativeSource={RelativeSource AncestorType=UserControl}}"
             Text="{Binding Mon, UpdateSourceTrigger=LostFocus, TargetNullValue='',
                   ValidatesOnNotifyDataErrors=True, NotifyOnValidationError=True}"/>
    <!-- Holiday placeholder: shown only when this column is a holiday day -->
    <TextBlock Text="Holiday" HorizontalAlignment="Center" VerticalAlignment="Center"
               FontSize="11" FontStyle="Italic" Foreground="{StaticResource TextSecondary}"
               IsHitTestVisible="False">
      <TextBlock.Style>
        <Style TargetType="TextBlock">
          <Setter Property="Visibility" Value="Collapsed"/>
          <Style.Triggers>
            <DataTrigger Binding="{Binding DataContext.MonIsHoliday,
                         RelativeSource={RelativeSource AncestorType=UserControl}}" Value="True">
              <Setter Property="Visibility" Value="Visible"/>
            </DataTrigger>
          </Style.Triggers>
        </Style>
      </TextBlock.Style>
    </TextBlock>
  </Grid>
</Border>
```
Repeat for Tue/Wed/Thu/Fri with the matching `TueIsHoliday`, `WedIsHoliday`, `ThuIsHoliday`, `FriIsHoliday` property names. The `TextBox` remains (just invisible content when empty) so read-only enforcement is unchanged. `IsHitTestVisible="False"` on the overlay keeps clicks on the Border from focusing the hidden label instead of the TextBox.

No ViewModel changes needed for C.

---

GROUP D â€” Reports "NOT LOGGED" Warning

Current state [VERIFIED]:
- `ReportsViewModel.cs` line 122: `ObservableCollection<MissingLogWarning> MissingBanner { get; }` exists but is never bound in the XAML.
- `ReportsViewModel.cs` line 124: `[ObservableProperty] string _bannerText` is the property that IS bound.
- `LoadBannerAsync` (lines 221â€“230): populates `MissingBanner` with one `MissingLogWarning(UserName)` per missing user, then immediately collapses the whole list into `BannerText = string.Join("; ", ...)` â€” a single joined string.
- `ReportsTab.xaml` lines 87â€“95: the stat card (column 3 in a 4-column Grid) has two TextBlocks:
  - Label: `Text="âš  NOT LOGGED"` with `Foreground="{StaticResource AmberFg}"`
  - Value: `Text="{Binding BannerText}"` with `TextTrimming="CharacterEllipsis"` (single-line ellipsis â€” this is what truncates)
- `ReportsTab.xaml` lines 16â€“29: a top banner Border also binds `BannerText` as a full `TextWrapping=Wrap` TextBlock â€” this is separate from the stat card.

`MissingLogWarning` is defined in `e:/Learning/AAM 2nd/aamanagementtool/src/TimesheetApp/Models/ReadModels.cs` line 81 as `sealed record MissingLogWarning(string UserName)`. [VERIFIED]

Assessment: `MissingBanner` (the per-user collection) already exists in the VM and is populated. The XAML just doesn't use it â€” it uses the flat `BannerText` string. No VM change is required; we only need to rewire the stat card to bind to `MissingBanner` via an `ItemsControl` inside a `ScrollViewer`.

Files and changes:

1. `e:/Learning/AAM 2nd/aamanagementtool/src/TimesheetApp/Views/Tabs/ReportsTab.xaml` â€” replace the NOT LOGGED stat card content (lines 87â€“95, the `Border Grid.Column="3"` block) with:
```xml
<Border Grid.Column="3" Margin="5,0,0,0" Background="{StaticResource AmberBg}"
        BorderBrush="{StaticResource AmberBorder}" BorderThickness="1" CornerRadius="8" Padding="14,11">
  <StackPanel>
    <TextBlock Style="{StaticResource StatLabel}" Text="&#9888; NOT LOGGED" Foreground="{StaticResource AmberFg}"/>
    <!-- D1: per-user list with fixed max height + vertical scroll instead of single truncated line -->
    <ScrollViewer MaxHeight="80" VerticalScrollBarVisibility="Auto" Margin="0,6,0,0"
                  HorizontalScrollBarVisibility="Disabled">
      <ItemsControl ItemsSource="{Binding MissingBanner}">
        <ItemsControl.ItemTemplate>
          <DataTemplate>
            <TextBlock Text="{Binding UserName, StringFormat='{}{0} has not logged'}"
                       TextWrapping="Wrap" FontSize="12" FontWeight="SemiBold"
                       Foreground="{StaticResource AmberFg}" Margin="0,1"/>
          </DataTemplate>
        </ItemsControl.ItemTemplate>
        <!-- "None" placeholder when collection is empty -->
        <ItemsControl.Style>
          <Style TargetType="ItemsControl">
            <Style.Triggers>
              <DataTrigger Binding="{Binding MissingBanner.Count}" Value="0">
                <Setter Property="Visibility" Value="Collapsed"/>
              </DataTrigger>
            </Style.Triggers>
          </Style>
        </ItemsControl.Style>
      </ItemsControl>
    </ScrollViewer>
    <!-- "None" label shown only when no missing users -->
    <TextBlock Text="None" FontSize="12" FontWeight="SemiBold" Foreground="{StaticResource AmberFg}"
               Margin="0,6,0,0">
      <TextBlock.Style>
        <Style TargetType="TextBlock">
          <Setter Property="Visibility" Value="Collapsed"/>
          <Style.Triggers>
            <DataTrigger Binding="{Binding MissingBanner.Count}" Value="0">
              <Setter Property="Visibility" Value="Visible"/>
            </DataTrigger>
          </Style.Triggers>
        </Style>
      </TextBlock.Style>
    </TextBlock>
  </StackPanel>
</Border>
```

Note on the full string format: the current `BannerText` produces `"{UserName} has not logged in {n} days"`. Since `n` comes from config and is not on the `MissingLogWarning` record, two options:
- Option A (minimal, no VM change): use `StringFormat='{}{0} has not logged'` in the DataTemplate and drop the day-count from the per-user lines. The top amber banner (lines 16â€“29) still shows `BannerText` with the day count.
- Option B (full fidelity): add an `int NDays` property to `MissingLogWarning` in `Models/ReadModels.cs`, pass it from `LoadBannerAsync`, and use `StringFormat='{}{0} has not logged in {1} days'` with a `MultiBinding`. This requires one small VM/model edit.

Option A requires zero VM/model changes and is the simpler path. Option B matches the exact wording of the current banner. The top amber banner at lines 16â€“29 of ReportsTab.xaml already wraps with `TextWrapping=Wrap` and shows the full sentence with day count â€” so Option A is sufficient for REQ-D1 (per-user scrollable list in the stat card; full info in the top banner).

No schema change. The existing `MissingBanner` ObservableCollection is already populated correctly by `LoadBannerAsync`.

---

Summary of touched files:

- `e:/Learning/AAM 2nd/aamanagementtool/src/TimesheetApp/Views/Theme/Theme.xaml` â€” add `HolidayBg` brush (#C8D0DA) after line 26
- `e:/Learning/AAM 2nd/aamanagementtool/src/TimesheetApp/Views/Tabs/TimesheetTab.xaml` â€” rewrite `HolidayCellBorder` style (lines 29â€“36) to use `HolidayBg`; wrap each of the 5 day-cell TextBoxes in a Grid with an overlay "Holiday" TextBlock
- `e:/Learning/AAM 2nd/aamanagementtool/src/TimesheetApp/Views/Tabs/ReportsTab.xaml` â€” replace the NOT LOGGED stat card body (lines 87â€“95) with a ScrollViewer+ItemsControl bound to `MissingBanner`
