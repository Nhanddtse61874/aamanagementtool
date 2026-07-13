using Microsoft.AspNetCore.Mvc;
using TimesheetApp.Api.Contracts;
using TimesheetApp.Api.Infrastructure;
using TimesheetApp.Services;

namespace TimesheetApp.Api.Endpoints;

/// <summary>M9 P3b/P3f. Owns <c>/api/tasklist</c> and <c>/api/tasklist/export</c> — the Task List screen's
/// read model and its monthly markdown. Both were unreachable from the web before this file: P1 built
/// <c>ITaskListService</c> and registered it, and NO endpoint called it; <c>ITaskListArchiveService</c> had
/// been DI-registered for far longer and was likewise called by zero endpoints.
///
/// <para><b>REGISTERED ON THE <c>api</c> GROUP, NEVER ON <c>app</c>.</b> <c>Program.cs</c> builds
/// <c>var api = app.MapGroup("").AddEndpointFilter&lt;ClientContextFilter&gt;()</c>, and that filter is the
/// only thing that populates <see cref="IClientContext"/>. <c>app.MapTaskListEndpoints()</c> would compile,
/// route correctly, and hand every handler here an EMPTY <c>ctx.MemberTeamIds</c> — so the screen would
/// render blank for everyone, and the export would silently produce nothing, with no error to explain
/// either.</para>
///
/// <para><b>THE TEAM SCOPE IS A TWO-SIDED TRAP, and both sides are live on these two routes.</b> The
/// repositories underneath treat the team list as:</para>
/// <list type="bullet">
/// <item><c>null</c> => EVERY TEAM, unfiltered. A data leak.</item>
/// <item><c>[]</c> (empty) => NO TEAMS, no rows. Not an accident —
/// <c>IBacklogRepository.SearchAsync</c>'s documented R6 semantic.</item>
/// </list>
/// <para>So "don't filter" is expressible as NEITHER value, and the two mistakes fail in opposite directions:
/// one leaks the whole company, the other returns a blank screen. <c>ITaskListService.GetRowsAsync</c> takes
/// a NON-NULLABLE list (so the leak is at least unrepresentable there), but
/// <c>ITaskListArchiveService.BuildMonthMarkdownAsync</c> takes a NULLABLE one and
/// <c>ExportMonthAsync</c> — the sibling method — passes it <c>null</c> internally, i.e. every team. Both
/// routes below therefore resolve their scope through <c>TimesheetEndpoints.EffectiveTeamIds</c>, which
/// intersects any client-supplied ids with <c>ctx.MemberTeamIds</c> and defaults an ABSENT key to the
/// caller's own memberships.</para>
///
/// <para><b><c>TaskListService</c> deliberately does not inject <c>ICurrentTeamService</c></b> — that service
/// is SCOPED, and injecting it into a Singleton is a captive dependency that fails the container at
/// <c>Build()</c> under the API's <c>ValidateScopes</c>/<c>ValidateOnBuild</c>. The team ids are resolved
/// HERE, in the endpoint, and passed as a parameter. Do not move that resolution into the service.</para>
///
/// <para><b><c>month == 0</c> means ALL MONTHS</b> on <c>/api/tasklist</c> — the desktop month selector's
/// "All months" sentinel, which lists every backlog regardless of period INCLUDING one whose
/// <c>period_month</c> is null. <c>/api/tasklist/export</c> archives one concrete month and has no such
/// sentinel.</para></summary>
public static class TaskListEndpoints
{
    public static IEndpointRouteBuilder MapTaskListEndpoints(this IEndpointRouteBuilder api)
    {
        // ==== The screen ====================================================================================

        // ONE round-trip for the whole screen (rows + Gantt), and that is deliberate: the grid's schedule
        // chips and the Gantt's bar colours are the SAME ScheduleState, so fetching them separately would let
        // a write landing between the two calls put the chart and the grid into visible disagreement, with no
        // way for the client to notice. GetRowsAsync computes both from one snapshot.
        api.MapGet("/api/tasklist", async (
            [FromQuery] int year,
            [FromQuery] int month,
            // DECLARED FOR ApiExplorer ONLY -- DO NOT READ IT, AND DO NOT DELETE IT AS DEAD. The handler
            // resolves teams through TimesheetEndpoints.EffectiveTeamIds(http, ctx), which reads the raw query
            // string BY HAND because a bound int[]? CANNOT TELL "key absent" (=> the caller's own teams) from
            // "key present but empty" (=> no teams) -- both bind to an EMPTY ARRAY, never null, while null
            // means EVERY TEAM to the repository. That hand-read is a data-leak guard and must stay.
            //
            // But a parameter the handler reads off HttpContext is INVISIBLE to ApiExplorer, and therefore to
            // the generated TypeScript client. This declaration is the ONLY thing that puts `teamIds` in the
            // OpenAPI document, and so the only thing that lets the client send a team filter at all. Delete
            // it and the Task List screen silently loses its team filter -- with nothing going red.
            [FromQuery] int[]? teamIds,
            HttpContext http,
            IClientContext ctx,
            ITaskListService taskList) =>
        {
            // NEVER `[]` to mean "don't filter" -- see the class doc. EffectiveTeamIds defaults an absent
            // teamIds key to the caller's own teams, which is what makes an unfiltered request return the
            // caller's rows instead of nothing at all.
            var screen = await taskList.GetRowsAsync(year, month, TimesheetEndpoints.EffectiveTeamIds(http, ctx));
            return Results.Ok(screen.ToDto());
        })
        .WithName("TaskListScreen")   // NOT "TaskList" -- GET /api/tasks already owns that endpoint name.
        .WithTags("TaskList")
        // The Core read-model is NOT what goes on the wire: TaskListRow carries Tag and TaskItem ENTITIES.
        // See TaskListScreenDto.
        .Produces<TaskListScreenDto>();

        // ==== The monthly markdown ==========================================================================

        // BuildMonthMarkdownAsync, NOT ExportMonthAsync. The two are easy to confuse and only one of them can
        // serve a browser:
        //
        //   ExportMonthAsync(year, month)  -> WRITES A FILE ON THE SERVER and returns a SERVER PATH. Useless
        //                                     to a web client, which cannot open it. It also builds that file
        //                                     with teamIds: null -- EVERY TEAM.
        //   BuildMonthMarkdownAsync(...)   -> returns the CONTENT, scoped to the teamIds it is given. This is
        //                                     the one that exists for exactly this purpose.
        //
        // Its teamIds parameter is NULLABLE and null means every team, so EffectiveTeamIds is load-bearing
        // here and not merely tidy: passing null would hand any authenticated caller a markdown dump of the
        // whole company's backlogs.
        api.MapGet("/api/tasklist/export", async (
            [FromQuery] int year,
            [FromQuery] int month,
            // DECLARED FOR ApiExplorer ONLY -- DO NOT READ IT, AND DO NOT DELETE IT AS DEAD. See the identical
            // note on GET /api/tasklist above: EffectiveTeamIds(http, ctx) hand-reads the raw query because a
            // bound int[]? cannot distinguish "absent" from "empty", and this declaration exists solely so the
            // OpenAPI document -- and hence the generated client -- can SEE `teamIds`.
            //
            // The stakes are higher on THIS route than on any other: BuildMonthMarkdownAsync's teamIds is
            // NULLABLE and null means EVERY TEAM, so the export is one wrong value away from handing any
            // authenticated caller a markdown dump of the whole company's backlogs. Reading this bound
            // parameter instead of EffectiveTeamIds is exactly how that happens.
            [FromQuery] int[]? teamIds,
            HttpContext http,
            IClientContext ctx,
            ITaskListArchiveService archive) =>
        {
            var markdown = await archive.BuildMonthMarkdownAsync(
                TimesheetEndpoints.EffectiveTeamIds(http, ctx), year, month);

            // "No data => no file" is the service's own rule (TL-09) -- it returns null rather than building
            // an empty document. Answering 200 with an empty body would give the user a zero-byte download
            // they cannot tell apart from a broken one.
            if (markdown is null)
                return Results.BadRequest(new ValidationBody("No task list data for this month to export."));

            return Results.Text(markdown, "text/markdown");
        })
        .WithName("TaskListExport")
        .WithTags("TaskList")
        // NOT JSON: a markdown document. An application/json schema here would make the generated client call
        // response.json() on it.
        .Produces<string>(StatusCodes.Status200OK, "text/markdown")
        .Produces<ValidationBody>(StatusCodes.Status400BadRequest);

        return api;
    }
}
