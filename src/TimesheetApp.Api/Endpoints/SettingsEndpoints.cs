namespace TimesheetApp.Api.Endpoints;

/// <summary>W2-D. Owns tag CRUD (<c>/api/tags/*</c>) · <c>/api/teams/*</c> · <c>/api/pca-contacts/*</c> ·
/// <c>/api/users/*</c> · <c>/api/templates/*</c> · <c>/api/holidays/*</c> · <c>/api/default-tasks/*</c> ·
/// <c>/api/standup/entries/*</c> · <c>/api/ops/*</c>.
///
/// <para><b>Standup issues are NESTED under the entry:</b>
/// <c>POST|PUT|DELETE /api/standup/entries/{entryId}/issues[/{issueId}]</c>. A flat
/// <c>/api/standup/issues/{id}</c> CANNOT BE AUTHORIZED AT ALL: <c>GetIssueAsync(issueId)</c> does not exist
/// anywhere in Core, so from an issue id alone there is no way to discover which team it belongs to. For
/// every issue write: (1) <c>GetEntryAsync(entryId)</c> and assert <c>.TeamId ∈ ctx.MemberTeamIds</c>;
/// (2) assert the issue actually belongs to that entry via <c>GetIssuesForEntriesAsync([entryId])</c> —
/// <c>entryId</c> is attacker-supplied too, so step 2 is not optional. Issues are collaborative by design
/// (no owner gate); the TEAM gate is the only one they get.</para>
///
/// <para><b>Call <c>IStandupService</c>, never <c>IStandupRepository</c></b> — the service holds the OWNER
/// GATE on entry writes. Go around it and a user edits a colleague's standup entry. Note
/// <c>StandupEntry</c> is deliberately UNVERSIONED (owner-gated, last-write-wins by design): ignore any
/// <c>rowVersion</c> a client sends for one. Issues ARE versioned — use <c>UpdateIssueCheckedAsync</c>.</para>
///
/// <para><b>After any DefaultTasks write, call <c>IDefaultTaskSyncService.SyncAsync()</c></b> — it reconciles
/// DefaultTasks into every team's DEFAULT backlog. Skip it and the change never reaches any team.</para>
///
/// <para><b><c>/api/ops/*</c> is exactly four routes, all <c>.RequireAuthorization(AuthSetup.AdminPolicy)</c>:</b>
/// <c>POST /retention/preview</c> · <c>POST /retention/run</c> · <c>POST /export/run</c> ·
/// <c>POST /backup/run</c>. <c>RetentionService</c> holds one <c>BEGIN IMMEDIATE</c> across six bulk DELETEs,
/// blocking every writer app-wide — wired straight into the request path, ONE ADMIN CLICK 500s EVERYONE
/// ELSE. Return <b>202 Accepted</b> and run it on a background queue.
/// <c>IBackupService.RestoreAsync</c> is NOT exposed in M8.3: it overwrites the live .db in place while the
/// API holds open connections, which corrupts live readers.</para>
///
/// <para><b>Never call any <c>IAppConfig.Set*</c> from an endpoint.</b> It is a process-wide singleton with
/// ten setters; on a server every one of them is cross-user state — one user toggling dark mode flips it for
/// everyone, and <c>SetDbPath</c> repoints the whole server's database.</para>
///
/// <para><c>Tag</c>, <c>PcaContact</c>, <c>User</c> and <c>Team</c> have no team column — they are global.
/// Team-checking them is meaningless; gate them on the Admin policy instead.</para></summary>
public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder api)
    {
        // Wave 2 (W2-D).
        return api;
    }
}
