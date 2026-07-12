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
    /// <see cref="Message"/> carries the detail.</summary>
    public long Id { get; }

    /// <summary>The <c>row_version</c> the caller believed it was updating.
    /// <c>null</c> means the caller believed the row did not exist yet.</summary>
    public long? ExpectedVersion { get; }

    /// <summary><c>true</c> when the row is gone entirely; <c>false</c> when it exists but
    /// has moved on. Drives the wording the user sees, and the <c>deleted</c> flag in the
    /// 409 body.</summary>
    public bool Deleted { get; }

    public ConcurrencyConflictException(string table, long id, long? expectedVersion, bool deleted)
        : base(BuildMessage(table, id, expectedVersion, deleted))
    {
        Table = table;
        Id = id;
        ExpectedVersion = expectedVersion;
        Deleted = deleted;
    }

    private static string BuildMessage(string table, long id, long? expected, bool deleted)
    {
        var row = id > 0 ? $"{table} #{id}" : table;
        return deleted
            ? $"{row} no longer exists — it was deleted by someone else."
            : expected is null
                ? $"{row} already exists — someone else created it while you were looking."
                : $"{row} was changed by someone else (you last saw row_version {expected}).";
    }
}
