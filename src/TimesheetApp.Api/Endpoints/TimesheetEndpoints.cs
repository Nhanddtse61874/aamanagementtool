namespace TimesheetApp.Api.Endpoints;

/// <summary>W2-B. Owns <c>/api/timesheet/*</c> · <c>/api/smartfill/*</c> · <c>/api/reports/*</c> ·
/// <c>/api/export/*</c>.
///
/// <para><b>Call <c>ITimeLogService</c>, NEVER <c>ITimeLogRepository</c>.</b> The service holds the 8h/day
/// cap, the holiday guard, the weekend guard and the 1-decimal rounding. Go around it and a user logs 40
/// hours on a Sunday holiday. Use <c>SaveCellCheckedAsync</c> / <c>ClearCellCheckedAsync</c> —
/// the unchecked overloads are bump-only and silently win every race.</para>
///
/// <para><b>The actor is <c>IClientContext.UserId</c>, never a <c>userId</c> from the request body.</b>
/// <c>SaveCellCheckedAsync</c> takes <c>userId</c> as a parameter; binding it from the wire lets any user
/// log hours as any other user.</para>
///
/// <para><b><c>ExportFilter.TeamIds</c> is a trailing optional defaulting to <c>null</c>, and <c>null</c>
/// means NO FILTER — every team, every user.</b> The 4-arg positional ctor every WPF call site uses
/// compiles, looks complete, and exports the whole company. Always pass
/// <c>TeamIds: client ∩ ctx.MemberTeamIds</c>.</para></summary>
public static class TimesheetEndpoints
{
    public static IEndpointRouteBuilder MapTimesheetEndpoints(this IEndpointRouteBuilder api)
    {
        // Wave 2 (W2-B).
        return api;
    }
}
