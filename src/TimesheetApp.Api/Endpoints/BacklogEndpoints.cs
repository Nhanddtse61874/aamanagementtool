using Microsoft.AspNetCore.Mvc;
using TimesheetApp.Api.Contracts;
using TimesheetApp.Api.Infrastructure;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.Services;

namespace TimesheetApp.Api.Endpoints;

/// <summary>W2-C. Owns <c>/api/backlogs/*</c> · <c>/api/tasks/*</c>, INCLUDING tag ASSIGNMENT
/// (<c>PUT /api/backlogs/{id}/tags</c>). Tag CRUD (<c>/api/tags/*</c>) belongs to W2-D — mapping it here
/// too is an <c>AmbiguousMatchException</c>, i.e. HTTP 500 on every tags request, found only at the merge.
///
/// <para><b>A whole-record update overwrites EVERY column, including <c>team_id</c>.</b>
/// <c>UpdateCheckedAsync(Backlog, …)</c> writes all 15 fields, so a DTO that merely OMITS <c>teamId</c> maps
/// to <c>TeamId = null</c> — <c>team_id = NULL</c> — and the backlog drops out of every team and becomes
/// invisible to everyone, permanently, while every test still passes. Re-read the stored entity, apply only
/// the request's fields with <c>with { … }</c>, and never let <c>TeamId</c> come from the wire.</para>
///
/// <para><b>Audited writes must name the editor:</b> <c>UpdateCheckedAsync</c>, <c>UpdateStatusCheckedAsync</c>,
/// <c>SetTagsCheckedAsync</c>, <c>UpdateExtendedCheckedAsync</c> and <c>SetTaskTagsCheckedAsync</c> all take
/// <c>changedByUserId</c> / <c>changedByName</c> as OPTIONAL params defaulting to <c>null</c>. Omit them and
/// it compiles, the tests pass, and every web edit writes an anonymous audit row.</para>
///
/// <para><b>"Continue to next month" is <c>IBacklogContinuationService.ContinueAsync</c></b> — it copies
/// tags, copies not-Done tasks and writes the <c>continued</c> audit row. A raw INSERT does none of that.</para>
///
/// <para><b>There is no <c>DELETE /api/backlogs/{id}</c>.</b> <c>IBacklogRepository</c> has no delete at all
/// (recorded decision: backlogs are not soft-deletable). Do not add one.</para></summary>
public static class BacklogEndpoints
{
    public static IEndpointRouteBuilder MapBacklogEndpoints(this IEndpointRouteBuilder api)
    {
        // ==== Backlogs ==================================================================================

        // R6: client teamIds (attacker-controlled) INTERSECTED with ctx.MemberTeamIds; absent defaults to
        // ctx.MemberTeamIds. NEVER passes null to SearchAsync (null there means "every team").
        api.MapGet("/api/backlogs", async (
            [FromQuery] string? term,
            HttpContext http,
            IClientContext ctx,
            IBacklogRepository backlogs) =>
        {
            var effectiveTeamIds = EffectiveTeamIds(http, ctx);
            var found = await backlogs.SearchAsync(term, effectiveTeamIds);
            return Results.Ok(found.Select(b => b.ToDto()).ToList());
        });

        api.MapGet("/api/backlogs/{id}", async (
            int id, IClientContext ctx, IBacklogRepository backlogs) =>
        {
            var authorized = await AuthorizedBacklogAsync(id, backlogs, ctx);
            return authorized is null ? Results.NotFound() : Results.Ok(authorized.Value.Backlog.ToDto());
        })
            .WithName("BacklogGet")
            .WithTags("Backlogs")
            .Produces<BacklogDto>()
            .Produces(StatusCodes.Status404NotFound);

        // A new backlog belongs to the caller's ACTIVE team, never a team read off the wire -- mirrors
        // RequestEditorViewModel: `TeamId: _currentTeam?.ActiveTeamId is { } tid and > 0 ? tid : null`.
        api.MapPost("/api/backlogs", async (
            [FromBody] BacklogCreateRequest req,
            IClientContext ctx,
            ICurrentTeamService currentTeam,
            IBacklogRepository backlogs,
            IChangeNotifier notifier,
            IClock clock) =>
        {
            if (string.IsNullOrWhiteSpace(req.BacklogCode) || string.IsNullOrWhiteSpace(req.Project))
                return Results.BadRequest(new ValidationBody("BacklogCode and Project are required."));

            int? teamId = currentTeam.ActiveTeamId > 0 ? currentTeam.ActiveTeamId : null;

            var toInsert = new Backlog(0, req.BacklogCode.Trim(), req.Project, clock.UtcNow,
                req.StartDate, req.EndDate, req.PeriodMonth, req.Type, req.AssigneeUserId,
                req.DeadlineInternal, req.DeadlineExternal, req.RoughEstimateHours, req.OfficialEstimateHours,
                req.ProgressPercent, req.Note, req.PcaContactId, teamId);

            var newId = await backlogs.InsertAsync(toInsert);

            // Re-read to get the server-assigned CreatedAt/RowVersion. This is NOT the racy re-read rule
            // #3 forbids: that rule guards against re-reading a version AFTER a checked write to feed a
            // FUTURE expectedVersion, where another writer could sneak in between commit and read. Here
            // the id was only just generated and handed to nobody else yet, so nothing else can be
            // targeting this row between the INSERT and this SELECT.
            var created = await backlogs.GetByIdAsync(newId);

            if (teamId is { } tid) await notifier.DataChangedAsync(DataKind.Backlogs, tid, ctx.ConnectionId);

            return Results.Ok(created!.ToDto());
        });

        api.MapPut("/api/backlogs/{id}", async (
            int id,
            [FromBody] BacklogUpdateRequest req,
            IClientContext ctx,
            IBacklogRepository backlogs,
            IChangeNotifier notifier) =>
        {
            if (string.IsNullOrWhiteSpace(req.BacklogCode) || string.IsNullOrWhiteSpace(req.Project))
                return Results.BadRequest(new ValidationBody("BacklogCode and Project are required."));

            var authorized = await AuthorizedBacklogAsync(id, backlogs, ctx);
            if (authorized is null) return Results.NotFound();
            var (existing, teamId) = authorized.Value;

            // Rule #8: re-read + `with{}` only the DTO's own fields. BacklogUpdateRequest has NO TeamId
            // property at all -- there is nothing on the wire that could ever null it out.
            var updated = existing with
            {
                BacklogCode = req.BacklogCode.Trim(),
                Project = req.Project,
                StartDate = req.StartDate,
                EndDate = req.EndDate,
                PeriodMonth = req.PeriodMonth,
                Type = req.Type,
                AssigneeUserId = req.AssigneeUserId,
                DeadlineInternal = req.DeadlineInternal,
                DeadlineExternal = req.DeadlineExternal,
                RoughEstimateHours = req.RoughEstimateHours,
                OfficialEstimateHours = req.OfficialEstimateHours,
                ProgressPercent = req.ProgressPercent,
                Note = req.Note,
                PcaContactId = req.PcaContactId,
            };

            // Rule #4: name the editor, or every web edit writes an anonymous audit row.
            var newVersion = await backlogs.UpdateCheckedAsync(updated, req.ExpectedVersion,
                changedByUserId: ctx.UserId, changedByName: ctx.UserName, auditNote: req.AuditNote);

            await notifier.DataChangedAsync(DataKind.Backlogs, teamId, ctx.ConnectionId);
            return Results.Ok(new SavedBody(newVersion));
        })
            .WithName("BacklogUpdate")
            .WithTags("Backlogs")
            .Produces<SavedBody>()
            .Produces<ValidationBody>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status404NotFound)
            .Produces<ConflictBody>(StatusCodes.Status409Conflict);

