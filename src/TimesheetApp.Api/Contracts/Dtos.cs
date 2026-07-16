using TimesheetApp.Models;

namespace TimesheetApp.Api.Contracts;

// =====================================================================================================
// Wire contracts. M8.4 generates its TypeScript client from the OpenAPI document these produce, so a
// field missing here is a field the client cannot send back.
//
// EIGHT ENTITIES CARRY row_version AND THEIR DTOs MUST EXPOSE IT AS `rowVersion`:
//     User · Team · Backlog · TaskItem · TimeLog · Tag · PcaContact   (Models/Entities.cs)
//     StandupIssue                                                    (Models/StandupModels.cs)
// Drop it and the client cannot send back the expectedVersion a checked write requires — M8.2's entire
// optimistic-concurrency mechanism is dead for that entity, silently.
//
// DO NOT INVENT A VERSION FOR:
//     TaskTemplate · DefaultTask · Holiday   — no row_version column; a write bumping it is a SQL error.
//     StandupEntry                           — deliberately unversioned: owner-gated, last-write-wins BY
//                                              DESIGN. Two users cannot reach the same row, so there is no
//                                              race to guard.
//
// REQUEST BODIES ARE NOT HERE. Wave 2 owns Endpoints/*.cs and declares its own request records inside its
// own file. Prefix them with your area (TimesheetSaveCellRequest, BacklogUpdateRequest, ...) — all four
// endpoint files share the TimesheetApp.Api.Endpoints namespace, so an unprefixed `SaveRequest` in two of
// them is a duplicate-type compile error found only at the merge.
// =====================================================================================================

// ---- Auth --------------------------------------------------------------------------------------------

/// <summary><c>Username</c> is the <c>Users.username</c> column (schema v10 renamed it from
/// windows_username), NOT the display name.</summary>
public sealed record LoginRequest(string? Username, string? Password);

/// <summary>Returned on a successful login so the SPA does not need a second round-trip for identity.</summary>
public sealed record LoginResponse(int Id, string Username, string Name, bool IsAdmin);

/// <summary>The authenticated caller's own context — the wire projection of <c>IClientContext</c>.
///
/// <para><c>MemberTeamIds</c> is the AUTHORIZATION BOUND; <c>ActiveTeamId</c> is the current WORKING SCOPE
/// (a single team). Different things, and the client needs both: the first bounds which teams a filter may
/// offer, the second is the team a new timesheet or standup row lands in.</para></summary>
public sealed record MeResponse(
    int Id,
    string Name,
    bool IsAdmin,
    IReadOnlyList<int> MemberTeamIds,
    int ActiveTeamId);

/// <summary>The body of EVERY successful checked write. <c>RowVersion</c> is what the checked repository
/// call returned — the caller's next <c>expectedVersion</c>.
///
/// <para><b>Never re-read the version after a write.</b> Between the write committing and the re-read,
/// another client can write: you would hand back THEIR version with YOUR data, and the next save would pass
/// the check and silently overwrite them — the exact lost update this mechanism exists to prevent,
/// laundered through a read-back. <c>GetRowVersionAsync</c> was deleted for this reason.</para></summary>
public sealed record SavedBody(long RowVersion);

// ---- Versioned entities (rowVersion REQUIRED) ---------------------------------------------------------

/// <summary>The entity property is still <c>WindowsUsername</c> (renaming it was judged out of scope at
/// 28+ consumers), but the column and the wire field are both <c>username</c>.</summary>
public sealed record UserDto(
    int Id, string Name, string? Username, bool IsActive, bool IsAdmin, long RowVersion);

public sealed record TeamDto(
    int Id, string Name, bool IsActive, DateTimeOffset CreatedAt, long RowVersion);

public sealed record BacklogDto(
    int Id, string BacklogCode, string Project, DateTimeOffset CreatedAt,
    DateOnly? StartDate, DateOnly? EndDate, string? PeriodMonth, string? Type,
    int? AssigneeUserId, DateOnly? DeadlineInternal, DateOnly? DeadlineExternal,
    decimal? RoughEstimateHours, decimal? OfficialEstimateHours,
    int? ProgressPercent, string? Note, int? PcaContactId, int? TeamId,
    long RowVersion);

