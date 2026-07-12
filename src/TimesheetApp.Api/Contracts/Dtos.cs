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
}
