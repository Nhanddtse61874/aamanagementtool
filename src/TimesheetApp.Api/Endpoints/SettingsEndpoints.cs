using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using TimesheetApp.Api.Auth;
using TimesheetApp.Api.Contracts;
using TimesheetApp.Api.Infrastructure;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.Services;

namespace TimesheetApp.Api.Endpoints;

/// <summary>W2-D. Owns tag CRUD (<c>/api/tags/*</c>) · <c>/api/teams/*</c> · <c>/api/pca-contacts/*</c> ·
/// <c>/api/users/*</c> · <c>/api/templates/*</c> · <c>/api/holidays/*</c> · <c>/api/default-tasks/*</c> ·
/// <c>/api/standup/entries/*</c> · <c>/api/ops/*</c>.
///
/// <para><b>Standup issues are NESTED under the entry:</b>
/// <c>POST|PUT|DELETE /api/standup/entries/{entryId}/issues[/{issueId}]</c>. A flat
/// <c>/api/standup/issues/{id}</c> CANNOT BE AUTHORIZED AT ALL: <c>GetIssueAsync(issueId)</c> does not exist
/// anywhere in Core, so from an issue id alone there is no way to discover which team it belongs to. For
/// every issue write: (1) <c>GetEntryAsync(entryId)</c> and assert <c>.TeamId ∈ ctx.MemberTeamIds</c>;
/// (2) assert the issue actually belongs to that entry via <c>GetIssuesForEntriesAsync([entryId])</c> —
/// <c>entryId</c> is attacker-supplied too, so step 2 is not optional. Issues are collaborative by design
/// (no owner gate); the TEAM gate is the only one they get.</para>
///
/// <para><b>Call <c>IStandupService</c>, never <c>IStandupRepository</c></b> — the service holds the OWNER
/// GATE on entry writes. Go around it and a user edits a colleague's standup entry. Note
/// <c>StandupEntry</c> is deliberately UNVERSIONED (owner-gated, last-write-wins by design): ignore any
/// <c>rowVersion</c> a client sends for one. Issues ARE versioned — use <c>UpdateIssueCheckedAsync</c>.
/// <c>IStandupRepository.GetEntryAsync</c> / <c>GetIssuesForEntriesAsync</c> are still called directly from
/// here, but only as READS to establish the team gate before delegating the actual write to the service —
/// that is not the same thing as writing through the repository.</para>
///
/// <para><b>After any DefaultTasks write, call <c>IDefaultTaskSyncService.SyncAsync()</c></b> — it reconciles
/// DefaultTasks into every team's DEFAULT backlog. Skip it and the change never reaches any team.</para>
///
/// <para><b><c>/api/ops/*</c> is exactly four routes, all <c>.RequireAuthorization(AuthSetup.AdminPolicy)</c>:</b>
/// <c>POST /retention/preview</c> · <c>POST /retention/run</c> · <c>POST /export/run</c> ·
/// <c>POST /backup/run</c>. <c>RetentionService</c> holds one <c>BEGIN IMMEDIATE</c> across six bulk DELETEs,
/// blocking every writer app-wide — wired straight into the request path, ONE ADMIN CLICK 500s EVERYONE
/// ELSE. Return <b>202 Accepted</b> and run it on a background queue.
/// <c>IBackupService.RestoreAsync</c> is NOT exposed in M8.3: it overwrites the live .db in place while the
/// API holds open connections, which corrupts live readers.</para>
///
/// <para><b>Never call any <c>IAppConfig.Set*</c> from an endpoint.</b> It is a process-wide singleton with
/// ten setters; on a server every one of them is cross-user state — one user toggling dark mode flips it for
/// everyone, and <c>SetDbPath</c> repoints the whole server's database.</para>
///
/// <para><c>Tag</c>, <c>PcaContact</c>, <c>User</c> and <c>Team</c> have no team column — they are global.
/// Team-checking them is meaningless; gate them on the Admin policy instead.</para>
///
/// <para><b>CONTROLLER RULING:</b> for every global entity (Tag/PcaContact/User/Team, and by the identical
/// "no team column" logic, TaskTemplate/DefaultTask/Holiday too) <c>IChangeNotifier.DataChangedAsync</c> is
/// called with <c>teamId: 0</c>, the reserved "broadcast to every connected client" sentinel. Wave 3's
/// SignalR notifier turns <c>teamId == 0</c> into an all-clients send rather than a group send. Standup
/// entries/issues are team-scoped, so they pass the entry's REAL <c>TeamId</c> instead.</para>
///
/// <para><b>Reads vs. writes.</b> For the global "settings" lists (Tags, Teams, PcaContacts, Users), the
/// ACTIVE/default list is open to any authenticated user — <c>TaskListViewModel</c> (a non-admin screen)
/// depends directly on <c>ITagRepository</c>/<c>IPcaContactRepository</c>/<c>IUserRepository</c> for pickers
/// and chips, so gating reads behind Admin would break tagging/assigning for ordinary team members. Only the
/// "incl. inactive" management list and every mutation are Admin-gated.</para></summary>
public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder api)
    {
        MapTagEndpoints(api);
        MapTeamEndpoints(api);
        MapPcaContactEndpoints(api);
        MapUserEndpoints(api);
        MapTemplateEndpoints(api);
        MapHolidayEndpoints(api);
        MapDefaultTaskEndpoints(api);
        MapMeEndpoints(api);
        MapStandupEndpoints(api);
        MapOpsEndpoints(api);
        return api;
    }

    // ===== The caller's own working scope ===================================================================

    /// <summary>The active-team switcher. Without it a user cannot change teams from the web at all, and
    /// <c>ActiveTeamId</c> scopes every timesheet and standup query.
    ///
    /// <para><c>PUT /api/me/active-team</c> sits under <c>/api/me</c>, which <c>AuthSetup.MapAuthMechanism</c>
    /// already maps as a <c>GET</c>. Different method AND different path, so there is no
    /// <c>AmbiguousMatchException</c> — but it is outside W2-D's originally-assigned prefixes, so it is worth
    /// naming at the merge gate.</para></summary>
    private static void MapMeEndpoints(IEndpointRouteBuilder api)
    {
        api.MapPut("/api/me/active-team", async (
            [FromBody] SettingsActiveTeamRequest req,
            IClientContext ctx, ICurrentTeamService currentTeam, IChangeNotifier notifier) =>
        {
            // Rule 8: the target team id is attacker-supplied. Not one of MY memberships => 404 (not 403 —
            // a 403 confirms the team exists). Skip this and a user switches themselves into a team they are
            // not in, and every subsequent timesheet/standup query serves them that team's data — which is
            // precisely what moving ActiveTeamId off IAppConfig (M8.2/W4) existed to prevent.
            if (!ctx.MemberTeamIds.Contains(req.TeamId))
                return Results.NotFound();

            // NOT redundant with the check above, and this is the trap.
            //
            //   ctx.MemberTeamIds  = GetTeamIdsForUserAsync = every UserTeams row, with NO is_active filter.
            //   AvailableTeams     = GetActiveAsync() ∩ memberships  — strictly NARROWER.
            //
            // SetActiveTeamAsync THROWS InvalidOperationException for anything outside AvailableTeams, and
            // ExceptionMapper maps only ConcurrencyConflictException and ArgumentException — so that throw
            // escapes as a 500. A user who is still a member of a team an admin has since DEACTIVATED (via
            // PUT /api/teams/{id}/active, above — the UserTeams rows survive the soft-delete) is exactly
            // that case: present in MemberTeamIds, absent from AvailableTeams. Membership alone does not
            // keep this endpoint off the 500 path.
            //
            // 400, not 404: the caller IS a member, so naming the reason leaks nothing they do not know.
            if (!currentTeam.AvailableTeams.Any(t => t.Id == req.TeamId))
                return Results.BadRequest(new ValidationBody("That team is not active."));

            // Bump-only (rule 9): Users.active_team_id is a system write carrying no client-held version.
            await currentTeam.SetActiveTeamAsync(req.TeamId);

            // The NEW team id, never 0 — an active-team switch is per-USER state, not a global change.
            await notifier.DataChangedAsync(DataKind.Teams, req.TeamId, ctx.ConnectionId);
            return Results.NoContent();
        });
    }

    // ===== Tags (global; hard-delete) ======================================================================

    private static void MapTagEndpoints(IEndpointRouteBuilder api)
    {
        api.MapGet("/api/tags", async (ITagRepository tags) =>
            Results.Ok((await tags.GetAllAsync()).Select(t => t.ToDto()).ToList()));

        api.MapPost("/api/tags", async (
                [FromBody] SettingsTagCreateRequest req,
                ITagRepository tags, IChangeNotifier notifier, IClientContext ctx) =>
            {
                if (string.IsNullOrWhiteSpace(req.Text))
                    return Results.BadRequest(new ValidationBody("Tag text is required."));

                var id = await tags.InsertAsync(new Tag(0, req.Text, req.Icon, req.Color, DateTimeOffset.UtcNow));
                await notifier.DataChangedAsync(DataKind.Tags, teamId: 0, ctx.ConnectionId);
                return Results.Ok(new TagDto(id, req.Text, req.Icon, req.Color, RowVersion: 1));
            })
            .RequireAuthorization(AuthSetup.AdminPolicy);

        api.MapPut("/api/tags/{id:int}", async (
                int id, [FromBody] SettingsTagUpdateRequest req,
                ITagRepository tags, IChangeNotifier notifier, IClientContext ctx) =>
            {
                if (string.IsNullOrWhiteSpace(req.Text))
                    return Results.BadRequest(new ValidationBody("Tag text is required."));

                // CreatedAt is ignored by UpdateCheckedAsync's SQL (it never touches created_at) — the
                // placeholder value below is never persisted.
                var newVersion = await tags.UpdateCheckedAsync(
                    new Tag(id, req.Text, req.Icon, req.Color, default), req.ExpectedVersion);
                await notifier.DataChangedAsync(DataKind.Tags, teamId: 0, ctx.ConnectionId);
                return Results.Ok(new SavedBody(newVersion));
            })
            .RequireAuthorization(AuthSetup.AdminPolicy);

        api.MapDelete("/api/tags/{id:int}", async (
                int id, ITagRepository tags, IChangeNotifier notifier, IClientContext ctx) =>
            {
                await tags.DeleteAsync(id);
                await notifier.DataChangedAsync(DataKind.Tags, teamId: 0, ctx.ConnectionId);
                return Results.NoContent();
            })
            .RequireAuthorization(AuthSetup.AdminPolicy);
    }

    // ===== Teams (global; soft-delete; membership) =========================================================

    private static void MapTeamEndpoints(IEndpointRouteBuilder api)
    {
        api.MapGet("/api/teams", async (ITeamRepository teams) =>
            Results.Ok((await teams.GetActiveAsync()).Select(t => t.ToDto()).ToList()));

        api.MapGet("/api/teams/all", async (ITeamRepository teams) =>
                Results.Ok((await teams.GetAllAsync()).Select(t => t.ToDto()).ToList()))
            .RequireAuthorization(AuthSetup.AdminPolicy);

        api.MapPost("/api/teams", async (
                [FromBody] SettingsNameRequest req,
                ITeamRepository teams, IChangeNotifier notifier, IClientContext ctx) =>
            {
                if (string.IsNullOrWhiteSpace(req.Name))
                    return Results.BadRequest(new ValidationBody("Team name is required."));

                var now = DateTimeOffset.UtcNow;
                var id = await teams.InsertAsync(new Team(0, req.Name, true, now));
                await notifier.DataChangedAsync(DataKind.Teams, teamId: 0, ctx.ConnectionId);
                return Results.Ok(new TeamDto(id, req.Name, true, now, RowVersion: 1));
            })
            .RequireAuthorization(AuthSetup.AdminPolicy);

        api.MapPut("/api/teams/{id:int}", async (
                int id, [FromBody] SettingsRenameRequest req,
                ITeamRepository teams, IChangeNotifier notifier, IClientContext ctx) =>
            {
                if (string.IsNullOrWhiteSpace(req.Name))
                    return Results.BadRequest(new ValidationBody("Team name is required."));

                var newVersion = await teams.UpdateNameCheckedAsync(id, req.Name, req.ExpectedVersion);
                await notifier.DataChangedAsync(DataKind.Teams, teamId: 0, ctx.ConnectionId);
                return Results.Ok(new SavedBody(newVersion));
            })
            .RequireAuthorization(AuthSetup.AdminPolicy);

        api.MapPut("/api/teams/{id:int}/active", async (
                int id, [FromBody] SettingsSetActiveRequest req,
                ITeamRepository teams, IChangeNotifier notifier, IClientContext ctx) =>
            {
                await teams.SetActiveAsync(id, req.IsActive);
                await notifier.DataChangedAsync(DataKind.Teams, teamId: 0, ctx.ConnectionId);
                return Results.NoContent();
            })
            .RequireAuthorization(AuthSetup.AdminPolicy);

        // Membership editor: read/replace the member set. Admin-only — the read is scoped to the same
        // admin-only editing flow as the write, and any team member already sees teammates via the board.
        api.MapGet("/api/teams/{id:int}/members", async (int id, ITeamRepository teams) =>
                Results.Ok(await teams.GetUserIdsForTeamAsync(id)))
            .RequireAuthorization(AuthSetup.AdminPolicy);

        api.MapPut("/api/teams/{id:int}/members", async (
                int id, [FromBody] SettingsTeamMembersRequest req,
                ITeamRepository teams, IChangeNotifier notifier, IClientContext ctx) =>
            {
                var newVersion = await teams.SetMembersCheckedAsync(id, req.UserIds, req.ExpectedVersion);
                await notifier.DataChangedAsync(DataKind.Teams, teamId: 0, ctx.ConnectionId);
                return Results.Ok(new SavedBody(newVersion));
            })
            .RequireAuthorization(AuthSetup.AdminPolicy);
    }

    // ===== PCA contacts (global; soft-delete) ===============================================================

    private static void MapPcaContactEndpoints(IEndpointRouteBuilder api)
    {
        api.MapGet("/api/pca-contacts", async (IPcaContactRepository contacts) =>
            Results.Ok((await contacts.GetActiveAsync()).Select(c => c.ToDto()).ToList()));

        api.MapGet("/api/pca-contacts/all", async (IPcaContactRepository contacts) =>
                Results.Ok((await contacts.GetAllAsync()).Select(c => c.ToDto()).ToList()))
            .RequireAuthorization(AuthSetup.AdminPolicy);

        api.MapPost("/api/pca-contacts", async (
                [FromBody] SettingsNameRequest req,
                IPcaContactRepository contacts, IChangeNotifier notifier, IClientContext ctx) =>
            {
                if (string.IsNullOrWhiteSpace(req.Name))
                    return Results.BadRequest(new ValidationBody("Contact name is required."));

                var id = await contacts.InsertAsync(new PcaContact(0, req.Name, true));
                await notifier.DataChangedAsync(DataKind.PcaContacts, teamId: 0, ctx.ConnectionId);
                return Results.Ok(new PcaContactDto(id, req.Name, true, RowVersion: 1));
            })
            .RequireAuthorization(AuthSetup.AdminPolicy);

        api.MapPut("/api/pca-contacts/{id:int}", async (
                int id, [FromBody] SettingsRenameRequest req,
                IPcaContactRepository contacts, IChangeNotifier notifier, IClientContext ctx) =>
            {
                if (string.IsNullOrWhiteSpace(req.Name))
                    return Results.BadRequest(new ValidationBody("Contact name is required."));

                var newVersion = await contacts.UpdateNameCheckedAsync(id, req.Name, req.ExpectedVersion);
                await notifier.DataChangedAsync(DataKind.PcaContacts, teamId: 0, ctx.ConnectionId);
                return Results.Ok(new SavedBody(newVersion));
            })
            .RequireAuthorization(AuthSetup.AdminPolicy);

        api.MapPut("/api/pca-contacts/{id:int}/active", async (
                int id, [FromBody] SettingsSetActiveRequest req,
                IPcaContactRepository contacts, IChangeNotifier notifier, IClientContext ctx) =>
            {
                await contacts.SetActiveAsync(id, req.IsActive);
                await notifier.DataChangedAsync(DataKind.PcaContacts, teamId: 0, ctx.ConnectionId);
                return Results.NoContent();
            })
            .RequireAuthorization(AuthSetup.AdminPolicy);
    }

    // ===== Users (global; soft-delete). Login credentials (password) are W2-A's — not here. ================

    private static void MapUserEndpoints(IEndpointRouteBuilder api)
    {
        api.MapGet("/api/users", async (IUserRepository users) =>
            Results.Ok((await users.GetActiveAsync()).Select(u => u.ToDto()).ToList()));

        api.MapGet("/api/users/all", async (IUserRepository users) =>
                Results.Ok((await users.GetAllAsync()).Select(u => u.ToDto()).ToList()))
            .RequireAuthorization(AuthSetup.AdminPolicy);

        // Mirrors UsersViewModel.InsertAsync: name only, no username, active. A created user cannot log in
        // until an admin also sets a username (below) and a password (W2-A's set-password-for-user).
        api.MapPost("/api/users", async (
                [FromBody] SettingsNameRequest req,
                IUserRepository users, IChangeNotifier notifier, IClientContext ctx) =>
            {
                if (string.IsNullOrWhiteSpace(req.Name))
                    return Results.BadRequest(new ValidationBody("User name is required."));

                var id = await users.InsertAsync(new User(0, req.Name, null, true));
                await notifier.DataChangedAsync(DataKind.Users, teamId: 0, ctx.ConnectionId);
                return Results.Ok(new UserDto(id, req.Name, null, true, false, RowVersion: 1));
            })
            .RequireAuthorization(AuthSetup.AdminPolicy);

        api.MapPut("/api/users/{id:int}", async (
                int id, [FromBody] SettingsRenameRequest req,
                IUserRepository users, IChangeNotifier notifier, IClientContext ctx) =>
            {
                if (string.IsNullOrWhiteSpace(req.Name))
                    return Results.BadRequest(new ValidationBody("User name is required."));

                var newVersion = await users.UpdateNameCheckedAsync(id, req.Name, req.ExpectedVersion);
                await notifier.DataChangedAsync(DataKind.Users, teamId: 0, ctx.ConnectionId);
                return Results.Ok(new SavedBody(newVersion));
            })
            .RequireAuthorization(AuthSetup.AdminPolicy);

        // A newly-created user has a NULL username (see POST above) and cannot log in until this is set.
        api.MapPut("/api/users/{id:int}/username", async (
                int id, [FromBody] SettingsUserSetUsernameRequest req,
                IUserRepository users, IChangeNotifier notifier, IClientContext ctx) =>
            {
                if (string.IsNullOrWhiteSpace(req.Username))
                    return Results.BadRequest(new ValidationBody("Username is required."));

                var newVersion = await users.SetUsernameCheckedAsync(id, req.Username, req.ExpectedVersion);
                await notifier.DataChangedAsync(DataKind.Users, teamId: 0, ctx.ConnectionId);
                return Results.Ok(new SavedBody(newVersion));
            })
            .RequireAuthorization(AuthSetup.AdminPolicy);

        api.MapPut("/api/users/{id:int}/active", async (
                int id, [FromBody] SettingsSetActiveRequest req,
                IUserRepository users, IChangeNotifier notifier, IClientContext ctx) =>
            {
                await users.SetActiveAsync(id, req.IsActive);
                await notifier.DataChangedAsync(DataKind.Users, teamId: 0, ctx.ConnectionId);
                return Results.NoContent();
            })
            .RequireAuthorization(AuthSetup.AdminPolicy);
    }

    // ===== Task templates (global; unversioned; edit = delete-by-name then reinsert, mirrors the WPF VM) ====

    private static void MapTemplateEndpoints(IEndpointRouteBuilder api)
    {
        api.MapGet("/api/templates", async (ITaskTemplateRepository templates) =>
            Results.Ok((await templates.GetAllAsync()).Select(t => t.ToDto()).ToList()));

        api.MapPost("/api/templates", async (
                [FromBody] SettingsTemplateCreateRequest req,
                ITaskTemplateRepository templates, IChangeNotifier notifier, IClientContext ctx) =>
            {
                if (string.IsNullOrWhiteSpace(req.TemplateName) || string.IsNullOrWhiteSpace(req.TaskName))
                    return Results.BadRequest(new ValidationBody("Template name and task name are required."));

                var id = await templates.InsertAsync(
                    new TaskTemplate(0, req.TemplateName, req.TaskName, req.OrderIndex));
                await notifier.DataChangedAsync(DataKind.Templates, teamId: 0, ctx.ConnectionId);
                return Results.Ok(new TaskTemplateDto(id, req.TemplateName, req.TaskName, req.OrderIndex));
            })
            .RequireAuthorization(AuthSetup.AdminPolicy);

        api.MapDelete("/api/templates/{id:int}", async (
                int id, ITaskTemplateRepository templates, IChangeNotifier notifier, IClientContext ctx) =>
            {
                await templates.DeleteAsync(id);
                await notifier.DataChangedAsync(DataKind.Templates, teamId: 0, ctx.ConnectionId);
                return Results.NoContent();
            })
            .RequireAuthorization(AuthSetup.AdminPolicy);

        // Bulk delete of a whole named template (all its rows) — the other half of the WPF "edit" flow
        // (delete-by-name, then re-POST each row). Query string, not a route segment: a template name is
        // free text and may contain characters that are awkward to route-escape.
        api.MapDelete("/api/templates", async (
                string? templateName,
                ITaskTemplateRepository templates, IChangeNotifier notifier, IClientContext ctx) =>
            {
                if (string.IsNullOrWhiteSpace(templateName))
                    return Results.BadRequest(new ValidationBody("templateName is required."));

                await templates.DeleteByTemplateNameAsync(templateName);
                await notifier.DataChangedAsync(DataKind.Templates, teamId: 0, ctx.ConnectionId);
                return Results.NoContent();
            })
            .RequireAuthorization(AuthSetup.AdminPolicy);
    }

    // ===== Holidays (global; unversioned; natural key = date). Column is holiday_date, not date. ===========

    private static void MapHolidayEndpoints(IEndpointRouteBuilder api)
    {
        api.MapGet("/api/holidays", async (int? year, int? month, IHolidayRepository holidays) =>
        {
            var rows = year is { } y && month is { } m
                ? await holidays.GetForMonthAsync(y, m)
                : await holidays.GetAllAsync();
            return Results.Ok(rows.Select(h => h.ToDto()).ToList());
        });

        api.MapPost("/api/holidays", async (
                [FromBody] SettingsHolidayRequest req,
                IHolidayRepository holidays, IChangeNotifier notifier, IClientContext ctx) =>
            {
                await holidays.UpsertAsync(req.Date, req.Description);
                await notifier.DataChangedAsync(DataKind.Holidays, teamId: 0, ctx.ConnectionId);
                return Results.NoContent();
            })
            .RequireAuthorization(AuthSetup.AdminPolicy);

        api.MapDelete("/api/holidays/{date}", async (
                string date, IHolidayRepository holidays, IChangeNotifier notifier, IClientContext ctx) =>
            {
                if (!TryParseDay(date, out var day))
                    return Results.BadRequest(new ValidationBody("date must be yyyy-MM-dd."));

                await holidays.DeleteAsync(day);
                await notifier.DataChangedAsync(DataKind.Holidays, teamId: 0, ctx.ConnectionId);
                return Results.NoContent();
            })
            .RequireAuthorization(AuthSetup.AdminPolicy);
    }

    // ===== Default tasks (global; every write reconciles into every team's DEFAULT backlog) =================

    private static void MapDefaultTaskEndpoints(IEndpointRouteBuilder api)
    {
        api.MapGet("/api/default-tasks", async (IDefaultTaskRepository defaults) =>
            Results.Ok((await defaults.GetActiveAsync()).Select(d => d.ToDto()).ToList()));

        api.MapPost("/api/default-tasks", async (
                [FromBody] SettingsDefaultTaskCreateRequest req,
                IDefaultTaskRepository defaults, IDefaultTaskSyncService sync,
                IChangeNotifier notifier, IClientContext ctx) =>
            {
                if (string.IsNullOrWhiteSpace(req.TaskName))
                    return Results.BadRequest(new ValidationBody("Task name is required."));

                var id = await defaults.InsertAsync(new DefaultTask(0, req.TaskName, req.OrderIndex, true));
                await sync.SyncAsync();
                await notifier.DataChangedAsync(DataKind.DefaultTasks, teamId: 0, ctx.ConnectionId);
                return Results.Ok(new DefaultTaskDto(id, req.TaskName, req.OrderIndex, true));
            })
            .RequireAuthorization(AuthSetup.AdminPolicy);

        api.MapPut("/api/default-tasks/{id:int}/active", async (
                int id, [FromBody] SettingsSetActiveRequest req,
                IDefaultTaskRepository defaults, IDefaultTaskSyncService sync,
                IChangeNotifier notifier, IClientContext ctx) =>
            {
                await defaults.SetActiveAsync(id, req.IsActive);
                await sync.SyncAsync();
                await notifier.DataChangedAsync(DataKind.DefaultTasks, teamId: 0, ctx.ConnectionId);
                return Results.NoContent();
            })
            .RequireAuthorization(AuthSetup.AdminPolicy);
    }

    // ===== Standup entries + issues (team-scoped; entries are owner-gated by the service) ===================

    private static void MapStandupEndpoints(IEndpointRouteBuilder api)
    {
        // My own day (Input tab) — scoped internally to the current user + active team.
        api.MapGet("/api/standup/entries", async (string? date, IStandupService standup) =>
        {
            if (!TryParseDay(date, out var day))
                return Results.BadRequest(new ValidationBody("date must be yyyy-MM-dd."));

            return Results.Ok(ToWire(await standup.GetMyStandupAsync(day)));
        });

        // The multi-team board. R6: client-supplied teamIds is INTERSECTED with membership; absent defaults
        // to the full membership; never passed through as null (null means "every team" to the service).
        api.MapGet("/api/standup/board", async (
                string? date, int[]? teamIds, IClientContext ctx, IStandupService standup) =>
            {
                if (!TryParseDay(date, out var day))
                    return Results.BadRequest(new ValidationBody("date must be yyyy-MM-dd."));

                var requested = teamIds is { Length: > 0 } ? teamIds : ctx.MemberTeamIds;
                var scoped = requested.Intersect(ctx.MemberTeamIds).ToList();

                var board = await standup.GetTeamStandupAsync(day, scoped);
                return Results.Ok(board.Select(ToWire).ToList());
            });

        api.MapPost("/api/standup/entries", async (
                [FromBody] SettingsStandupEntryCreateRequest req,
                IStandupService standup, ICurrentTeamService currentTeam,
                IChangeNotifier notifier, IClientContext ctx) =>
            {
                var draft = new StandupEntryDraft(
                    req.Section, req.BacklogId, req.BacklogCode, req.TaskText,
                    req.Description ?? "", req.Deadline, req.Status);

                // AddEntryAsync validates first (throws ArgumentException -> 400 via the mapper) and only
                // returns 0 for the edit-lock no-op (today/yesterday only) — ClientContextFilter already
                // guarantees a resolved current user, so 0 here can only mean "locked day".
                var id = await standup.AddEntryAsync(req.WorkDate, draft);
                if (id == 0)
                    return Results.BadRequest(new ValidationBody(
                        "Cannot add: the day is locked (editable only today or yesterday)."));

                await notifier.DataChangedAsync(DataKind.Standup, currentTeam.ActiveTeamId, ctx.ConnectionId);
                return Results.Ok(id);
            });

        api.MapPut("/api/standup/entries/{entryId:int}", async (
                int entryId, [FromBody] SettingsStandupEntryUpdateRequest req,
                IClientContext ctx, IStandupRepository standupRepo, IStandupService standup,
                IChangeNotifier notifier) =>
            {
                var entry = await standupRepo.GetEntryAsync(entryId);
                if (!TryAuthorizeEntryTeam(entry, ctx, out var teamId))
                    return Results.NotFound();

                var draft = new StandupEntryDraft(
                    req.Section, req.BacklogId, req.BacklogCode, req.TaskText,
                    req.Description ?? "", req.Deadline, req.Status);

                // The entry is confirmed to exist and be team-visible above, so a false here can only be the
                // owner gate or the edit-lock (StandupService.cs:158) — never "not found".
                var ok = await standup.UpdateEntryAsync(entryId, draft);
                if (!ok)
                    return Results.BadRequest(new ValidationBody(
                        "Not the entry's owner, or the day is no longer editable."));

                await notifier.DataChangedAsync(DataKind.Standup, teamId, ctx.ConnectionId);
                return Results.NoContent();
            });

        api.MapDelete("/api/standup/entries/{entryId:int}", async (
                int entryId, IClientContext ctx, IStandupRepository standupRepo, IStandupService standup,
                IChangeNotifier notifier) =>
            {
                var entry = await standupRepo.GetEntryAsync(entryId);
                if (!TryAuthorizeEntryTeam(entry, ctx, out var teamId))
                    return Results.NotFound();

                var ok = await standup.DeleteEntryAsync(entryId);
                if (!ok)
                    return Results.BadRequest(new ValidationBody(
                        "Not the entry's owner, or the day is no longer editable."));

                await notifier.DataChangedAsync(DataKind.Standup, teamId, ctx.ConnectionId);
                return Results.NoContent();
            });

        // Drag-reorder. The literal "reorder" cannot satisfy the `:int` constraint on the sibling
        // PUT /api/standup/entries/{entryId:int}, so the two coexist without an AmbiguousMatchException in
        // either registration order.
        api.MapPut("/api/standup/entries/reorder", async (
            [FromBody] SettingsStandupReorderRequest req,
            IClientContext ctx, IStandupRepository standupRepo, IStandupService standup,
            IChangeNotifier notifier) =>
        {
            // BOTH ids are attacker-supplied, so BOTH are team-gated.
            var dragged = await standupRepo.GetEntryAsync(req.DraggedId);
            if (!TryAuthorizeEntryTeam(dragged, ctx, out var teamId))
                return Results.NotFound();

            var target = await standupRepo.GetEntryAsync(req.TargetId);
            if (!TryAuthorizeEntryTeam(target, ctx, out _))
                return Results.NotFound();

            // ReorderEntryAsync returns void and SILENTLY no-ops on each of its three rejections (owner,
            // edit-lock, cross-day). Re-checking them here is not a second gate — the service still enforces
            // every one of them — it only stops a 204 from claiming a write that never happened. `dragged` is
            // non-null here: TryAuthorizeEntryTeam is [NotNullWhen(true)].
            if (dragged.UserId != ctx.UserId)
                return Results.BadRequest(new ValidationBody("Only the entry's owner may reorder it."));
            if (!standup.CanEditDay(dragged.WorkDate))
                return Results.BadRequest(new ValidationBody("The day is no longer editable."));
            if (dragged.WorkDate != target.WorkDate)
                return Results.BadRequest(new ValidationBody("Entries can only be reordered within one day."));

            await standup.ReorderEntryAsync(req.DraggedId, req.TargetId);

            await notifier.DataChangedAsync(DataKind.Standup, teamId, ctx.ConnectionId);
            return Results.NoContent();
        });

        // P18 Quick Import: clone my own source day into my own target day, appending.
        api.MapPost("/api/standup/quick-import", async (
            [FromBody] SettingsQuickImportRequest req,
            IClientContext ctx, IStandupService standup, ICurrentTeamService currentTeam,
            IChangeNotifier notifier) =>
        {
            // No id on the wire, so nothing to team-gate: QuickImportDayAsync reads and writes only the
            // CURRENT user's own entries, scoped to their own active team. The actor is ctx.UserId by
            // construction (the service takes it from ICurrentUserService, which ClientContextFilter
            // resolved from the cookie) — there is no author field a caller could supply.
            //
            // The service returns 0 for BOTH "locked target" (a rejection) and "empty source" (a legitimate
            // no-op). Checking the lock here — CanEditDay is on IStandupService — separates them, so a 0
            // that survives to the response can only mean the source day was empty.
            if (!standup.CanEditDay(req.TargetDate))
                return Results.BadRequest(new ValidationBody(
                    "Cannot import: the target day is locked (editable only today or yesterday)."));

            var cloned = await standup.QuickImportDayAsync(req.SourceDate, req.TargetDate);

            // Rule 7 is about SUCCESSFUL mutations: nothing copied => nothing changed => nothing to announce.
            if (cloned > 0)
                await notifier.DataChangedAsync(DataKind.Standup, currentTeam.ActiveTeamId, ctx.ConnectionId);

            return Results.Ok(cloned);
        });

        // ---- Issues: collaborative (no owner gate), team-gated only. See the class doc for why the team
        // gate has to be established here, from the repository, before the service is ever called. --------

        api.MapPost("/api/standup/entries/{entryId:int}/issues", async (
                int entryId, [FromBody] SettingsStandupIssueCreateRequest req,
                IClientContext ctx, IStandupRepository standupRepo, IStandupService standup,
                IChangeNotifier notifier) =>
            {
                var entry = await standupRepo.GetEntryAsync(entryId);
                if (!TryAuthorizeEntryTeam(entry, ctx, out var teamId))
                    return Results.NotFound();

                // AddIssueAsync throws ArgumentException for bad input (empty text / invalid status) -> 400
                // via the mapper. Mirrors ProbeEndpoints' /probe/issue: return the bare new id.
                var id = await standup.AddIssueAsync(entryId, req.IssueText, req.SolutionText, req.Status);

                await notifier.DataChangedAsync(DataKind.Standup, teamId, ctx.ConnectionId);
                return Results.Ok(id);
            });

        api.MapPut("/api/standup/entries/{entryId:int}/issues/{issueId:int}", async (
                int entryId, int issueId, [FromBody] SettingsStandupIssueUpdateRequest req,
                IClientContext ctx, IStandupRepository standupRepo, IStandupService standup,
                IChangeNotifier notifier) =>
            {
                var entry = await standupRepo.GetEntryAsync(entryId);
                if (!TryAuthorizeEntryTeam(entry, ctx, out var teamId))
                    return Results.NotFound();

                // entryId is attacker-supplied too: confirm the issue actually belongs to THIS entry before
                // touching it, or a caller can pair their own entry id with someone else's issue id.
                var existingIssue = await FindIssueInEntryAsync(standupRepo, entryId, issueId);
                if (existingIssue is null)
                    return Results.NotFound();

                // OrderIndex is deliberately NOT taken from the request and preserved via `with`: there is
                // no reorder concept for issues in IStandupService, and a whole-record overwrite would
                // otherwise silently reset it to whatever the client's DTO happened to default to.
                var updated = existingIssue with
                {
                    IssueText = req.IssueText,
                    SolutionText = req.SolutionText,
                    Status = req.Status,
                };
                var newVersion = await standup.UpdateIssueCheckedAsync(updated, req.ExpectedVersion);

                await notifier.DataChangedAsync(DataKind.Standup, teamId, ctx.ConnectionId);
                return Results.Ok(new SavedBody(newVersion));
            });

        api.MapDelete("/api/standup/entries/{entryId:int}/issues/{issueId:int}", async (
                int entryId, int issueId,
                IClientContext ctx, IStandupRepository standupRepo, IStandupService standup,
                IChangeNotifier notifier) =>
            {
                var entry = await standupRepo.GetEntryAsync(entryId);
                if (!TryAuthorizeEntryTeam(entry, ctx, out var teamId))
                    return Results.NotFound();

                var existingIssue = await FindIssueInEntryAsync(standupRepo, entryId, issueId);
                if (existingIssue is null)
                    return Results.NotFound();

                // No *CheckedAsync delete exists for StandupIssue (rule 9's bump-only list applies by the
                // same "no checked sibling" logic) — the team+membership gate above is the only guard there
                // is for a delete.
                await standup.DeleteIssueAsync(issueId);

                await notifier.DataChangedAsync(DataKind.Standup, teamId, ctx.ConnectionId);
                return Results.NoContent();
            });
    }

    // ===== Ops: retention / export / backup — admin-only, destructive, deliberately narrow ==================

    private static void MapOpsEndpoints(IEndpointRouteBuilder api)
    {
        api.MapPost("/api/ops/retention/preview", async (IClientContext ctx, IRetentionService retention) =>
            {
                if (!ctx.IsAdmin) return Results.StatusCode(StatusCodes.Status403Forbidden);
                return Results.Ok(await retention.PreviewAsync());
            })
            .RequireAuthorization(AuthSetup.AdminPolicy);

        // 202, NOT in the request path: RetentionService holds one BEGIN IMMEDIATE across six bulk DELETEs,
        // which blocks every other writer app-wide. IRetentionService is a Singleton with an entirely
        // Singleton dependency graph (IAppConfig/IConnectionFactory/IClock/IDbBackupHelper/
        // ISettingsRepository/IPruneArchiver), so capturing it and running it on a background Task after the
        // request's scope ends is safe — nothing scoped is captured. There is no DI-registered background
        // queue available here (Program.cs is frozen for Wave 2), so this is a plain fire-and-forget Task.Run
        // with its own try/catch so a failure is logged instead of silently lost.
        api.MapPost("/api/ops/retention/run", (
                IClientContext ctx, IRetentionService retention, ILogger<SettingsOpsMarker> logger) =>
            {
                if (!ctx.IsAdmin) return Results.StatusCode(StatusCodes.Status403Forbidden);

                _ = Task.Run(async () =>
                {
                    try { await retention.EnsureRetentionAsync(); }
                    catch (Exception ex) { logger.LogError(ex, "Background retention run failed."); }
                });

                return Results.Accepted();
            })
            .RequireAuthorization(AuthSetup.AdminPolicy);

        api.MapPost("/api/ops/export/run", async (IClientContext ctx, IExportHubService export) =>
            {
                if (!ctx.IsAdmin) return Results.StatusCode(StatusCodes.Status403Forbidden);
                return Results.Ok(new SettingsOpsResult(await export.ExportNowAsync()));
            })
            .RequireAuthorization(AuthSetup.AdminPolicy);

        api.MapPost("/api/ops/backup/run", async (IClientContext ctx, IBackupService backup) =>
            {
                if (!ctx.IsAdmin) return Results.StatusCode(StatusCodes.Status403Forbidden);
                return Results.Ok(new SettingsOpsResult(await backup.BackupNowAsync()));
            })
            .RequireAuthorization(AuthSetup.AdminPolicy);
    }

    // ===== helpers ===========================================================================================

    private static bool TryParseDay(string? s, out DateOnly day) =>
        DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);

    /// <summary>The entry-level team gate shared by every entry/issue write: null/missing entry, or a
    /// <c>TeamId</c> the caller is not a member of, both resolve to "not found" (never 403 — a 403 would
    /// confirm the row exists to someone who cannot see it).
    ///
    /// <para><c>[NotNullWhen(true)]</c> is what lets the reorder handler read <c>dragged.UserId</c> without a
    /// null-forgiving <c>!</c>: a <c>true</c> return provably means the entry was found.</para></summary>
    private static bool TryAuthorizeEntryTeam(
        [NotNullWhen(true)] StandupEntry? entry, IClientContext ctx, out int teamId)
    {
        teamId = entry?.TeamId ?? -1;
        return entry is not null && ctx.MemberTeamIds.Contains(teamId);
    }

    private static async Task<StandupIssue?> FindIssueInEntryAsync(
        IStandupRepository standupRepo, int entryId, int issueId)
    {
        var issues = await standupRepo.GetIssuesForEntriesAsync(new[] { entryId });
        return issues.FirstOrDefault(i => i.Id == issueId);
    }

    private static SettingsUserStandup ToWire(UserStandup u) => new(
        u.UserId, u.UserName,
        u.Yesterday.Select(ToWire).ToList(),
        u.Today.Select(ToWire).ToList());

    private static SettingsStandupEntryView ToWire(StandupEntryView v) => new(
        v.Entry.ToDto(),
        v.Issues.Select(i => i.ToDto()).ToList(),
        v.Editable);
}

