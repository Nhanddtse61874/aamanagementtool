namespace TimesheetApp.Api.Contracts;

/// <summary>The 409 body. Produced ONLY by <see cref="Infrastructure.ExceptionMapper"/>, from a
/// <c>ConcurrencyConflictException</c> — endpoints let it throw and never catch it.
///
/// <para><b><see cref="Detail"/> is not garnish.</b> <c>TimeLogs</c> is keyed by the natural
/// <c>(user_id, task_id, work_date)</c> triple rather than an id, so its <see cref="Id"/> is <b>0</b>.
/// Without <see cref="Detail"/> a 409 on a timesheet cell tells the client only "something in TimeLogs
/// changed" — useless to a user with a week's grid in front of them. It is <c>null</c> for the id-keyed
/// tables, whose <c>Table #Id</c> already names the row.</para>
///
/// <para><b><see cref="Deleted"/> separates two situations</b> that <c>rowsAffected == 0</c> conflates:
/// "someone else edited this" and "someone else deleted this". The user needs different wording for each.</para>
///
/// <para><b>Recorded deviation from spec §8.1: there is no <c>current</c> field</b> (the server's current
/// row). The exception does not carry it, and a global handler cannot re-read it — that would need a
/// <c>Table → repository</c> dispatch map, and for <c>TimeLogs</c> (<see cref="Id"/> = 0)
/// <c>GetByIdAsync</c> is not even expressible. THE CLIENT RE-FETCHES ON 409 INSTEAD: one round-trip on a
/// by-definition-rare path, and it is not racy — a re-fetch returns a coherent (state, version) pair, and
/// if someone writes again in between, the next save 409s again, which is correct.</para>
///
/// <para><b>Known limitation, by design:</b> <c>TimeLogs</c> has no <c>changed_by</c> column, so the API
/// cannot name WHO changed a cell. "Someone else just changed this" can name the person for
/// <c>Backlogs</c> (via BacklogAudit) but not for a timesheet cell.</para></summary>
public sealed record ConflictBody(string Table, long Id, bool Deleted, string? Detail, string Message);
