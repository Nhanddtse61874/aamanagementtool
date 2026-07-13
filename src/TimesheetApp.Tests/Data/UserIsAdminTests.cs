using Dapper;
using TimesheetApp.Data;
using TimesheetApp.Data.Repositories;
using Xunit;

namespace TimesheetApp.Tests.Data;

// M9 (P1b): Users.is_admin has existed since v10, but until now the ONLY statement in the entire
// codebase that wrote it was the v10 migration itself (`UPDATE Users SET is_admin = 1 WHERE id =
// (SELECT MIN(id) FROM Users)`). So admin was granted exactly once, by a migration, and was
// un-grantable and un-revocable at runtime — there was no way to appoint a second admin, and no way
// to demote the first one. SetIsAdminAsync is the write path; these tests pin it.
//
// CHECK-AND-BUMP, mirroring UpdateNameCheckedAsync: lands only at expectedVersion, returns the new
// version, throws ConcurrencyConflictException on stale/missing.
public class UserIsAdminTests
{
    private static async Task<long> RowVersionAsync(TestDb db, int userId)
    {
        using var c = db.Create();
        return await c.ExecuteScalarAsync<long>(
            "SELECT row_version FROM Users WHERE id = @id;", new { id = userId });
    }

    [Fact] // The grant lands, is readable back through the normal read path, and returns the new version.
    public async Task SetIsAdmin_grants_and_returns_the_bumped_row_version()
    {
        using var db = await TestDb.CreateAsync();
        var repo = new UserRepository(db);
        var id = await db.SeedUserAsync("Alice", "alice");
        var v0 = await RowVersionAsync(db, id);

        Assert.False((await repo.GetByIdAsync(id))!.IsAdmin);   // nobody is admin on a fresh DB

        var v1 = await repo.SetIsAdminAsync(id, isAdmin: true, expectedVersion: v0);

        Assert.Equal(v0 + 1, v1);
        Assert.Equal(v1, await RowVersionAsync(db, id));         // the RETURNING value is the real version
        Assert.True((await repo.GetByIdAsync(id))!.IsAdmin);
    }

    // Revoke is the half that matters most: a granted admin who cannot be demoted is a permanent
    // privilege. The returned version must chain, so a caller can grant then revoke without re-reading.
    [Fact]
    public async Task SetIsAdmin_revokes_and_the_returned_version_chains()
    {
        using var db = await TestDb.CreateAsync();
        var repo = new UserRepository(db);
        var id = await db.SeedUserAsync("Alice", "alice");
        var v0 = await RowVersionAsync(db, id);

        var v1 = await repo.SetIsAdminAsync(id, isAdmin: true, expectedVersion: v0);
        Assert.True((await repo.GetByIdAsync(id))!.IsAdmin);

        // v1 came straight out of the previous write — no re-read, which is the point of returning it.
        var v2 = await repo.SetIsAdminAsync(id, isAdmin: false, expectedVersion: v1);

        Assert.Equal(v1 + 1, v2);
        Assert.False((await repo.GetByIdAsync(id))!.IsAdmin);
    }

    // The whole reason this is CHECKED: two admins racing on one user's row must not silently lose one
    // of the two decisions. The stale writer is rejected, and — critically — the row is UNCHANGED.
    [Fact]
    public async Task SetIsAdmin_rejects_a_stale_version_and_does_not_write()
    {
        using var db = await TestDb.CreateAsync();
        var repo = new UserRepository(db);
        var id = await db.SeedUserAsync("Alice", "alice");
        var stale = await RowVersionAsync(db, id);

        // Someone else grants admin first, moving the version on.
        await repo.SetIsAdminAsync(id, isAdmin: true, expectedVersion: stale);

        // The second admin still holds the OLD version and tries to revoke.
        var ex = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => repo.SetIsAdminAsync(id, isAdmin: false, expectedVersion: stale));

        Assert.Equal("Users", ex.Table);
        Assert.False(ex.Deleted);                                 // stale, not gone
        Assert.True((await repo.GetByIdAsync(id))!.IsAdmin);      // the losing revoke did NOT land
    }

    // A conflict on a row that no longer exists is reported as Deleted, not as a plain stale version —
    // the caller says "this user is gone", not "someone else edited them".
    [Fact]
    public async Task SetIsAdmin_reports_deleted_when_the_row_is_gone()
    {
        using var db = await TestDb.CreateAsync();
        var repo = new UserRepository(db);

        var ex = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => repo.SetIsAdminAsync(9999, isAdmin: true, expectedVersion: 1));

        Assert.True(ex.Deleted);
    }

    // Belt-and-braces: the write must not splash onto neighbouring rows. A single-row UPDATE with a
    // typo'd WHERE would make everyone an admin, and the grant test above would still pass.
    [Fact]
    public async Task SetIsAdmin_touches_only_the_target_row()
    {
        using var db = await TestDb.CreateAsync();
        var repo = new UserRepository(db);
        var target = await db.SeedUserAsync("Alice", "alice");
        var bystander = await db.SeedUserAsync("Bob", "bob");
        var bystanderV0 = await RowVersionAsync(db, bystander);

        await repo.SetIsAdminAsync(target, isAdmin: true, expectedVersion: await RowVersionAsync(db, target));

        Assert.True((await repo.GetByIdAsync(target))!.IsAdmin);
        Assert.False((await repo.GetByIdAsync(bystander))!.IsAdmin);         // not promoted
        Assert.Equal(bystanderV0, await RowVersionAsync(db, bystander));     // not even bumped
    }
}
