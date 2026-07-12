using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using TimesheetApp.Api.Auth;
using TimesheetApp.Api.Contracts;
using TimesheetApp.Api.Infrastructure;
using TimesheetApp.Services;

namespace TimesheetApp.ApiTests;

/// <summary>Test-only routes, mapped into the REAL pipeline (real auth, real ClientContextFilter, real
/// ExceptionMapper, real services, real database) via an <see cref="IStartupFilter"/>.
///
/// <para><b>Why these exist.</b> Wave 1 ships the four <c>Endpoints/*.cs</c> files as empty stubs — Wave 2
/// fills them in. But two Wave-1 contracts can only be proven over HTTP: that <c>ClientContextFilter</c>
/// actually populates <see cref="IClientContext"/>, and that the two error channels really do produce the
/// frozen 409 and 400 bodies. Rather than put endpoint bodies into files Wave 2 owns — or bake a test-only
/// seam into production <c>Program.cs</c> — the probes live in the test assembly and vanish with it.</para>
///
/// <para><b><c>/probe/cell</c> is deliberately written the way W2-B must write the real one</b>, so it is
/// also the worked example: call the SERVICE (never the repository), check <c>Ok</c> and return 400 on a
/// broken rule, let <c>ConcurrencyConflictException</c> escape to the mapper, take the actor from
/// <c>IClientContext</c> and never from the body, and hand back the version the checked call returned
/// rather than re-reading it.</para></summary>
internal sealed class ProbeEndpointsFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        // Run Program.cs's own pipeline first (ExceptionMapper -> auth -> the API group). UseRouting has
        // stashed the IEndpointRouteBuilder by then and the endpoint table is read lazily per request, so
        // routes added here join the same table the real endpoints live in — and sit INSIDE ExceptionMapper.
        next(app);

        app.UseEndpoints(endpoints =>
        {
            var probes = endpoints.MapGroup("").AddEndpointFilter<ClientContextFilter>();

            // Echoes the whole IClientContext, so a test can assert what the per-request filter published.
            probes.MapGet("/probe/context", (IClientContext ctx) => Results.Ok(new ProbeContext(
                ctx.UserId, ctx.UserName, ctx.IsAdmin, ctx.MemberTeamIds, ctx.ConnectionId)));

            // A version-checked timesheet cell save, through the real ITimeLogService.
            probes.MapPut("/probe/cell", async (
                [FromBody] ProbeCellRequest req,
                IClientContext ctx,
                ITimeLogService logs) =>
            {
                // THE ACTOR IS THE COOKIE, NEVER THE BODY.
                var result = await logs.SaveCellCheckedAsync(
                    ctx.UserId, req.TaskId, req.Date, req.Hours, req.ExpectedVersion);

                // The business-rule channel NEVER throws: ignore Ok and this returns 200 on a rejected
                // write, and the user watches their hours vanish.
                return result.Ok
                    ? Results.Ok(new SavedBody(result.RowVersion))
                    : Results.BadRequest(new ValidationBody(result.Error!));
            });

            // A version-checked clear, so a test can delete a cell and then conflict against the hole.
            probes.MapDelete("/probe/cell", async (
                [FromBody] ProbeClearRequest req,
                IClientContext ctx,
                ITimeLogService logs) =>
            {
                await logs.ClearCellCheckedAsync(ctx.UserId, req.TaskId, req.Date, req.ExpectedVersion);
                return Results.NoContent();
            });

            // A standup issue write, whose validation signals bad input by THROWING ArgumentException
            // rather than returning a result — the third error channel.
            probes.MapPost("/probe/issue", async (
                [FromBody] ProbeIssueRequest req,
                IStandupService standup) =>
            {
                var id = await standup.AddIssueAsync(req.EntryId, req.IssueText, null, req.Status);
                return Results.Ok(id);
            });

            // The Admin policy, on a route shaped exactly like the four /api/ops/* routes W2-D will write.
            // RequireClaim compares with StringComparer.Ordinal, so a claim written from a bool ("True")
            // makes this ALWAYS FAIL — a real admin gets a silent 403 with nothing logged.
            probes.MapGet("/probe/admin-only", () => Results.Ok(new { ok = true }))
                  .RequireAuthorization(AuthSetup.AdminPolicy);
        });
    };
}

internal sealed record ProbeContext(
    int UserId, string UserName, bool IsAdmin, IReadOnlyList<int> MemberTeamIds, string? ConnectionId);

internal sealed record ProbeCellRequest(int TaskId, DateOnly Date, decimal Hours, long? ExpectedVersion);

internal sealed record ProbeClearRequest(int TaskId, DateOnly Date, long ExpectedVersion);

internal sealed record ProbeIssueRequest(int EntryId, string IssueText, string Status);
