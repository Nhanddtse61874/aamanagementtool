namespace TimesheetApp.Api.Endpoints;

/// <summary>W2-C. Owns <c>/api/backlogs/*</c> · <c>/api/tasks/*</c>, INCLUDING tag ASSIGNMENT
/// (<c>PUT /api/backlogs/{id}/tags</c>). Tag CRUD (<c>/api/tags/*</c>) belongs to W2-D — mapping it here
/// too is an <c>AmbiguousMatchException</c>, i.e. HTTP 500 on every tags request, found only at the merge.
///
/// <para><b>A whole-record update overwrites EVERY column, including <c>team_id</c>.</b>
/// <c>UpdateCheckedAsync(Backlog, …)</c> writes all 15 fields, so a DTO that merely OMITS <c>teamId</c> maps
/// to <c>TeamId = null</c> — <c>team_id = NULL</c> — and the backlog drops out of every team and becomes
/// invisible to everyone, permanently, while every test still passes. Re-read the stored entity, apply only
/// the request's fields with <c>with { … }</c>, and never let <c>TeamId</c> come from the wire.</para>
///
/// <para><b>Audited writes must name the editor:</b> <c>UpdateCheckedAsync</c>, <c>UpdateStatusCheckedAsync</c>,
/// <c>SetTagsCheckedAsync</c>, <c>UpdateExtendedCheckedAsync</c> and <c>SetTaskTagsCheckedAsync</c> all take
/// <c>changedByUserId</c> / <c>changedByName</c> as OPTIONAL params defaulting to <c>null</c>. Omit them and
/// it compiles, the tests pass, and every web edit writes an anonymous audit row.</para>
///
/// <para><b>"Continue to next month" is <c>IBacklogContinuationService.ContinueAsync</c></b> — it copies
/// tags, copies not-Done tasks and writes the <c>continued</c> audit row. A raw INSERT does none of that.</para>
///
/// <para><b>There is no <c>DELETE /api/backlogs/{id}</c>.</b> <c>IBacklogRepository</c> has no delete at all
/// (recorded decision: backlogs are not soft-deletable). Do not add one.</para></summary>
public static class BacklogEndpoints
{
    public static IEndpointRouteBuilder MapBacklogEndpoints(this IEndpointRouteBuilder api)
    {
        // Wave 2 (W2-C).
        return api;
    }
}
