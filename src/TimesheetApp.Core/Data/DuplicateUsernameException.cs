namespace TimesheetApp.Data;

/// <summary>
/// Thrown when a write would put two users on the same <c>Users.username</c>, compared
/// case-INsensitively. The v11 <c>UNIQUE INDEX ux_users_username</c> (<c>COLLATE NOCASE</c>) is the real
/// guarantee; this exception is how that DB-level violation — and the endpoint's own pre-write check —
/// reach the client as a clean 409 instead of a raw <c>SqliteException</c> 500.
/// </summary>
/// <remarks>
/// <para>
/// The bug it closes: <c>Users.username</c> was born as <c>windows_username TEXT</c> in the v1 DDL and was
/// only renamed by v10, so it never carried a unique index. Two rows sharing a username made
/// <c>UserRepository.GetCredentialsAsync</c> / <c>GetByUsernameAsync</c> — both
/// <c>QuerySingleOrDefaultAsync</c> over <c>WHERE username = @u</c> — THROW on the second row, i.e. a 500 on
/// login that locked BOTH users out.
/// </para>
/// <para>
/// Mapped by <c>ExceptionMapper</c> to HTTP 409, exactly like <see cref="ConcurrencyConflictException"/>.
/// A duplicate username is a conflict with the current state of the resource, not a missing-field error.
/// </para>
/// </remarks>
public sealed class DuplicateUsernameException : Exception
{
    /// <summary>The username the caller tried to claim (for logs); the user-facing text is <see cref="Message"/>.</summary>
    public string Username { get; }

    public DuplicateUsernameException(string username)
        : base("That username is already taken.")
        => Username = username;
}