        api.MapGet("/api/backlogs/{id}/tags", async (
            int id, IClientContext ctx, IBacklogRepository backlogs) =>
        {
            var authorized = await AuthorizedBacklogAsync(id, backlogs, ctx);
            if (authorized is null) return Results.NotFound();
            return Results.Ok(await backlogs.GetTagIdsAsync(id));
        });

        api.MapPut("/api/backlogs/{id}/tags", async (
            int id,
            [FromBody] BacklogTagsRequest req,
            IClientContext ctx,
            IBacklogRepository backlogs,
            IChangeNotifier notifier) =>
        {
            var authorized = await AuthorizedBacklogAsync(id, backlogs, ctx);
            if (authorized is null) return Results.NotFound();
            var (_, teamId) = authorized.Value;

            var newVersion = await backlogs.SetTagsCheckedAsync(id, req.TagIds, req.ExpectedVersion,
                changedByUserId: ctx.UserId, changedByName: ctx.UserName);

            await notifier.DataChangedAsync(DataKind.Backlogs, teamId, ctx.ConnectionId);
            return Results.Ok(new SavedBody(newVersion));
        });

        // P20, rule #1: MUST go through the service -- it copies tags, copies not-Done tasks and writes
        // the 'continued' audit row. A raw INSERT here would do none of that.
        api.MapPost("/api/backlogs/{id}/continue", async (
            int id,
            [FromBody] BacklogContinueRequest req,
            IClientContext ctx,
            IBacklogRepository backlogs,
            IBacklogContinuationService continuation,
            IChangeNotifier notifier) =>
        {
            if (string.IsNullOrWhiteSpace(req.TargetPeriod))
                return Results.BadRequest(new ValidationBody("TargetPeriod is required."));

            var authorized = await AuthorizedBacklogAsync(id, backlogs, ctx);
            if (authorized is null) return Results.NotFound();
            var (_, teamId) = authorized.Value;

            var newId = await continuation.ContinueAsync(id, req.TargetPeriod);
            if (newId == 0)
                return Results.BadRequest(new ValidationBody(
                    $"A backlog with this code already exists in {req.TargetPeriod}."));

            var created = await backlogs.GetByIdAsync(newId);

            await notifier.DataChangedAsync(DataKind.Backlogs, teamId, ctx.ConnectionId);
            await notifier.DataChangedAsync(DataKind.Tasks, teamId, ctx.ConnectionId);

            return Results.Ok(created!.ToDto());
        });