/// <summary>The LIST shape (<c>GET /api/backlogs</c>). Deliberately NOT <see cref="BacklogDto"/>:
/// <c>BacklogDto</c> is the EDITOR's shape (what <c>GET /{id}</c> and <c>POST</c> return), and a
/// <c>TaskCount</c> on it would force every single-backlog read to compute a number nobody uses.
///
/// <para><b>No <c>rowVersion</c>, deliberately.</b> The Edit button does a fresh <c>GET /{id}</c>, which
/// returns the authoritative version. A version carried on a list row is STALE BY CONSTRUCTION — and a
/// stale version is exactly the thing that gets narrowed with a <c>!</c> and silently overwrites somebody.
/// This is the one DTO on a versioned entity that must NOT expose the token (see the file header).</para></summary>
public sealed record BacklogListItemDto(
    int Id, string BacklogCode, string Project, int TaskCount,
    string? PeriodMonth, string? Type, int? AssigneeUserId, int? TeamId);

/// <summary>Projected from <c>BacklogAuditEntry</c> (<c>Models/Entities.cs</c>). <c>BacklogId</c> is dropped
/// (it is the path param); <c>ChangedByUserId</c> is dropped (the panel renders the NAME — and the
/// repository audits by name precisely so a deleted user's history still reads).</summary>
public sealed record BacklogAuditDto(
    int Id, string Field, string? OldValue, string? NewValue,
    string? ChangedByName, DateTimeOffset ChangedAt, string? Note);

public sealed record TaskItemDto(
    int Id, int BacklogId, string TaskName, int OrderIndex, bool IsActive,
    string Status, string? Type, int? AssigneeUserId, long RowVersion);

public sealed record TimeLogDto(
    int Id, int UserId, int TaskId, DateOnly WorkDate, decimal Hours, long RowVersion);

public sealed record TagDto(
    int Id, string Text, string Icon, string Color, long RowVersion);

public sealed record PcaContactDto(int Id, string Name, bool IsActive, long RowVersion);

public sealed record StandupIssueDto(
    int Id, int EntryId, string IssueText, string? SolutionText, string Status,
    int OrderIndex, long RowVersion);

// ---- Name-only projections ----------------------------------------------------------------------------

/// <summary>Id + display name, and NOTHING else — deliberately narrower than <see cref="UserDto"/> and
/// <see cref="PcaContactDto"/>.
///
/// <para><b>Why it exists.</b> The backlog editor must render the name of an assignee (or PCA contact) who
/// has since been DEACTIVATED — otherwise opening such a backlog and saving without touching anything
/// silently clears the assignee. The ACTIVE lists (<c>GET /api/users</c>, <c>GET /api/pca-contacts</c>) omit
/// that person by construction, and the full lists (<c>/api/users/all</c>, <c>/api/pca-contacts/all</c>) are
/// <c>AdminPolicy</c>-gated — an ordinary user reading one gets a 403, the list's forkJoin errors, and the
/// whole screen dies with it.</para>
///
/// <para><b>Why it is safe to leave open to any authenticated caller.</b> It carries no <c>username</c> —
/// the credential handle the admin gate on <c>/all</c> exists to protect — no <c>isAdmin</c>, and no
/// <c>rowVersion</c>: there is nothing here to write back, so no version is needed and none is offered.
/// <b>Do not "just reuse <see cref="UserDto"/>"</b> for these routes: that re-exposes <c>username</c> to
/// every authenticated caller and quietly undoes the boundary <c>/api/users/all</c> is guarding.</para></summary>
public sealed record NamedRefDto(int Id, string Name);

// ---- Task List screen (M9 P3b) -------------------------------------------------------------------------

