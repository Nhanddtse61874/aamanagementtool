using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.Services;

namespace TimesheetApp.Api.Infrastructure;

/// <summary>The per-request initializer. Runs on EVERY endpoint in the API group, and without it every
/// timesheet and standup endpoint silently returns empty.
///
/// <para><b>Nothing else calls these.</b> <c>ICurrentUserService.ResolveAsync()</c> and
/// <c>ICurrentTeamService.InitializeAsync(userId)</c> have no other caller in the API — the WPF shell used
/// to invoke them from <c>MainViewModel.InitializeAsync</c>. Skip them and: <c>ActiveTeamId</c> stays 0, so
/// <c>GetActiveForTimesheetAsync(0)</c> returns no tasks (its own doc: "teamId 0 ⇒ no tasks (empty, R6)");
/// <c>StandupService.AddEntryAsync</c> writes <c>TeamId: 0</c>, poisoning data; and
/// <c>ICurrentUserService.Current</c> is null, so the standup owner gate compares against nothing.</para>
///
/// <para>Order is load-bearing: resolve the user, THEN the team (which needs the user id), THEN publish
/// both plus the authorization bound into <see cref="IClientContext"/>.</para></summary>
public sealed class ClientContextFilter : IEndpointFilter
{
    private readonly ICurrentUserService _currentUser;
    private readonly ICurrentTeamService _currentTeam;
    private readonly ITeamRepository _teams;
    private readonly ApiClientContext _context;

    public ClientContextFilter(
        ICurrentUserService currentUser,
        ICurrentTeamService currentTeam,
        ITeamRepository teams,
        ApiClientContext context)
    {
        _currentUser = currentUser;
        _currentTeam = currentTeam;
        _teams = teams;
        _context = context;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;

        // AllowAnonymous endpoints inside the group (login, health) have no user to resolve. The
        // authorization middleware runs BEFORE endpoint filters, so anything that requires auth and
        // lacks a cookie has already been rejected with 401 by the time we get here.
        if (http.User.Identity?.IsAuthenticated != true)
            return await next(ctx);

        // The cookie names a user who may have been renamed or soft-deleted since it was issued.
        // NeedsSelection means the lookup found nobody -> Current is null -> UserId would be 0 ->
        // we would write timesheet rows and standup entries on behalf of a user who does not exist.
        // 401 immediately; do not proceed.
        var resolved = await _currentUser.ResolveAsync();
        if (resolved.Outcome != CurrentUserOutcome.Resolved || resolved.User is null)
            return Results.Unauthorized();

        var user = resolved.User;

        // ActiveTeamId is a SYNC property whose resolution needs an ASYNC database read, so it cannot
        // lazily resolve and must be eagerly initialized here, once, per request.
        await _currentTeam.InitializeAsync(user.Id);

        // THE AUTHORIZATION BOUND. ITeamRepository — not IUserRepository, where an agent naturally looks.
        var memberTeamIds = await _teams.GetTeamIdsForUserAsync(user.Id);

        _context.Populate(
            user.Id,
            user.Name,
            user.IsAdmin,
            memberTeamIds,
            // Not on HttpContext: the SignalR JS client sends its connection id as a header, and something
            // has to read it out. If that reader is not here, every endpoint author invents their own.
            http.Request.Headers["X-Connection-Id"].FirstOrDefault());

        return await next(ctx);
    }
}