        // ==== Tasks ======================================================================================

        api.MapGet("/api/tasks", async (
            [FromQuery] int backlogId,
            IClientContext ctx,
            IBacklogRepository backlogs,
            ITaskRepository tasks) =>
        {
            var authorized = await AuthorizedBacklogAsync(backlogId, backlogs, ctx);
            if (authorized is null) return Results.NotFound();

            var found = await tasks.GetActiveByBacklogAsync(backlogId);
            return Results.Ok(found.Select(t => t.ToDto()).ToList());
        });

        api.MapGet("/api/tasks/{id}", async (
            int id, IClientContext ctx, IBacklogRepository backlogs, ITaskRepository tasks) =>
        {
            var authorized = await AuthorizedTaskAsync(id, tasks, backlogs, ctx);
            return authorized is null ? Results.NotFound() : Results.Ok(authorized.Value.Task.ToDto());
        });

        api.MapPost("/api/tasks", async (
            [FromBody] TaskCreateRequest req,
            IClientContext ctx,
            IBacklogRepository backlogs,
            ITaskRepository tasks,
            IChangeNotifier notifier) =>
        {
            if (string.IsNullOrWhiteSpace(req.TaskName))
                return Results.BadRequest(new ValidationBody("TaskName is required."));

            // req.BacklogId is attacker-controlled -- team-check it BEFORE creating anything under it.
            var authorized = await AuthorizedBacklogAsync(req.BacklogId, backlogs, ctx);
            if (authorized is null) return Results.NotFound();
            var (_, teamId) = authorized.Value;

            var newId = await tasks.InsertAsync(
                new TaskItem(0, req.BacklogId, req.TaskName.Trim(), req.OrderIndex, true));
            var created = await tasks.GetByIdAsync(newId);   // server-assigned RowVersion; see backlog create

            await notifier.DataChangedAsync(DataKind.Tasks, teamId, ctx.ConnectionId);
            return Results.Ok(created!.ToDto());
        })
            .WithName("TaskCreate")
            .WithTags("Tasks")
            .Produces<TaskItemDto>()
            .Produces(StatusCodes.Status404NotFound);

