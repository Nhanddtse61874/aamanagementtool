using Dapper;
using TimesheetApp.Data.Repositories;
using Xunit;

namespace TimesheetApp.Tests.Data;

// M8.3 W0-T1: Users.password_hash / is_admin exist since v10 but NOTHING read them — no repository
// method, no entity property. So nobody could log in and nobody could set a password. These tests pin
// the credential surface that the API's auth path is built on.
//
// Real temp-file DB via TestDb (never :memory: — each connection would get its OWN database, so a
// two-connection race could not occur and the test would pass while asserting nothing).
public class UserCredentialsTests
{
    // v10 promotes MIN(id) to admin, but only on a database that already HAS users. A fresh TestDb is
    // empty at migration time, so nobody is an admin and is_admin must be set explicitly here.
    private static async Task MakeAdminAsync(TestDb db, int userId)
    {
        using var c = db.Create();
        await c.ExecuteAsync("UPDATE Users SET is_admin = 1 WHERE id = @id;", new { id = userId });
    }

    private static async Task<long> RowVersionAsync(TestDb db, int userId)
    {
        using var c = db.Create();
        return await c.ExecuteScalarAsync<long>(
            "SELECT row_version FROM Users WHERE id = @id;", new { id = userId });
    }

    private static async Task<string?> HashAsync(TestDb db, int userId)
    {
        using var c = db.Create();
        return await c.ExecuteScalarAsync<string?>(
            "SELECT password_hash FROM Users WHERE id = @id;", new { id = userId });
    }

    [Fact]
    public async Task Credentials_round_trip_for_a_user_with_a_hash()
    {
        using var db = await TestDb.CreateAsync();
        var repo = new UserRepository(db);
        var id = await db.SeedUserAsync("Alice", "alice");
        await MakeAdminAsync(db, id);

        await repo.SetPasswordHashAsync(id, "HASH-1");

        var creds = await repo.GetCredentialsAsync("alice");
        Assert.NotNull(creds);
        Assert.Equal(id, creds!.Id);
        Assert.Equal("Alice", creds.Name);
        Assert.Equal("HASH-1", creds.PasswordHash);
        Assert.True(creds.IsAdmin);
        Assert.True(creds.IsActive);
    }

    [Fact]
    public async Task GetCredentials_returns_null_for_an_unknown_username()
    {
        using var db = await TestDb.CreateAsync();
        var repo = new UserRepository(db);
        await db.SeedUserAsync("Alice", "alice");

        Assert.Null(await repo.GetCredentialsAsync("nobody"));
    }

    // Fail-CLOSED: a user who has never had a password gets a NULL hash, not an empty string. The auth
    // path must read that as "cannot log in", never as "any password matches".
    [Fact]
    public async Task GetCredentials_returns_a_null_hash_when_no_password_was_ever_set()
    {
        using var db = await TestDb.CreateAsync();
        var repo = new UserRepository(db);
        await db.SeedUserAsync("Bob", "bob");

        var creds = await repo.GetCredentialsAsync("bob");
        Assert.NotNull(creds);
        Assert.Null(creds!.PasswordHash);
        Assert.False(creds.IsAdmin);
    }

    // The repository projects is_active rather than filtering on it, so the caller can tell a
    // deactivated user (IsActive == false) apart from an unknown one (null) and choose what to say.
    [Fact]
    public async Task GetCredentials_still_resolves_a_deactivated_user_and_reports_IsActive_false()
    {
        using var db = await TestDb.CreateAsync();
        var repo = new UserRepository(db);
        var id = await db.SeedUserAsync("Gone", "gone", isActive: false);
        await repo.SetPasswordHashAsync(id, "HASH-X");

        var creds = await repo.GetCredentialsAsync("gone");
        Assert.NotNull(creds);
        Assert.False(creds!.IsActive);
        Assert.Equal("HASH-X", creds.PasswordHash);
    }

    // The wave's stated truth: true exactly ONCE, false every time after.
    [Fact]
    public async Task TryBootstrapAdminPassword_lands_once_then_never_again()
    {
        using var db = await TestDb.CreateAsync();
        var repo = new UserRepository(db);
        var id = await db.SeedUserAsync("Admin", "admin");

        Assert.True(await repo.TryBootstrapAdminPasswordAsync(id, "FIRST"));
        Assert.False(await repo.TryBootstrapAdminPasswordAsync(id, "SECOND"));
        Assert.False(await repo.TryBootstrapAdminPasswordAsync(id, "THIRD"));
    }

