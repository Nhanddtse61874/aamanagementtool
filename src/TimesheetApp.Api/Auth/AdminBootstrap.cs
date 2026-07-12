using System.Security.Cryptography;
using TimesheetApp.Data.Repositories;

namespace TimesheetApp.Api.Auth;

/// <summary>Gives the designated admin a password on first start — WITHOUT a persistent secret anywhere.
///
/// <para>Schema v10 added <c>password_hash</c> and promoted the lowest-id user to <c>is_admin = 1</c>, but
/// gave nobody a hash. As it stands NOBODY CAN LOG IN and nobody can set a password. This closes that,
/// once: generate a random password, claim the empty slot atomically, and write it to the console exactly
/// once. The operator reads it, logs in, changes it. <b>There is no secret to leak, because it exists only
/// in that one log line.</b> (An earlier draft read it from configuration — that puts a credential in a
/// file that tends to get committed.)</para>
///
/// <para><b>No "first login claims the account" flow.</b> On a shared internal network that lets anyone
/// claim a colleague's account before they do.</para>
///
/// <para><c>TryBootstrapAdminPasswordAsync</c> is a single <c>UPDATE … WHERE password_hash IS NULL</c>, so
/// the check and the write cannot be split: two processes racing an overlapped service restart produce one
/// winner and one no-op, not two passwords. It also means bootstrap can never OVERWRITE an existing
/// password — a user who has one simply matches no row.</para></summary>
public static class AdminBootstrap
{
    public static async Task EnsureAdminPasswordAsync(IUserRepository users, ILogger logger)
    {
        // No dedicated "get the admins" repository method exists, and Wave 1 may not modify Core. User.IsAdmin
        // is projected by GetAllAsync (W0 added it), so the designated admin is reachable without touching Core.
        var all = await users.GetAllAsync();
        var admins = all.Where(u => u.IsAdmin).ToList();

        if (admins.Count == 0)
        {
            // Migration v10 promotes MIN(id) to is_admin = 1, so ANY database with users has exactly one
            // admin. Zero admins therefore means one of two very different things.
            if (all.Count == 0)
            {
                // An empty database. Expected on a fresh install and in every test run. Worth saying, not
                // worth alarming about — though note that such a deployment cannot bootstrap a login over
                // HTTP at all (/api/users is admin-gated), so the first user has to come from the desktop app.
                logger.LogInformation(
                    "Admin bootstrap: no users yet, nothing to bootstrap. The first user must be created by " +
                    "the desktop app; the v10 migration will promote them to admin.");
            }
            else
            {
                // Users exist but none is an admin. The v10 migration should have made that impossible.
                logger.LogWarning(
                    "Admin bootstrap: {Count} user(s) exist but NONE has is_admin = 1, so nobody can reach " +
                    "the admin-only routes. Set is_admin = 1 on the intended administrator's row.", all.Count);
            }

            return;
        }

        foreach (var admin in admins)
        {
            if (string.IsNullOrWhiteSpace(admin.WindowsUsername))
            {
                logger.LogWarning(
                    "Admin bootstrap: user #{Id} ('{Name}') is an admin but has no username, so they cannot " +
                    "log in. Set Users.username for that row.", admin.Id, admin.Name);
                continue;
            }

            var credentials = await users.GetCredentialsAsync(admin.WindowsUsername);
            if (credentials is null || !string.IsNullOrEmpty(credentials.PasswordHash))
                continue;   // already has a password — nothing to do, and we must never overwrite it.

            var password = GeneratePassword();
            var claimed = await users.TryBootstrapAdminPasswordAsync(
                admin.Id, AuthSetup.HashPassword(password));

            if (!claimed)
            {
                // Another process won the race and set a different password. Theirs stands; say nothing more.
                logger.LogInformation(
                    "Admin bootstrap: password for '{Username}' was set by another process.",
                    admin.WindowsUsername);
                continue;
            }

            // THE ONLY TIME THIS VALUE EVER EXISTS OUTSIDE THE HASH. Not persisted, not in config, not
            // recoverable — if the operator misses it, they re-run bootstrap by nulling the column.
            logger.LogWarning(
                "======================================================================\n" +
                "  ADMIN PASSWORD GENERATED (shown ONCE — copy it now)\n" +
                "    username: {Username}\n" +
                "    password: {Password}\n" +
                "  Log in and change it immediately.\n" +
                "======================================================================",
                admin.WindowsUsername, password);
        }
    }

    /// <summary>24 URL-safe characters from a CSPRNG (~142 bits). Long enough that it never needs a policy,
    /// and it is replaced by the operator on first login anyway.</summary>
    private static string GeneratePassword() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(18))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
}
