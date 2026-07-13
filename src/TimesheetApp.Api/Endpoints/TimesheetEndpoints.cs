using Microsoft.AspNetCore.Mvc;
using TimesheetApp.Api.Contracts;
using TimesheetApp.Api.Infrastructure;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.Services;

namespace TimesheetApp.Api.Endpoints;

/// <summary>W2-B. Owns <c>/api/timesheet/*</c> · <c>/api/smartfill/*</c> · <c>/api/reports/*</c> ·
/// <c>/api/export/*</c>.
///
/// <para><b>Call <c>ITimeLogService</c>, NEVER <c>ITimeLogRepository</c>, for WRITES.</b> The service holds
/// the 8h/day cap, the holiday guard, the weekend guard and the 1-decimal rounding. Go around it and a user
/// logs 40 hours on a Sunday holiday. Use <c>SaveCellCheckedAsync</c> / <c>ClearCellCheckedAsync</c> /
/// <c>ApplySmartFillAsync</c> — the unchecked overloads are bump-only and silently win every race.
/// <c>ITimeLogRepository</c> is still injected directly for pure READS that carry no business rule (week
/// grid via the service's own internal calls aside, reports/export and the post-Smart-Fill version
/// re-fetch below) — exactly the split <c>ReportsViewModel</c> itself uses, injecting both side by side.</para>
///
/// <para><b>The actor is <c>IClientContext.UserId</c>, never a <c>userId</c> from the request body.</b>
/// <c>SaveCellCheckedAsync</c> takes <c>userId</c> as a parameter; binding it from the wire lets any user
/// log hours as any other user. WPF's Timesheet tab lets a user pick ANY colleague as the edit target
/// (<c>TimesheetViewModel.EffectiveUserId</c>) and that was safe there; on the wire it is a raw int, so
/// that capability is deliberately NOT ported — every write below is always <c>ctx.UserId</c>.</para>
///
/// <para><b>Every <c>taskId</c> on the wire is team-checked before any read or write against it</b>
/// (<c>AuthorizedTaskTeamIdAsync</c>) — <c>SaveCellCheckedAsync</c>/<c>ApplySmartFillAsync</c> validate
/// hours/weekday/holiday/8h-cap but never verify the task belongs to the caller's team.</para>
///
/// <para><b><c>ExportFilter.TeamIds</c> is a trailing optional defaulting to <c>null</c>, and <c>null</c>
/// means NO FILTER — every team, every user.</b> The 4-arg positional ctor every WPF call site uses
/// compiles, looks complete, and exports the whole company. Always pass
/// <c>TeamIds: client ∩ ctx.MemberTeamIds</c> via the named argument.</para>
///
/// <para><b>Smart Fill's own write bumps every cell it touches but hands back no versions</b> (its result
/// is a bare <c>SaveResult</c>), and rule #7 excludes the caller from the SignalR echo — so the person who
/// just ran Smart Fill is the one person who never learns the new versions, and their next inline edit
/// would 409 against their own result. <c>/api/smartfill/apply</c> therefore re-fetches the affected date
/// range via <c>ITimeLogRepository.GetByUserAndRangeAsync</c> (a plain read, not the racy read-after-write
/// rule #3 forbids) and returns the fresh <c>TimeLogDto</c>s, each carrying its new <c>rowVersion</c>.</para></summary>
public static class TimesheetEndpoints
{
    // Duplicated from the shared "N days without a log" setting key (WPF ReportsViewModel.NDaysKey /
    // SET-02, default 3). The WPF ViewModel that owns the constant lives in TimesheetApp (net8.0-windows),
    // which the API (net8.0) cannot reference -- the same cross-project constraint Wave 1 hit with
    // ICurrentTeamService. ISettingsRepository itself is a DB-backed key/value store (not the IAppConfig
    // singleton rule #9 forbids), so reading it per-request is safe.
    private const string MissingLogsNDaysKey = "chua_log_n_days";
    private const int DefaultMissingLogsNDays = 3;

