namespace TimesheetApp.Data;

/// <summary>
/// Thrown when an optimistic-concurrency check fails on a versioned write: the row's
/// <c>row_version</c> no longer matches what the caller last read, or the row is gone.
/// The API maps this to HTTP 409 (spec §8.1).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Deleted"/> is not cosmetic. <c>rowsAffected == 0</c> conflates two different
/// situations — "someone else edited this" and "someone else deleted this" — and the user
/// needs different wording for each. Repositories therefore run one cheap existence check
/// on the conflict path (and only there) to tell them apart.
/// </para>
/// <para>
/// This is thrown ONLY by the check-and-bump path. System writes — reorders, soft-deletes,
/// backfills — use bump-only: they always increment <c>row_version</c> but never compare it,
/// because they carry no version from a client. Bumping without checking is safe; checking
/// without bumping is the bug this whole mechanism exists to prevent.
/// </para>
/// </remarks>
public sealed class ConcurrencyConflictException : Exception
{
    /// <summary>Table the conflict occurred on, e.g. <c>Backlogs</c>.</summary>
    public string Table { get; }

    /// <summary>Primary key of the row. For <c>TimeLogs</c>, whose key is the natural
    /// <c>(user_id, task_id, work_date)</c> triple rather than an id, this is 0 and
    /// <see cref="Detail"/> — which <see cref="Message"/> includes — carries the cell identity.</summary>
    public long Id { get; }

    /// <summary>Human-readable identity of the row when <see cref="Id"/> cannot name it — i.e. for a
    /// natural-keyed table. <c>null</c> for the id-keyed tables, whose <c>Table #Id</c> already says
    /// which row this is.</summary>
    /// <remarks>
    /// This exists because without it a 409 on a timesheet cell said only "TimeLogs was changed by
    /// someone else", naming no cell — useless to a user with a week's grid in front of them, and to
    /// anyone reading the log. <see cref="Id"/> is 0 there, so it could not fill the gap.
    /// </remarks>
    public string? Detail { get; }

    /// <summary>The <c>row_version</c> the caller believed it was updating.
    /// <c>null</c> means the caller believed the row did not exist yet.</summary>
    public long? ExpectedVersion { get; }

    /// <summary><c>true</c> when the row is gone entirely; <c>false</c> when it exists but
    /// has moved on. Drives the wording the user sees, and the <c>deleted</c> flag in the
    /// 409 body.</summary>
    public bool Deleted { get; }

    /// <param name="detail">Identity of the row for a natural-keyed table (see <see cref="Detail"/>).
    /// Omit for the id-keyed tables — the existing 4-argument shape keeps working unchanged.</param>
    public ConcurrencyConflictException(string table, long id, long? expectedVersion, bool deleted,
        string? detail = null)
        : base(BuildMessage(table, id, expectedVersion, deleted, detail))
    {
        Table = table;
        Id = id;
        ExpectedVersion = expectedVersion;
        Deleted = deleted;
        Detail = detail;
    }

    private static string BuildMessage(string table, long id, long? expected, bool deleted, string? detail)
    {
        // id 0 => natural-keyed (TimeLogs): `detail` is the only thing that can name the row, so the
        // XML doc on Id is only true if it actually lands in the message. It does, here.
        var row = id > 0 ? $"{table} #{id}" : table;
        if (detail is { Length: > 0 }) row = $"{row} ({detail})";
        return deleted
            ? $"{row} no longer exists — it was deleted by someone else."
            : expected is null
                ? $"{row} already exists — someone else created it while you were looking."
                : $"{row} was changed by someone else (you last saw row_version {expected}).";
    }
}
