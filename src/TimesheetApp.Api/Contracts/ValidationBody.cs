namespace TimesheetApp.Api.Contracts;

/// <summary>The 400 body. ONE shape for every rejected input, so the client has one thing to render.
///
/// <para><b>This is the channel that never throws</b>, and that is exactly why it is dangerous. A broken
/// business rule — the 8h/day cap, a holiday, a weekend, more than one decimal — comes back from
/// <c>ITimeLogService</c> as a RETURN VALUE: <c>SaveResult(bool Ok, string? Error)</c> or
/// <c>SaveCellResult(bool Ok, string? Error, long RowVersion)</c>. An endpoint that ignores <c>Ok</c>
/// compiles, passes its tests, RETURNS 200 — and the user watches their hours vanish, because nothing was
/// written and nothing said so.</para>
///
/// <para>The second producer is <c>ArgumentException</c> from <c>IStandupService</c> (invalid section,
/// invalid status, empty issue text — 9 throw sites). <see cref="Infrastructure.ExceptionMapper"/> maps it
/// here too: bad input is the caller's fault whether the service signals it by return value or by throw.</para>
/// </summary>
public sealed record ValidationBody(string Error);