    public static IEndpointRouteBuilder MapTimesheetEndpoints(this IEndpointRouteBuilder api)
    {
        // ==== Timesheet: week grid + checked cell writes ================================================

        // Self view (default) mirrors WPF's own login-user default; the read-only team aggregate
        // (?allUsers=true) mirrors WPF's IsTeamView. Both GetWeekGrouped* methods are already scoped to
        // ICurrentTeamService.ActiveTeamId INTERNALLY (resolved per-request in Wave 1) -- no client-
        // supplied team id is accepted here, so there is nothing further to authorize on this route.
        // Deliberately does NOT accept a userId query param: unlike WPF's EntryTarget picker, the web API
        // never lets one user view another named user's individual grid -- only their own, or the
        // already-aggregated (and read-only) team view.
        api.MapGet("/api/timesheet/week", async (
            [FromQuery] DateOnly monday,
            [FromQuery] bool? allUsers,
            IClientContext ctx,
            ITimeLogService logs) =>
        {
            var groups = allUsers == true
                ? await logs.GetWeekGroupedAllUsersAsync(monday)
                : await logs.GetWeekGroupedAsync(ctx.UserId, monday);
            return Results.Ok(groups);
        })
        .WithName("TimesheetWeek")
        .WithTags("Timesheet")
        // M8.4/W2: WITHOUT THIS the OpenAPI document describes NO response body for this route, and the
        // generated TypeScript client therefore has NO WeekCell and NO rowVersion -- the milestone's entire
        // read path. `Results.Ok(x)` is typed `IResult`, and ApiExplorer CANNOT infer a schema from it: it
        // emits a bare `"200": { "description": "OK" }` with no content. The RUNTIME was always correct (see
        // The_week_read_JSON_exposes_a_per_cell_rowVersion_field) -- only the DOCUMENT was silent, which is
        // the more dangerous failure, because codegen succeeds and produces a client typed `any`.
        .Produces<IReadOnlyList<WeekBacklogGroup>>();

        api.MapPut("/api/timesheet/cell", async (
            [FromBody] TimesheetSaveCellRequest req,
            IClientContext ctx,
            ITaskRepository tasks,
            IBacklogRepository backlogs,
            ITimeLogService logs,
            IChangeNotifier notifier) =>
        {
            var teamId = await AuthorizedTaskTeamIdAsync(req.TaskId, tasks, backlogs, ctx);
            if (teamId is null) return Results.NotFound();

            // THE ACTOR IS THE COOKIE, NEVER THE BODY (rule #8) -- ExpectedVersion is passed through
            // UNNORMALIZED: null legitimately asserts "I believe this cell is empty" (rule #2).
            var result = await logs.SaveCellCheckedAsync(
                ctx.UserId, req.TaskId, req.Date, req.Hours, req.ExpectedVersion);

            // The business-rule channel is a RETURN VALUE, not an exception (rule #6): ignoring Ok would
            // return 200 on a rejected write and the user watches their hours vanish. A version conflict
            // (ConcurrencyConflictException) is LET THROUGH to ExceptionMapper -> 409.
            if (!result.Ok) return Results.BadRequest(new ValidationBody(result.Error!));

            await notifier.DataChangedAsync(DataKind.Logs, teamId.Value, ctx.ConnectionId);
            // Hand back the version the checked call returned -- never re-read it (rule #3).
            return Results.Ok(new SavedBody(result.RowVersion));
        })
        .WithName("TimesheetSaveCell")
        .WithTags("Timesheet")
        // The write path's four outcomes, all four of which the client MUST discriminate: the new version
        // (SavedBody) is the caller's next expectedVersion; 400 carries the 8h-cap/holiday message; 404 is a
        // deleted or out-of-team task; 409 is the conflict dialog. Undocumented, the client sees only `void`.
        .Produces<SavedBody>()
        .Produces<ValidationBody>(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound)
        .Produces<ConflictBody>(StatusCodes.Status409Conflict);

        api.MapDelete("/api/timesheet/cell", async (
            [FromBody] TimesheetClearCellRequest req,
            IClientContext ctx,
            ITaskRepository tasks,
            IBacklogRepository backlogs,
            ITimeLogService logs,
            IChangeNotifier notifier) =>
        {
            var teamId = await AuthorizedTaskTeamIdAsync(req.TaskId, tasks, backlogs, ctx);
            if (teamId is null) return Results.NotFound();

            // Clearing a cell that already moved on, or is already gone, is itself a conflict --
            // ConcurrencyConflictException is LET THROUGH to ExceptionMapper -> 409 (both `deleted` cases
            // are reachable here: TimeLogs has no id-based pre-check to shadow them, unlike Backlogs/Tasks).
            await logs.ClearCellCheckedAsync(ctx.UserId, req.TaskId, req.Date, req.ExpectedVersion);

            await notifier.DataChangedAsync(DataKind.Logs, teamId.Value, ctx.ConnectionId);
            return Results.NoContent();
        })
        .WithName("TimesheetClearCell")
        .WithTags("Timesheet")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status404NotFound)
        .Produces<ConflictBody>(StatusCodes.Status409Conflict);

        // ==== Smart Fill ==================================================================================

