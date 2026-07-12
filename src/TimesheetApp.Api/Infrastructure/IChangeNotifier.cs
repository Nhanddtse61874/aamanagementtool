using TimesheetApp.Services;

namespace TimesheetApp.Api.Infrastructure;

/// <summary>The seam that keeps Wave 3 out of the endpoint files.
///
/// <para>Endpoints call this after every successful mutation. Wave 3 replaces the implementation with the
/// real SignalR hub and touches NO endpoint file — which is the entire point of shipping the interface now.</para>
///
/// <para><c>DataKind</c> is Core's existing cross-tab-sync enum (<c>Services/DataChangedMessage.cs</c>) and
/// has EXACTLY 11 values. It is reused rather than redefined so the SignalR listener table ports 1:1 from
/// the in-process <c>WeakReferenceMessenger</c> it replaces.</para></summary>
public interface IChangeNotifier
{
    /// <param name="exceptConnectionId">The CALLER's SignalR connection id, from
    /// <c>IClientContext.ConnectionId</c>. Excluding the caller is not an optimization: without it the
    /// editing user receives their own echo, which re-fetches and CLOBBERS the very conflict dialog a 409
    /// just raised. <c>IHubContext</c> has no <c>Others</c>, so it has to be passed explicitly.</param>
    Task DataChangedAsync(DataKind kind, int teamId, string? exceptConnectionId = null);
}

/// <summary>Wave 1 / Wave 2 stand-in. Wave 3 swaps in the SignalR implementation.</summary>
public sealed class NoopChangeNotifier : IChangeNotifier
{
    public Task DataChangedAsync(DataKind kind, int teamId, string? exceptConnectionId = null) =>
        Task.CompletedTask;
}