/// <summary>The wire projection of <c>TaskListRow</c>. Structurally identical to the Core read-model EXCEPT
/// for its last two members, which is the whole reason it exists: <c>TaskListRow.Tags</c> is a list of
/// <c>Tag</c> ENTITIES and <c>.Tasks</c> a list of <c>TaskItem</c> ENTITIES, and serialising the read-model
/// straight out would put entity shapes on the wire (a <c>Tag</c> carries a <c>createdAt</c> no client has any
/// use for) and into the generated TypeScript client. The <c>Tasks</c> here are <see cref="TaskItemDto"/>s and
/// so carry the <c>rowVersion</c> the Task List's inline status/type/assignee editors must send back.
///
/// <para><c>ScheduleState</c> passes through as the Core enum: it is a pure value with no entity behind it,
/// exactly like the <c>WeeklyDayTotal</c> / <c>TeamNode</c> read-models the Reports responses already put on
/// the wire.</para>
///
/// <para><c>AssigneeUserId</c> / <c>PcaContactId</c> ride alongside <c>PctAssigneeName</c> /
/// <c>PcaContactName</c>: the names render the cell, the ids SEED the inline Type/PCT/PCA <c>&lt;select&gt;</c>s
/// (a dropdown bound to a name has no option to preselect). They are copied straight from the read-model.</para></summary>
public sealed record TaskListRowDto(
    int BacklogId, string BacklogCode, string Project, string? Type,
    string? PctAssigneeName, string? PcaContactName,
    int? AssigneeUserId, int? PcaContactId,
    DateOnly? DeadlineInternal, DateOnly? DeadlineExternal, DateOnly? StartDate, DateOnly? EndDate,
    int? ProgressPercent, decimal LoggedHours, decimal? EstimateHours,
    ScheduleState ScheduleState, IReadOnlyList<TagDto> Tags, IReadOnlyList<TaskItemDto> Tasks,
    int? TeamId);

/// <summary>The whole Task List screen in ONE response — the grid rows and the Gantt built from those same
/// rows, at one instant.
///
/// <para><b>One record rather than two calls, deliberately</b> (see <c>TaskListScreen</c> in Core): the grid's
/// schedule chips and the Gantt's bar colours are the SAME <c>ScheduleState</c>. Fetched separately, a write
/// landing between the two requests would let the chart and the grid disagree on screen with no way for the
/// client to notice.</para>
///
/// <para><c>Gantt</c> is the Core <c>GanttModel</c> unchanged: it is a pure geometry read-model (a working-day
/// axis and one bar per backlog) with no entity anywhere inside it, so there is nothing to project away.</para></summary>
public sealed record TaskListScreenDto(IReadOnlyList<TaskListRowDto> Rows, GanttModel Gantt);

// ---- Settings + archive (M9 P3d/P3e) --------------------------------------------------------------------

/// <summary>One row of the DB-backed key/value <c>Settings</c> store.
///
/// <para><c>Value</c> is nullable because an UNSET key is the normal state, not an error: every key is unset
/// on a fresh database, and the caller's correct response is to fall back to the documented default (3, for
/// the <c>chua_log_n_days</c> warning window). <b>No <c>rowVersion</c>:</b> Settings is deliberately
/// unversioned — key-addressed, last-write-wins IS the correct semantics there (see
/// <c>DatabaseInitializer</c>), so there is no token to hand out and none is offered.</para></summary>
public sealed record SettingDto(string Key, string? Value);

/// <summary>The SERVER-SIDE path of a file an archive route just wrote. Not a download — the standup archive
/// (DR-09) exists to accumulate markdown next to the database, where the backup job picks it up, and the path
/// is what an admin needs in order to find it. A route that hands the CONTENT to a browser instead returns
/// <c>text/markdown</c> and does not use this.</summary>
public sealed record ArchivedFileDto(string Path);

// ---- Deliberately UNVERSIONED -------------------------------------------------------------------------

public sealed record TaskTemplateDto(int Id, string TemplateName, string TaskName, int OrderIndex);

public sealed record DefaultTaskDto(int Id, string TaskName, int OrderIndex, bool IsActive);