        api.MapPost("/api/smartfill/validate", async (
            [FromBody] SmartFillRequest req,
            IClientContext ctx,
            ITaskRepository tasks,
            IBacklogRepository backlogs,
            ITimeLogService logs) =>
        {
            var teamIds = await AuthorizedTaskTeamIdsAsync(req.Tasks, tasks, backlogs, ctx);
            if (teamIds is null) return Results.NotFound();

            var result = await logs.ValidateSmartFillAsync(ctx.UserId, ToDomainTasks(req.Tasks));
            return result.Ok ? Results.Ok() : Results.BadRequest(new ValidationBody(result.Error!));
        })
        .WithName("SmartFillValidate")
        .WithTags("SmartFill")
        // Success is deliberately an EMPTY 200 -- validate answers "may I?", it returns no cells.
        .Produces(StatusCodes.Status200OK)
        .Produces<ValidationBody>(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound);

        api.MapPost("/api/smartfill/apply", async (
            [FromBody] SmartFillRequest req,
            IClientContext ctx,
            ITaskRepository tasks,
            IBacklogRepository backlogs,
            ITimeLogService logs,
            ITimeLogRepository rawLogs,
            IChangeNotifier notifier) =>
        {
            var teamIds = await AuthorizedTaskTeamIdsAsync(req.Tasks, tasks, backlogs, ctx);
            if (teamIds is null) return Results.NotFound();

            var domainTasks = ToDomainTasks(req.Tasks);
            var result = await logs.ApplySmartFillAsync(ctx.UserId, domainTasks);
            if (!result.Ok) return Results.BadRequest(new ValidationBody(result.Error!));

            foreach (var teamId in teamIds)
                await notifier.DataChangedAsync(DataKind.Logs, teamId, ctx.ConnectionId);

            // Rule #3 carve-out: ApplySmartFillAsync bumps every written cell's row_version but returns
            // none, and the caller is excluded from its own SignalR echo (rule #7) -- so without this, the
            // one person who just ran Smart Fill is the one person who never learns the new versions, and
            // their next inline edit 409s against their own result, on the happy path, every time. This is
            // a plain re-fetch of a coherent (state, version) pair -- not the racy read-after-write rule #3
            // forbids, because nothing here is fed back into a FUTURE expectedVersion blindly; the client
            // reads this response and uses it as ITS OWN next expectedVersion, same as any GET would.
            //
            // `dates` is never empty when result.Ok: ApplySmartFillAsync's first step is
            // ValidateSmartFillAsync, whose first check rejects an empty (task, cell>0h) set -- the exact
            // filter used below -- so Ok=true guarantees at least one date here.
            var dates = domainTasks.SelectMany(t => t.Cells).Where(c => c.Hours > 0m).Select(c => c.Date).ToList();
            var refreshed = await rawLogs.GetByUserAndRangeAsync(ctx.UserId, dates.Min(), dates.Max());
            return Results.Ok(refreshed.Select(l => l.ToDto()).ToList());
        })
        .WithName("SmartFillApply")
        .WithTags("SmartFill")
        // A FLAT TimeLogDto[], NOT a week grid, and it spans only min..max OF THE FILLED DATES. The client
        // MERGES it into the grid by (taskId, workDate); replacing grid state from it would wipe the days the
        // fill did not touch, and a flat list cannot reconstruct the backlog grouping anyway.
        .Produces<IReadOnlyList<TimeLogDto>>()
        .Produces<ValidationBody>(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status404NotFound);

        // ==== Reports ======================================================================================

        api.MapGet("/api/reports/weekly", async (
            [FromQuery] DateOnly monday,
            [FromQuery] int? userId,
            [FromQuery] string? project,
            HttpContext http,
            IClientContext ctx,
            ITimeLogRepository logs,
            IHolidayRepository holidays,
            IReportAggregator aggregator) =>
        {
            var rows = await RowsForTargetAsync(logs, http, ctx, userId, monday, monday.AddDays(4), project);

            var dayTotals = aggregator.WeeklyDayTotals(rows);
            // HOL-02: a holiday is not a working day, so it must not inflate the DAYS LOGGED denominator.
            var holidaySet = (await holidays.GetAllAsync()).Select(h => h.Date).ToHashSet();
            var stat = aggregator.DaysLogged(dayTotals, monday, holidaySet);

            return Results.Ok(new TimesheetWeeklyReportResponse(dayTotals, aggregator.WeeklyDetailRows(rows), stat));
        });

