using Microsoft.AspNetCore.SignalR;
using TimesheetApp.Api.Hubs;
using TimesheetApp.Services;

namespace TimesheetApp.Api.Infrastructure;

/// <summary>The real <see cref="IChangeNotifier"/>. Replaces <see cref="NoopChangeNotifier"/> now that
/// <c>DataHub</c> exists — and touches NO endpoint file, which is the entire point of the Wave-1 seam.
///
/// <para>Team-scoped (<c>teamId &gt; 0</c>) goes to that team's SignalR group only — <c>DataHub.GroupName</c>,
/// the same name the hub joins connections to, so the two cannot compute it differently. The reserved
/// <c>teamId: 0</c> (entities with no team column: Tag, PcaContact, User, Team, TaskTemplate, DefaultTask,
/// Holiday) goes to every connected client via <c>AllExcept</c>, never a group — a global entity's change is
/// global by definition, so there is nothing to leak by broadcasting it.</para>
///
/// <para><c>IHubContext</c> has no "Others": excluding the acting caller's own connection — so an edit does
/// not echo back to the editor and re-fetch over the very conflict dialog its own 409 just raised — has to be
/// done explicitly via <c>GroupExcept</c> / <c>AllExcept</c>, which is why <c>exceptConnectionId</c> is
/// threaded through to both.</para></summary>
public sealed class SignalRChangeNotifier : IChangeNotifier
{
    /// <summary>The hub method name the client subscribes to via <c>connection.on(...)</c>. One named
    /// constant so the send side cannot drift from the (future Angular) receive side on the literal string.</summary>
    public const string ClientMethod = "DataChanged";

    private readonly IHubContext<DataHub> _hub;

    public SignalRChangeNotifier(IHubContext<DataHub> hub)
    {
        _hub = hub;
    }

    public Task DataChangedAsync(DataKind kind, int teamId, string? exceptConnectionId = null)
    {
        var except = exceptConnectionId is null
            ? Array.Empty<string>()
            : new[] { exceptConnectionId };

        var clients = teamId > 0
            ? _hub.Clients.GroupExcept(DataHub.GroupName(teamId), except)
            : _hub.Clients.AllExcept(except);

        return clients.SendAsync(ClientMethod, kind, teamId);
    }
}
