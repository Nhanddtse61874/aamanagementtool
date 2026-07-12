using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using TimesheetApp.Api.Infrastructure;

namespace TimesheetApp.ApiTests;

/// <summary>Test-only routes, mapped into the REAL pipeline (real auth, real ClientContextFilter, real
/// exception mapper, real services, real database) via an <see cref="IStartupFilter"/>.
///
/// <para><b>Why these exist.</b> Wave 1 deliberately ships the four <c>Endpoints/*.cs</c> files as empty
/// stubs — Wave 2 fills them in. But two Wave-1 contracts can only be proven over HTTP: that
/// <c>ClientContextFilter</c> actually populates <see cref="IClientContext"/>, and that a
/// <c>ConcurrencyConflictException</c> escaping an endpoint really becomes a 409 carrying the frozen body.
/// Rather than put endpoint bodies into files Wave 2 owns — or bake a test-only seam into production
/// <c>Program.cs</c> — the probes live in the test assembly and vanish with it.</para></summary>
internal sealed class ProbeEndpointsFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        // Run Program.cs's own pipeline first (UseAuthentication -> UseAuthorization -> the API group).
        // UseRouting has stashed the IEndpointRouteBuilder by then and the endpoint table is read lazily
        // per request, so routes added here join the same table the real endpoints live in.
        next(app);

        app.UseEndpoints(endpoints =>
        {
            var probes = endpoints.MapGroup("").AddEndpointFilter<ClientContextFilter>();

            // Echoes the whole IClientContext, so a test can assert what the per-request filter published.
            probes.MapGet("/probe/context", (IClientContext ctx) => Results.Ok(new ProbeContext(
                ctx.UserId, ctx.UserName, ctx.IsAdmin, ctx.MemberTeamIds, ctx.ConnectionId)));
        });
    };
}

internal sealed record ProbeContext(
    int UserId, string UserName, bool IsAdmin, IReadOnlyList<int> MemberTeamIds, string? ConnectionId);