        // Mirrors UpdateCheckedAsync(TaskItem, long): writes task_name + order_index + status together
        // (the "full editor" path). Not audited -- the repository method itself carries no changedBy
        // params, unlike the dedicated /status and /extended endpoints below.
        api.MapPut("/api/tasks/{id}", async (
            int id,
            [FromBody] TaskUpdateRequest req,
            IClientContext ctx,
            IBacklogRepository backlogs,
            ITaskRepository tasks,
            IChangeNotifier notifier) =>
        {
            if (string.IsNullOrWhiteSpace(req.TaskName))
                return Results.BadRequest(new ValidationBody("TaskName is required."));

            var authorized = await AuthorizedTaskAsync(id, tasks, backlogs, ctx);
            if (authorized is null) return Results.NotFound();
            var (existing, _, teamId) = authorized.Value;

            // Rule #8's general pattern (re-read + `with{}`), even though UpdateCheckedAsync(TaskItem,long)
            // only ever writes task_name/order_index/status today -- so a future SQL change can never
            // silently wipe BacklogId/IsActive/Type/AssigneeUserId through this endpoint.
            var updated = existing with
            {
                TaskName = req.TaskName.Trim(),
                OrderIndex = req.OrderIndex,
                Status = req.Status,
            };
            var newVersion = await tasks.UpdateCheckedAsync(updated, req.ExpectedVersion);

            await notifier.DataChangedAsync(DataKind.Tasks, teamId, ctx.ConnectionId);
            return Results.Ok(new SavedBody(newVersion));
        });

        // The "sub-row Status dropdown" path (TaskRepository's own naming) -- audited, unlike the combined
        // editor above.
        api.MapPut("/api/tasks/{id}/status", async (
            int id,
            [FromBody] TaskStatusRequest req,
            IClientContext ctx,
            IBacklogRepository backlogs,
            ITaskRepository tasks,
            IChangeNotifier notifier) =>
        {
            if (string.IsNullOrWhiteSpace(req.Status))
                return Results.BadRequest(new ValidationBody("Status is required."));

            var authorized = await AuthorizedTaskAsync(id, tasks, backlogs, ctx);
            if (authorized is null) return Results.NotFound();
            var (_, _, teamId) = authorized.Value;

            var newVersion = await tasks.UpdateStatusCheckedAsync(id, req.Status, req.ExpectedVersion,
                changedByUserId: ctx.UserId, changedByName: ctx.UserName);

            await notifier.DataChangedAsync(DataKind.Tasks, teamId, ctx.ConnectionId);
            return Results.Ok(new SavedBody(newVersion));
        });

        api.MapPut("/api/tasks/{id}/extended", async (
            int id,
            [FromBody] TaskExtendedRequest req,
            IClientContext ctx,
            IBacklogRepository backlogs,
            ITaskRepository tasks,
            IChangeNotifier notifier) =>
        {
            var authorized = await AuthorizedTaskAsync(id, tasks, backlogs, ctx);
            if (authorized is null) return Results.NotFound();
            var (_, _, teamId) = authorized.Value;

            var newVersion = await tasks.UpdateExtendedCheckedAsync(
                id, req.Type, req.AssigneeUserId, req.ExpectedVersion,
                changedByUserId: ctx.UserId, changedByName: ctx.UserName);

            await notifier.DataChangedAsync(DataKind.Tasks, teamId, ctx.ConnectionId);
            return Results.Ok(new SavedBody(newVersion));
        });

        api.MapGet("/api/tasks/{id}/tags", async (
            int id, IClientContext ctx, IBacklogRepository backlogs, ITaskRepository tasks) =>
        {
            var authorized = await AuthorizedTaskAsync(id, tasks, backlogs, ctx);
            if (authorized is null) return Results.NotFound();
            return Results.Ok(await tasks.GetTagIdsAsync(id));
        });