// ---- Request DTOs. Prefixed "Settings" — all four Wave-2 endpoint files share this namespace. -------------

internal sealed record SettingsNameRequest(string Name);

internal sealed record SettingsRenameRequest(string Name, long ExpectedVersion);

internal sealed record SettingsSetActiveRequest(bool IsActive);

internal sealed record SettingsTagCreateRequest(string Text, string Icon, string Color);

internal sealed record SettingsTagUpdateRequest(string Text, string Icon, string Color, long ExpectedVersion);

internal sealed record SettingsTeamMembersRequest(IReadOnlyList<int> UserIds, long ExpectedVersion);

internal sealed record SettingsUserSetUsernameRequest(string Username, long ExpectedVersion);

internal sealed record SettingsTemplateCreateRequest(string TemplateName, string TaskName, int OrderIndex);

internal sealed record SettingsHolidayRequest(DateOnly Date, string? Description);

internal sealed record SettingsDefaultTaskCreateRequest(string TaskName, int OrderIndex);

internal sealed record SettingsStandupEntryCreateRequest(
    DateOnly WorkDate, string Section, int? BacklogId, string BacklogCode, string TaskText,
    string? Description, DateOnly? Deadline, string Status);

// No WorkDate: UpdateEntryAsync's StandupEntryDraft carries no WorkDate field at all — an entry cannot be
// moved to a different day via update, so a WorkDate here would silently be accepted and silently ignored.
internal sealed record SettingsStandupEntryUpdateRequest(
    string Section, int? BacklogId, string BacklogCode, string TaskText,
    string? Description, DateOnly? Deadline, string Status);

