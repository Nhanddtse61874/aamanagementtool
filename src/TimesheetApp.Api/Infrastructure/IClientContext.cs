namespace TimesheetApp.Api.Infrastructure;

/// <summary>The ONE place a request's identity, its authorization bound, and its SignalR connection id
/// live. SCOPED — populated once per authenticated request by <see cref="ClientContextFilter"/>.
///
/// <para><b>Why <see cref="MemberTeamIds"/> is not called "TeamIds".</b> Three different things were
/// being conflated, and collapsing any two of them is a data leak:</para>
/// <list type="number">
/// <item><b>Authorization bound</b> — the user's memberships. That is THIS property. Its source is
/// <c>ITeamRepository.GetTeamIdsForUserAsync</c> — NOT <c>IUserRepository</c>.</item>
/// <item><b>Working scope</b> — <c>ICurrentTeamService.ActiveTeamId</c>, a single int, read
/// <i>internally</i> by TimeLogService (4 sites) and StandupService (7 sites). Not on this interface.</item>
/// <item><b>Filter</b> — the client's multi-select on the query string. ATTACKER-CONTROLLED. It must be
/// INTERSECTED with <see cref="MemberTeamIds"/> before it reaches any repository, and it must never be
/// passed as <c>null</c> (null means "no filter", i.e. every team, every user).</item>
/// </list>
/// </summary>
public interface IClientContext
{
    /// <summary>The acting user, from the auth cookie. NEVER bind an actor id from a request body:
    /// TimeLogService's <c>userId</c> parameter is always this value.</summary>
    int UserId { get; }

    /// <summary>The acting user's display name — the <c>changedByName</c> on every audited write.</summary>
    string UserName { get; }

    /// <summary>Read fresh from the database on every request (so a demotion takes effect immediately),
    /// unlike the <c>is_admin</c> cookie claim the "Admin" policy gates on, which is fixed at login.</summary>
    bool IsAdmin { get; }

    /// <summary>THE AUTHORIZATION BOUND. Source: <c>ITeamRepository.GetTeamIdsForUserAsync</c>.</summary>
    IReadOnlyList<int> MemberTeamIds { get; }

    /// <summary>The caller's SignalR connection id, read from the <c>X-Connection-Id</c> request header
    /// (it is NOT on HttpContext). <c>IChangeNotifier.DataChangedAsync(…, exceptConnectionId)</c> needs it
    /// to keep a mutation from echoing back to the user who made it — an echo re-fetches and clobbers the
    /// very conflict dialog a 409 just raised. Null when the client has no live hub connection.</summary>
    string? ConnectionId { get; }
}