        api.MapGet("/api/reports/monthly", async (
            [FromQuery] int year,
            [FromQuery] int month,
            [FromQuery] int? userId,
            [FromQuery] string? project,
            HttpContext http,
            IClientContext ctx,
            ITimeLogRepository logs,
            IReportAggregator aggregator) =>
        {
            var first = new DateOnly(year, month, 1);
            var rows = await RowsForTargetAsync(logs, http, ctx, userId, first, first.AddMonths(1).AddDays(-1), project);

            return Results.Ok(new TimesheetMonthlyReportResponse(
                aggregator.MonthlyBacklogTaskTotals(rows), aggregator.BuildProjectTree(rows)));
        });

        // RPT-04. Scoped to ctx's active team INTERNALLY by GetUsersMissingLogsAsync -- no client team id
        // accepted here. N is the shared app-wide setting (SET-02), read server-side and never client-
        // supplied (an attacker could otherwise request an arbitrarily large scan window).
        api.MapGet("/api/reports/missing-logs", async (
            ISettingsRepository settings,
            ITimeLogService logs) =>
        {
            var raw = await settings.GetAsync(MissingLogsNDaysKey);
            var n = int.TryParse(raw, out var parsed) && parsed > 0 ? parsed : DefaultMissingLogsNDays;

            var missing = await logs.GetUsersMissingLogsAsync(n);
            return Results.Ok(missing.Select(u => new MissingLogWarning(u.Name)).ToList());
        });

        // ==== Export =======================================================================================
        // R6 (rule #5): GetExportRowsAsync has NO userId parameter at all, and ExportFilter.TeamIds is a
        // TRAILING OPTIONAL defaulting to null -- forgetting it, or using the 4-arg ctor every WPF call
        // site uses, exports the WHOLE COMPANY. Always the full 5-arg ctor with a non-null TeamIds.

        api.MapGet("/api/export/excel", async (
            [FromQuery] int year, [FromQuery] int month,
            [FromQuery] int? userId, [FromQuery] string? project,
            HttpContext http, IClientContext ctx, IExportService export) =>
        {
            var filter = new ExportFilter(userId, year, month, project, TeamIds: EffectiveTeamIds(http, ctx));
            var bytes = await export.ExportExcelAsync(filter);
            return Results.File(bytes,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"Worklog-{year:D4}-{month:D2}.xlsx");
        });

        api.MapGet("/api/export/markdown", async (
            [FromQuery] int year, [FromQuery] int month,
            [FromQuery] int? userId, [FromQuery] string? project,
            HttpContext http, IClientContext ctx, IExportService export) =>
        {
            var filter = new ExportFilter(userId, year, month, project, TeamIds: EffectiveTeamIds(http, ctx));
            var markdown = await export.ExportMarkdownAsync(filter);
            return Results.Text(markdown, "text/markdown");
        });