internal sealed record SettingsStandupIssueCreateRequest(string IssueText, string? SolutionText, string Status);

internal sealed record SettingsStandupIssueUpdateRequest(
    string IssueText, string? SolutionText, string Status, long ExpectedVersion);

internal sealed record SettingsStandupReorderRequest(int DraggedId, int TargetId);

internal sealed record SettingsQuickImportRequest(DateOnly SourceDate, DateOnly TargetDate);

/// <summary>No <c>rowVersion</c>: <c>IUserRepository.SetActiveTeamIdAsync</c> is bump-only by design
/// (rule 9) — a system write with nobody to race.</summary>
internal sealed record SettingsActiveTeamRequest(int TeamId);

// ---- Response DTOs (composite read-models with no equivalent in Contracts/Dtos.cs) --------------------------

internal sealed record SettingsStandupEntryView(StandupEntryDto Entry, IReadOnlyList<StandupIssueDto> Issues, bool Editable);

internal sealed record SettingsUserStandup(
    int UserId, string UserName,
    IReadOnlyList<SettingsStandupEntryView> Yesterday, IReadOnlyList<SettingsStandupEntryView> Today);

internal sealed record SettingsOpsResult(string? Value);

/// <summary>Pure category marker for <see cref="ILogger{TCategoryName}"/> in the retention background task —
/// <see cref="SettingsEndpoints"/> itself is a static class and cannot be used as a generic type argument
/// (CS0718).</summary>
internal sealed class SettingsOpsMarker;
