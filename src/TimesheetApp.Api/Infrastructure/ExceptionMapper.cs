using TimesheetApp.Api.Contracts;
using TimesheetApp.Data;

namespace TimesheetApp.Api.Infrastructure;

/// <summary>The global exception -> HTTP mapping. Registered as the OUTERMOST middleware, so it wraps every
/// endpoint. Endpoints LET THESE THROW — they never catch them.
///
/// <para><b>There are TWO error channels, and this middleware only sees one of them.</b></para>
/// <list type="table">
/// <item><term>Business rule</term><description>8h cap, holiday, weekend, decimals — a RETURN VALUE
/// (<c>SaveResult</c> / <c>SaveCellResult</c>), NOT an exception. It never reaches this middleware. The
/// ENDPOINT must check <c>Ok</c> and return 400 + <see cref="ValidationBody"/> itself. Ignore <c>Ok</c> and
/// the endpoint returns 200 on a rejected write.</description></item>
/// <item><term>Version conflict</term><description><c>ConcurrencyConflictException</c> — thrown. Mapped
/// here to 409 + <see cref="ConflictBody"/>.</description></item>
/// <item><term>Duplicate username</term><description><c>DuplicateUsernameException</c> (v11) — thrown by the
/// <c>/api/users</c> pre-check and, as the real backstop, by <c>UserRepository</c> when the
/// <c>ux_users_username</c> UNIQUE index fires. Mapped here to 409 + <see cref="ConflictBody"/>, so a
/// duplicate is never the raw <c>SqliteException</c> 500 it used to be.</description></item>
/// </list>
///
/// <para><b>Third channel, not in the original design and worth naming:</b> <c>IStandupService</c> throws
/// <c>ArgumentException</c> for invalid input in NINE places (invalid section, invalid status, empty issue
/// text) — including from <c>UpdateIssueCheckedAsync</c>. Unmapped, a bad standup status would come back as
/// a 500. It is a caller-input error exactly like the <c>SaveResult</c> channel, so it is mapped to the same
/// 400 + <see cref="ValidationBody"/>. The tradeoff is accepted knowingly: an <c>ArgumentException</c>
/// raised by an internal bug will also surface as a 400 rather than a 500. That is strictly better than
/// every invalid standup status returning 500, and the alternative — nine try/catch blocks spread across
/// four parallel agents — is how contracts diverge.</para></summary>
public sealed class ExceptionMapper
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionMapper> _logger;

    public ExceptionMapper(RequestDelegate next, ILogger<ExceptionMapper> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (ConcurrencyConflictException ex)
        {
            _logger.LogInformation(
                "Concurrency conflict on {Table} #{Id} (deleted: {Deleted}).", ex.Table, ex.Id, ex.Deleted);

            await WriteAsync(context, StatusCodes.Status409Conflict,
                new ConflictBody(ex.Table, ex.Id, ex.Deleted, ex.Detail, ex.Message));
        }
        catch (DuplicateUsernameException ex)
        {
            _logger.LogInformation("Rejected duplicate username '{Username}'.", ex.Username);

            // A duplicate username is a conflict with the current state (409), like the version conflict
            // above. Id 0 / Deleted false: no single row identifies the clash, and the row exists -- Message
            // carries the meaning. This is the DB-constraint backstop (v11); the /api/users endpoints also
            // pre-check and throw this themselves for a friendlier message on the common path.
            await WriteAsync(context, StatusCodes.Status409Conflict,
                new ConflictBody("Users", 0, false, null, ex.Message));
        }
        catch (ArgumentException ex)
        {
            _logger.LogInformation("Rejected input: {Message}", ex.Message);

            await WriteAsync(context, StatusCodes.Status400BadRequest, new ValidationBody(ex.Message));
        }
    }

    private static async Task WriteAsync<T>(HttpContext context, int statusCode, T body)
    {
        // If the endpoint already began streaming a response we cannot rewrite the status line; re-throwing
        // would be worse than a truncated body, so the connection is simply let go.
        if (context.Response.HasStarted) return;

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsJsonAsync(body);
    }
}