public sealed record HolidayDto(DateOnly Date, string? Description);

/// <summary>No <c>rowVersion</c>, and that is deliberate — see the header. Ignore any version a client
/// sends for an entry.</summary>
public sealed record StandupEntryDto(
    int Id, int UserId, DateOnly WorkDate, string Section,
    int? BacklogId, string BacklogCode, string TaskText, string Description,
    DateOnly? Deadline, string Status, int OrderIndex, int? TeamId);

// ---- Mapping ------------------------------------------------------------------------------------------

/// <summary>Entity -> wire. One place, so four parallel agents cannot each invent a different JSON shape
/// for the same row.</summary>
public static class DtoMappings
{
    public static UserDto ToDto(this User u) =>
        new(u.Id, u.Name, u.WindowsUsername, u.IsActive, u.IsAdmin, u.RowVersion);

    public static TeamDto ToDto(this Team t) =>
        new(t.Id, t.Name, t.IsActive, t.CreatedAt, t.RowVersion);

    public static BacklogDto ToDto(this Backlog b) =>
        new(b.Id, b.BacklogCode, b.Project, b.CreatedAt,
            b.StartDate, b.EndDate, b.PeriodMonth, b.Type,
            b.AssigneeUserId, b.DeadlineInternal, b.DeadlineExternal,
            b.RoughEstimateHours, b.OfficialEstimateHours,
            b.ProgressPercent, b.Note, b.PcaContactId, b.TeamId,
            b.RowVersion);

    public static TaskItemDto ToDto(this TaskItem t) =>
        new(t.Id, t.BacklogId, t.TaskName, t.OrderIndex, t.IsActive,
            t.Status, t.Type, t.AssigneeUserId, t.RowVersion);

    public static TimeLogDto ToDto(this TimeLog l) =>
        new(l.Id, l.UserId, l.TaskId, l.WorkDate, l.Hours, l.RowVersion);

    public static TagDto ToDto(this Tag t) =>
        new(t.Id, t.Text, t.Icon, t.Color, t.RowVersion);

    public static PcaContactDto ToDto(this PcaContact p) =>
        new(p.Id, p.Name, p.IsActive, p.RowVersion);

    public static StandupIssueDto ToDto(this StandupIssue i) =>
        new(i.Id, i.EntryId, i.IssueText, i.SolutionText, i.Status, i.OrderIndex, i.RowVersion);

    public static TaskTemplateDto ToDto(this TaskTemplate t) =>
        new(t.Id, t.TemplateName, t.TaskName, t.OrderIndex);

    public static DefaultTaskDto ToDto(this DefaultTask d) =>
        new(d.Id, d.TaskName, d.OrderIndex, d.IsActive);

    public static HolidayDto ToDto(this Holiday h) =>
        new(h.Date, h.Description);

    public static StandupEntryDto ToDto(this StandupEntry e) =>
        new(e.Id, e.UserId, e.WorkDate, e.Section, e.BacklogId, e.BacklogCode,
            e.TaskText, e.Description, e.Deadline, e.Status, e.OrderIndex, e.TeamId);

    /// <summary>Read-model -> wire. The two entity collections are the only things that actually change
    /// shape; everything else is copied across unchanged.</summary>
    public static TaskListRowDto ToDto(this TaskListRow r) =>
        new(r.BacklogId, r.BacklogCode, r.Project, r.Type, r.PctAssigneeName, r.PcaContactName,
            r.AssigneeUserId, r.PcaContactId,
            r.DeadlineInternal, r.DeadlineExternal, r.StartDate, r.EndDate,
            r.ProgressPercent, r.LoggedHours, r.EstimateHours, r.ScheduleState,
            r.Tags.Select(t => t.ToDto()).ToList(),
            r.Tasks.Select(t => t.ToDto()).ToList(),
            r.TeamId);

    public static TaskListScreenDto ToDto(this TaskListScreen s) =>
        new(s.Rows.Select(r => r.ToDto()).ToList(), s.Gantt);
}