        api.MapPut("/api/tasks/{id}/tags", async (
            int id,
            [FromBody] TaskTagsRequest req,
            IClientContext ctx,
            IBacklogRepository backlogs,
            ITaskRepository tasks,
            IChangeNotifier notifier) =>
        {
            var authorized = await AuthorizedTaskAsync(id, tasks, backlogs, ctx);
            if (authorized is null) return Results.NotFound();
            var (_, _, teamId) = authorized.Value;

            var newVersion = await tasks.SetTaskTagsCheckedAsync(id, req.TagIds, req.ExpectedVersion,
                changedByUserId: ctx.UserId, changedByName: ctx.UserName);

            await notifier.DataChangedAsync(DataKind.Tasks, teamId, ctx.ConnectionId);
            return Results.Ok(new SavedBody(newVersion));
        });

        // Rule #9: bump-only BY DESIGN, no *CheckedAsync sibling -- ignore any rowVersion on the DTO
        // (TaskActiveRequest carries none to ignore).
        api.MapPut("/api/tasks/{id}/active", async (
            int id,
            [FromBody] TaskActiveRequest req,
            IClientContext ctx,
            IBacklogRepository backlogs,
            ITaskRepository tasks,
            IChangeNotifier notifier) =>
        {
            var authorized = await AuthorizedTaskAsync(id, tasks, backlogs, ctx);
            if (authorized is null) return Results.NotFound();
            var (_, _, teamId) = authorized.Value;

            await tasks.SetActiveAsync(id, req.IsActive);

            await notifier.DataChangedAsync(DataKind.Tasks, teamId, ctx.ConnectionId);
            return Results.NoContent();
        })
            .WithName("TaskSetActive")
            .WithTags("Tasks")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);

        // Rule #9: bump-only BY DESIGN. SetOrderAsync runs once per row during a drag
        // (TimesheetViewModel.ReorderAsync loops it over the whole list) -- a checked variant would
        // 409-storm an ordinary reorder. Ignore any rowVersion on the DTO.
        api.MapPut("/api/tasks/{id}/order", async (
            int id,
            [FromBody] TaskOrderRequest req,
            IClientContext ctx,
            IBacklogRepository backlogs,
            ITaskRepository tasks,
            IChangeNotifier notifier) =>
        {
            var authorized = await AuthorizedTaskAsync(id, tasks, backlogs, ctx);
            if (authorized is null) return Results.NotFound();
            var (_, _, teamId) = authorized.Value;

            await tasks.SetOrderAsync(id, req.OrderIndex);

            await notifier.DataChangedAsync(DataKind.Tasks, teamId, ctx.ConnectionId);
            return Results.NoContent();
        })
            .WithName("TaskSetOrder")
            .WithTags("Tasks")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);

        return api;
    }

    // ==== Authorization helpers ==========================================================================
    // Rule #8: every id on the wire is attacker-controlled -- team-check the resource BEFORE the call, and
    // return 404 (not 403) on failure so a probe cannot distinguish "not yours" from "doesn't exist".

    private static async Task<(Backlog Backlog, int TeamId)?> AuthorizedBacklogAsync(
        int backlogId, IBacklogRepository backlogs, IClientContext ctx)
    {
        var backlog = await backlogs.GetByIdAsync(backlogId);
        if (backlog is null || backlog.TeamId is not { } teamId || !ctx.MemberTeamIds.Contains(teamId))
            return null;
        return (backlog, teamId);
    }

    private static async Task<(TaskItem Task, Backlog Backlog, int TeamId)?> AuthorizedTaskAsync(
        int taskId, ITaskRepository tasks, IBacklogRepository backlogs, IClientContext ctx)
    {
        var task = await tasks.GetByIdAsync(taskId);
        if (task is null) return null;

        var authorized = await AuthorizedBacklogAsync(task.BacklogId, backlogs, ctx);
        return authorized is null ? null : (task, authorized.Value.Backlog, authorized.Value.TeamId);
    }

    // Rule #5 / R6: client teamIds INTERSECTED with ctx.MemberTeamIds; absent defaults to
    // ctx.MemberTeamIds. NEVER returns null -- null passed to SearchAsync would mean "every team".
    //
    // Deliberately reads the query collection directly rather than taking `int[]? teamIds` as a bound
    // parameter: minimal-API array binding cannot tell "the key is entirely absent" apart from "the key
    // is present with zero values" -- both produce an empty StringValues, and the generated binder turns
    // either one into an EMPTY ARRAY, never null. A plain `int[]? teamIds` therefore sees `is null` as
    // always false, so "absent" would fall into the intersect branch and produce an empty set instead of
    // the required default (every one of the caller's own teams) -- silently hiding every backlog from
    // the plain, filter-less `GET /api/backlogs`. Measured directly: an xunit test asserting a term-only
    // search returned results failed with an empty collection until this was changed to inspect
    // `HttpContext.Request.Query` and treat "key not present" as the absent case.
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