        return api;
    }

    // ==== helpers ==========================================================================================

    // Mirrors ReportsViewModel.GetRowsForTargetAsync: whole-team (userId absent) uses the all-users export
    // query; a specific target uses the per-user report query. Both take the SAME R6-safe teamIds, and the
    // project filter is applied the same way ReportsViewModel applies it -- client-side, because
    // GetReportRowsAsync (unlike GetExportRowsAsync) has no project parameter of its own.
    private static async Task<IReadOnlyList<TimeLogReportRow>> RowsForTargetAsync(
        ITimeLogRepository logs, HttpContext http, IClientContext ctx,
        int? userId, DateOnly from, DateOnly to, string? project)
    {
        var effectiveTeamIds = EffectiveTeamIds(http, ctx);
        IReadOnlyList<TimeLogReportRow> rows = userId is int uid
            ? await logs.GetReportRowsAsync(uid, from, to, effectiveTeamIds)
            : await logs.GetExportRowsAsync(from, to, null, effectiveTeamIds);

        return string.IsNullOrEmpty(project) ? rows : rows.Where(r => r.Project == project).ToList();
    }

    private static IReadOnlyList<SmartFillTask> ToDomainTasks(IReadOnlyList<SmartFillTaskRequest> tasks) =>
        tasks.Select(t => new SmartFillTask(
                t.TaskId, t.Cells.Select(c => new CellAssignment(c.Date, c.Hours)).ToList()))
            .ToList();

    // Rule #8: task id is never team-checked by anything else -- SaveCellCheckedAsync/ApplySmartFillAsync
    // validate hours/weekday/holiday/8h-cap but not team membership. Returns null (-> 404, not 403) so a
    // probe cannot distinguish "not yours" from "doesn't exist".
    private static async Task<int?> AuthorizedTaskTeamIdAsync(
        int taskId, ITaskRepository tasks, IBacklogRepository backlogs, IClientContext ctx)
    {
        var task = await tasks.GetByIdAsync(taskId);
        if (task is null) return null;

        var backlog = await backlogs.GetByIdAsync(task.BacklogId);
        if (backlog is null || backlog.TeamId is not { } teamId || !ctx.MemberTeamIds.Contains(teamId))
            return null;

        return teamId;
    }

    // Team-checks EVERY distinct task id in a Smart Fill request. Returns the DISTINCT team ids touched
    // (so a notification goes out to every affected team -- a single fill is not guaranteed single-team on
    // the wire even though the WPF panel only ever offers same-team tasks), or null if ANY task fails
    // authorization -- nothing is validated or applied when even one task is not the caller's.
    private static async Task<IReadOnlyList<int>?> AuthorizedTaskTeamIdsAsync(
        IReadOnlyList<SmartFillTaskRequest> requestTasks,
        ITaskRepository tasks, IBacklogRepository backlogs, IClientContext ctx)
    {
        var teamIds = new HashSet<int>();
        foreach (var taskId in requestTasks.Select(t => t.TaskId).Distinct())
        {
            var teamId = await AuthorizedTaskTeamIdAsync(taskId, tasks, backlogs, ctx);
            if (teamId is null) return null;
            teamIds.Add(teamId.Value);
        }
        return teamIds.ToList();
    }

    // Rule #5 / R6: client teamIds INTERSECTED with ctx.MemberTeamIds; absent defaults to
    // ctx.MemberTeamIds; NEVER null (null means "every team" to GetReportRowsAsync/GetExportRowsAsync).
    // Reads HttpContext.Request.Query directly rather than a bound `int[]? teamIds` parameter: minimal-API
    // array binding cannot tell "key absent" apart from "key present with zero values" -- both produce an
    // EMPTY array, never null, so a bound parameter's `is null` check is always false and "absent" would
    // fall into the intersect branch, producing an empty set instead of the required default (every one of
    // the caller's own teams).
    private static IReadOnlyList<int> EffectiveTeamIds(HttpContext http, IClientContext ctx)
    {
        if (!http.Request.Query.TryGetValue("teamIds", out var raw))
            return ctx.MemberTeamIds;

        var clientTeamIds = raw
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => int.TryParse(s, out var v) ? v : (int?)null)
            .Where(v => v.HasValue)
            .Select(v => v!.Value);

        return clientTeamIds.Intersect(ctx.MemberTeamIds).ToList();
    }
}

// ==== Request / response DTOs ===========================================================================
// Prefixed with the owning sub-area (Timesheet.../SmartFill...) -- all four Wave-2 endpoint files share
// this namespace, so an unprefixed `SaveCellRequest` in two of them would be a duplicate-type compile
// error found only at the merge.

/// <summary>UserId is deliberately ABSENT (rule #8): the actor is always <c>IClientContext.UserId</c>.
/// <c>ExpectedVersion</c> is nullable BY DESIGN (rule #2) -- null asserts "I believe this cell is
/// empty".</summary>
public sealed record TimesheetSaveCellRequest(int TaskId, DateOnly Date, decimal Hours, long? ExpectedVersion);

/// <summary>UserId is deliberately ABSENT (rule #8) -- see <see cref="TimesheetSaveCellRequest"/>. Unlike a
/// save, a clear has no "I believe it's empty" case, so ExpectedVersion here is NOT nullable.</summary>
public sealed record TimesheetClearCellRequest(int TaskId, DateOnly Date, long ExpectedVersion);

public sealed record SmartFillCellRequest(DateOnly Date, decimal Hours);

/// <summary>UserId is deliberately ABSENT (rule #8), same as the single-cell requests.</summary>
public sealed record SmartFillTaskRequest(int TaskId, IReadOnlyList<SmartFillCellRequest> Cells);

public sealed record SmartFillRequest(IReadOnlyList<SmartFillTaskRequest> Tasks);

/// <summary>RPT-01 weekly view: day totals + the (date, backlog, task) detail rows behind them, plus the
/// "N / M working days logged" stat -- bundled into one response so the client does not make three
/// round-trips (and risk seeing three different snapshots) for what is conceptually one report load.</summary>
public sealed record TimesheetWeeklyReportResponse(
    IReadOnlyList<WeeklyDayTotal> DayTotals,
    IReadOnlyList<WeeklyDetailRow> DetailRows,
    DaysLoggedStat DaysLogged);

/// <summary>RPT-02/03 monthly view: the flat (backlog, task) totals plus the Team -> Project -> Backlog ->
/// Task -> Date drill-down tree, built from the same underlying rows.</summary>
public sealed record TimesheetMonthlyReportResponse(
    IReadOnlyList<MonthlyBacklogTaskTotal> MonthlyTotals,
    IReadOnlyList<TeamNode> ProjectTree);
