namespace TimesheetApp.Api.Infrastructure;

/// <summary>Scoped, mutable-once holder for <see cref="IClientContext"/>. Written exactly once per
/// request by <see cref="ClientContextFilter"/>; read-only to everything else.
///
/// <para>Registered as a CONCRETE scoped service with <c>IClientContext</c> forwarded to it, so the
/// filter can write it while endpoints only ever see the read-only interface.</para></summary>
public sealed class ApiClientContext : IClientContext
{
    public int UserId { get; private set; }
    public string UserName { get; private set; } = "";
    public bool IsAdmin { get; private set; }
    public IReadOnlyList<int> MemberTeamIds { get; private set; } = Array.Empty<int>();
    public string? ConnectionId { get; private set; }

    internal void Populate(
        int userId, string userName, bool isAdmin, IReadOnlyList<int> memberTeamIds, string? connectionId)
    {
        UserId = userId;
        UserName = userName;
        IsAdmin = isAdmin;
        MemberTeamIds = memberTeamIds;
        ConnectionId = connectionId;
    }
}
