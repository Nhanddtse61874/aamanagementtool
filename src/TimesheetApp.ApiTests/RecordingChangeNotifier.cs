using TimesheetApp.Api.Infrastructure;
using TimesheetApp.Services;

namespace TimesheetApp.ApiTests;

/// <summary>One recorded <see cref="IChangeNotifier.DataChangedAsync"/> call — the whole argument list,
/// so a test can assert the CONTRACT and not merely that "something fired".</summary>
public sealed record NotifyCall(DataKind Kind, int TeamId, string? ExceptConnectionId);

/// <summary>The <see cref="IChangeNotifier"/> test double, and the reason it exists.
///
/// <para><b>Production registers <see cref="NoopChangeNotifier"/></b> (Program.cs) — <c>DataChangedAsync</c>
/// is <c>Task.CompletedTask</c>, so in the test host every one of the 44 notify calls across the four
/// endpoint files is <b>unobservable by construction</b>. Not "untested": UNTESTABLE. A test could not have
/// caught a missing notify, a wrong <c>DataKind</c>, a wrong <c>teamId</c>, or a dropped
/// <c>exceptConnectionId</c> even if someone had tried to write one — which is the same shape as the
/// text-file-pretending-to-be-SQLite fixture that hid a data-loss bug for four phases in M8.2.</para>
///
/// <para>Registered through <c>ConfigureTestServices</c> (see <see cref="ApiFactory"/>), which runs AFTER
/// <c>Program.cs</c> has built its service collection — so it can <c>Replace</c> the Noop without
/// <c>Program.cs</c> carrying a test-only seam.</para>
///
/// <para><b>Locked</b> because the host serves requests on the thread pool and <c>/api/smartfill/apply</c>
/// notifies once per affected team from a single request; an unsynchronized <c>List&lt;T&gt;</c> would make
/// this class the flake, not the code under test.</para></summary>
public sealed class RecordingChangeNotifier : IChangeNotifier
{
    private readonly List<NotifyCall> _calls = new();
    private readonly object _gate = new();

    /// <summary>Every call so far, in order. A SNAPSHOT — enumerate it freely while the host is still
    /// running.</summary>
    public IReadOnlyList<NotifyCall> Calls
    {
        get { lock (_gate) return _calls.ToList(); }
    }

    /// <summary>Drops everything recorded so far. Call it after ARRANGE and before ACT so an assertion
    /// reads only the notifications the act itself produced.</summary>
    public void Clear()
    {
        lock (_gate) _calls.Clear();
    }

    public Task DataChangedAsync(DataKind kind, int teamId, string? exceptConnectionId = null)
    {
        lock (_gate) _calls.Add(new NotifyCall(kind, teamId, exceptConnectionId));
        return Task.CompletedTask;
    }
}
