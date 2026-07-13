using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using TimesheetApp.Data.Repositories;

namespace TimesheetApp.Api.Hubs;

/// <summary>The live cross-user push channel. Replaces <c>DataChangedMessage</c> over
/// <c>WeakReferenceMessenger</c> — in-process only, which is precisely why, in the WPF app today, two users
/// never see each other's changes without reloading.
///
/// <para><b>SignalR hubs do NOT run endpoint filters.</b> <c>IClientContext</c> is populated by
/// <c>ClientContextFilter</c>, which only runs on Minimal API routes — so on a hub connection it stays at
/// its unpopulated defaults (<c>UserId: 0</c>, <c>MemberTeamIds: []</c>) with no error anywhere, and a hub
/// that trusted it would join every connection to zero groups. This hub resolves the caller and their teams
/// itself, from the authenticated <see cref="HubCallerContext.User"/> and
/// <c>ITeamRepository.GetTeamIdsForUserAsync</c> — the same authorization-bound source
/// <c>IClientContext.MemberTeamIds</c> reads on the HTTP side, so the bound cannot drift between the two.</para>
///
/// <para><b>Group membership is lost on every reconnect.</b> A new connection gets a new
/// <see cref="HubCallerContext.ConnectionId"/> and starts in zero groups — reconnecting to the same hub URL
/// does not restore prior membership. Rejoining in <see cref="OnConnectedAsync"/> (rather than assuming it
/// persists) is what keeps cross-user sync alive across a Wi-Fi blip instead of silently dying after one.</para></summary>
[Authorize]
public sealed class DataHub : Hub
{
    private readonly ITeamRepository _teams;

    public DataHub(ITeamRepository teams)
    {
        _teams = teams;
    }

    public override async Task OnConnectedAsync()
    {
        var userId = ResolveUserId();
        if (userId is int uid)
        {
            var teamIds = await _teams.GetTeamIdsForUserAsync(uid);
            foreach (var teamId in teamIds)
                await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(teamId));
        }

        await base.OnConnectedAsync();
    }

    /// <summary>The SignalR group a team-scoped broadcast is sent to. Internal and shared with
    /// <c>SignalRChangeNotifier</c> (via <c>InternalsVisibleTo</c>) so the join side and the send side can
    /// never compute the group name differently.</summary>
    internal static string GroupName(int teamId) => $"team-{teamId}";

    /// <summary><c>[Authorize]</c> guarantees an authenticated principal, and every cookie this app issues
    /// carries <see cref="ClaimTypes.NameIdentifier"/> as the user id (see
    /// <c>AuthSetup.MapAuthMechanism</c>), so this should always resolve for a connection that made it past
    /// authorization. The null path is defensive, not a normal one: it degrades to "joins no team groups"
    /// rather than throwing and tearing the connection down.</summary>
    private int? ResolveUserId()
    {
        var raw = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return int.TryParse(raw, out var id) ? id : null;
    }
}