// ==== Request DTOs ======================================================================================
// Prefixed with the owning area (Backlog.../Task...) -- all four Wave-2 endpoint files share this
// namespace, so an unprefixed `UpdateRequest` in two of them would be a duplicate-type compile error
// found only at the merge.

/// <summary>TeamId is deliberately ABSENT: a new backlog belongs to the caller's active team
/// (<see cref="ICurrentTeamService.ActiveTeamId"/>), never a team read off the wire.</summary>
public sealed record BacklogCreateRequest(
    string BacklogCode, string Project,
    DateOnly? StartDate, DateOnly? EndDate, string? PeriodMonth, string? Type,
    int? AssigneeUserId, DateOnly? DeadlineInternal, DateOnly? DeadlineExternal,
    decimal? RoughEstimateHours, decimal? OfficialEstimateHours,
    int? ProgressPercent, string? Note, int? PcaContactId);

/// <summary>TeamId is deliberately ABSENT (rule #8): <c>UpdateCheckedAsync(Backlog, …)</c> writes every
/// column including <c>team_id</c>, so the endpoint re-reads the stored entity and applies only these
/// fields with <c>with { … }</c> -- there is nothing here that could ever null it out.</summary>
public sealed record BacklogUpdateRequest(
    string BacklogCode, string Project,
    DateOnly? StartDate, DateOnly? EndDate, string? PeriodMonth, string? Type,
    int? AssigneeUserId, DateOnly? DeadlineInternal, DateOnly? DeadlineExternal,
    decimal? RoughEstimateHours, decimal? OfficialEstimateHours,
    int? ProgressPercent, string? Note, int? PcaContactId,
    long ExpectedVersion, string? AuditNote = null);

public sealed record BacklogTagsRequest(IReadOnlyList<int> TagIds, long ExpectedVersion);

/// <summary>P20. <c>TargetPeriod</c> is "yyyy-MM", matching <c>Backlog.PeriodMonth</c>.</summary>
public sealed record BacklogContinueRequest(string TargetPeriod);

public sealed record TaskCreateRequest(int BacklogId, string TaskName, int OrderIndex);

/// <summary>The combined editor path -- mirrors <c>UpdateCheckedAsync(TaskItem, long)</c>, which writes
/// task_name + order_index + status together and carries no audit params.</summary>
public sealed record TaskUpdateRequest(string TaskName, int OrderIndex, string Status, long ExpectedVersion);

public sealed record TaskStatusRequest(string Status, long ExpectedVersion);

public sealed record TaskExtendedRequest(string? Type, int? AssigneeUserId, long ExpectedVersion);

public sealed record TaskTagsRequest(IReadOnlyList<int> TagIds, long ExpectedVersion);

/// <summary>Bump-only (rule #9) -- carries no <c>rowVersion</c> on purpose.</summary>
public sealed record TaskActiveRequest(bool IsActive);

/// <summary>Bump-only (rule #9) -- carries no <c>rowVersion</c> on purpose.</summary>
public sealed record TaskOrderRequest(int OrderIndex);