    // The point of `WHERE password_hash IS NULL`: the loser of the race is a NO-OP, not an overwrite.
    // If this ever regressed to a read-then-write, the operator would hold one password and the database
    // would hold the other.
    [Fact]
    public async Task TryBootstrapAdminPassword_never_overwrites_the_password_that_won()
    {
        using var db = await TestDb.CreateAsync();
        var repo = new UserRepository(db);
        var id = await db.SeedUserAsync("Admin", "admin");

        await repo.TryBootstrapAdminPasswordAsync(id, "WINNER");
        await repo.TryBootstrapAdminPasswordAsync(id, "LOSER");

        Assert.Equal("WINNER", await HashAsync(db, id));
    }

    // Bootstrap must not resurrect a password slot that an admin has since set deliberately.
    [Fact]
    public async Task TryBootstrapAdminPassword_is_a_no_op_once_a_password_exists()
    {
        using var db = await TestDb.CreateAsync();
        var repo = new UserRepository(db);
        var id = await db.SeedUserAsync("Admin", "admin");
        await repo.SetPasswordHashAsync(id, "CHOSEN");

        Assert.False(await repo.TryBootstrapAdminPasswordAsync(id, "BOOTSTRAP"));
        Assert.Equal("CHOSEN", await HashAsync(db, id));
    }

    // Both writes are BUMP-ONLY (they carry no client version), so both must still increment row_version:
    // a write that changes a row without bumping is a lost update waiting to happen.
    [Fact]
    public async Task SetPasswordHash_and_successful_bootstrap_both_bump_row_version()
    {
        using var db = await TestDb.CreateAsync();
        var repo = new UserRepository(db);
        var id = await db.SeedUserAsync("Alice", "alice");
        var v0 = await RowVersionAsync(db, id);

        Assert.True(await repo.TryBootstrapAdminPasswordAsync(id, "H1"));
        var v1 = await RowVersionAsync(db, id);
        Assert.Equal(v0 + 1, v1);

        await repo.SetPasswordHashAsync(id, "H2");
        Assert.Equal(v1 + 1, await RowVersionAsync(db, id));

        // The no-op bootstrap must not bump — it wrote nothing.
        Assert.False(await repo.TryBootstrapAdminPasswordAsync(id, "H3"));
        Assert.Equal(v1 + 1, await RowVersionAsync(db, id));
    }

    // /api/users must be able to show who is an admin — so User has to carry it out of every read path.
    [Fact]
    public async Task User_reads_project_IsAdmin()
    {
        using var db = await TestDb.CreateAsync();
        var repo = new UserRepository(db);
        var adminId = await db.SeedUserAsync("Admin", "admin");
        var plainId = await db.SeedUserAsync("Plain", "plain");
        await MakeAdminAsync(db, adminId);

        Assert.True((await repo.GetByIdAsync(adminId))!.IsAdmin);
        Assert.False((await repo.GetByIdAsync(plainId))!.IsAdmin);
        Assert.True((await repo.GetByUsernameAsync("admin"))!.IsAdmin);

        Assert.True((await repo.GetAllAsync()).Single(u => u.Id == adminId).IsAdmin);
        Assert.True((await repo.GetActiveAsync()).Single(u => u.Id == adminId).IsAdmin);
        Assert.False((await repo.GetActiveAsync()).Single(u => u.Id == plainId).IsAdmin);
    }

    // M11 (Users screen "everyone can log in" bug): the admin Users screen needs to tell a bare username
    // apart from an account that can actually authenticate, so User has to carry HasPassword out of every
    // read path -- same requirement, same shape, as User_reads_project_IsAdmin above. HasPassword is
    // `password_hash IS NOT NULL`; the hash itself never leaves UserRepository (see UserRaw.password_hash).
    [Fact]
    public async Task User_reads_project_HasPassword()
    {
        using var db = await TestDb.CreateAsync();
        var repo = new UserRepository(db);
        var withHashId = await db.SeedUserAsync("HasPass", "haspass");
        var noHashId = await db.SeedUserAsync("NoPass", "nopass");
        await repo.SetPasswordHashAsync(withHashId, "HASH-1");

        Assert.True((await repo.GetByIdAsync(withHashId))!.HasPassword);
        Assert.False((await repo.GetByIdAsync(noHashId))!.HasPassword);
        Assert.True((await repo.GetByUsernameAsync("haspass"))!.HasPassword);

        Assert.True((await repo.GetAllAsync()).Single(u => u.Id == withHashId).HasPassword);
        Assert.True((await repo.GetActiveAsync()).Single(u => u.Id == withHashId).HasPassword);
        Assert.False((await repo.GetActiveAsync()).Single(u => u.Id == noHashId).HasPassword);
    }
}
