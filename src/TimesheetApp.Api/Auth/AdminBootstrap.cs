using System.Security.Cryptography;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;

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
    /// <summary>The seeded account on a brand-new database. Deliberately easy to type — see
    /// <see cref="SeedFirstAdminAsync"/> for why a default credential is the right call here, and how to
    /// override it.</summary>
    public const string DefaultUsername = "admin";
    public const string DefaultPassword = "admin";

    /// <summary>Set <c>TimesheetApp:SeedFirstAdmin=false</c> to suppress <see cref="SeedFirstAdminAsync"/>
    /// entirely. Defaults to TRUE — a fresh install must be loggable-into. It exists for the deployment that
    /// provisions accounts out of band and does not want a default-credential account to exist even briefly,
    /// and for the API test suite, whose ~489 tests are written against a database that contains exactly what
    /// each test seeds and nothing else.</summary>
    private static bool SeedFirstAdminEnabled(IConfiguration? config) =>
        !bool.TryParse(config?["TimesheetApp:SeedFirstAdmin"], out var enabled) || enabled;

    /// <returns><c>true</c> only when a first admin was CREATED on an empty database. The caller must then
    /// re-run team bootstrap — see Program.cs — because the seeded admin is otherwise in no team.</returns>
    public static async Task<bool> EnsureAdminPasswordAsync(
        IUserRepository users, ILogger logger, IConfiguration? config = null)
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
                // 🔴 A COMPLETELY EMPTY DATABASE — AND UNTIL M9 THIS WAS A DEADLOCK.
                //
                // The old code logged "nothing to bootstrap" and returned, which meant a fresh clone could
                // NEVER be logged into:
                //   - v10 promotes MIN(id) to admin. On zero rows MIN(id) is NULL, so it matches NOTHING.
                //   - POST /api/users is admin-gated, so the first user cannot be created over HTTP either.
                //   - The old comment's escape hatch — "the first user must come from the desktop app" —
                //     stops existing the moment the desktop app is deleted (M10).
                //
                // So: SEED ONE. Nobody can be locked out of a database that has nobody in it.
                if (!SeedFirstAdminEnabled(config))
                {
                    logger.LogWarning(
                        "Admin bootstrap: the database has NO USERS and first-admin seeding is disabled " +
                        "(TimesheetApp:SeedFirstAdmin=false), so NOBODY CAN LOG IN. Provision an " +
                        "administrator out of band, or remove the setting.");
                    return false;
                }

                return await SeedFirstAdminAsync(users, logger, config);
            }
            else
            {
                // Users exist but none is an admin. The v10 migration should have made that impossible.
                logger.LogWarning(
                    "Admin bootstrap: {Count} user(s) exist but NONE has is_admin = 1, so nobody can reach " +
                    "the admin-only routes. Set is_admin = 1 on the intended administrator's row.", all.Count);
            }

            return false;
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

        return false;
    }

    /// <summary>Create the first admin on a database that has NO USERS AT ALL.
    ///
    /// <para>🔴 <b>Why a DEFAULT credential here, when the rest of this class works so hard to avoid one?</b>
    /// Because the two cases are not the same. Bootstrapping a password onto an <i>existing</i> admin has a
    /// safe alternative — generate it, print it once, never persist it. Seeding the <i>first user of an empty
    /// database</i> has no such alternative: there is nobody to tell, no session to attach it to, and a random
    /// password printed to a console that a developer cloning the repo will never scroll back to is just a
    /// lock with the key thrown away. An empty database has nothing to protect and nobody to protect it from
    /// — the risk only begins once real data is in it, by which time this has long since been changed.</para>
    ///
    /// <para><b>It only ever runs on a database with ZERO users.</b> It cannot overwrite anyone, it cannot
    /// re-seed, and it cannot resurrect a deleted admin. The moment one user exists, this path is dead.</para>
    ///
    /// <para><b>Override for a real deployment</b> — <c>TimesheetApp:BootstrapAdminUsername</c> and
    /// <c>TimesheetApp:BootstrapAdminPassword</c>, in <c>appsettings.json</c> or on the command line. Set
    /// <c>TimesheetApp:SeedFirstAdmin=false</c> to suppress it entirely.</para>
    ///
    /// <para>⚠️ <b>NOT safe against two processes cold-starting the SAME empty database simultaneously.</b>
    /// The atomic <c>UPDATE … WHERE password_hash IS NULL</c> below protects the password OF A GIVEN ROW; it
    /// does NOT stop two processes from INSERTing two rows. <c>Users.username</c> carries no UNIQUE index
    /// (it is born as <c>windows_username TEXT</c> in the v1 DDL and merely renamed by v10), so both INSERTs
    /// succeed, each claims the password on its OWN fresh row, and the database ends up with TWO users named
    /// "admin" — at which point <c>GetCredentialsAsync</c>'s <c>QuerySingleOrDefaultAsync</c> throws and
    /// nobody can log in at all. Closing this properly means a UNIQUE index on <c>Users.username</c> (a
    /// schema migration, and one that would also close the same hole in <c>POST /api/users</c>) — deliberately
    /// out of scope here. In practice the API is started once, by one process, which is why this ships.</para></summary>
    private static async Task<bool> SeedFirstAdminAsync(
        IUserRepository users, ILogger logger, IConfiguration? config)
    {
        var username = config?["TimesheetApp:BootstrapAdminUsername"] ?? DefaultUsername;
        var password = config?["TimesheetApp:BootstrapAdminPassword"] ?? DefaultPassword;

        // VERIFIED against UserRepository.InsertAsync's SQL, not assumed: it writes ONLY (name, username,
        // is_active). password_hash is `TEXT` with no DEFAULT -> the new row's hash is NULL, which is exactly
        // what TryBootstrapAdminPasswordAsync's `WHERE password_hash IS NULL` needs to match. is_admin is
        // `INTEGER NOT NULL DEFAULT 0` -> the new row is NOT an admin, so SetIsAdminCheckedAsync below is
        // REQUIRED, not belt-and-braces. row_version is `INTEGER NOT NULL DEFAULT 1` and GetByIdAsync
        // projects it, so the checked write below has a real version to match on (never 0).
        var id = await users.InsertAsync(new User(0, "Administrator", username, IsActive: true));

        var seeded = await users.GetByIdAsync(id)
            ?? throw new InvalidOperationException($"Seeded admin #{id} vanished between INSERT and SELECT.");

        await users.SetIsAdminCheckedAsync(id, isAdmin: true, expectedVersion: seeded.RowVersion);

        var claimed = await users.TryBootstrapAdminPasswordAsync(id, AuthSetup.HashPassword(password));
        if (!claimed)
        {
            // Unreachable on the single-process path (we just INSERTed this row, so its hash is NULL). Kept
            // fail-closed rather than assumed-impossible.
            logger.LogWarning(
                "Admin bootstrap: could not claim the password slot on the admin we just created (#{Id}).", id);
            return false;
        }

        var isDefault = password == DefaultPassword;

        logger.LogWarning(
            "======================================================================\n" +
            "  FIRST ADMIN CREATED — the database was empty.\n" +
            "    username: {Username}\n" +
            "    password: {Password}\n" +
            "{Notice}" +
            "======================================================================",
            username,
            password,
            isDefault
                ? "\n  🔴 THIS IS THE DEFAULT PASSWORD. CHANGE IT BEFORE ANY REAL DATA GOES IN.\n" +
                  "     Override: TimesheetApp:BootstrapAdminUsername / :BootstrapAdminPassword\n"
                : "\n  Set from configuration. Log in and verify.\n");

        return true;
    }

    /// <summary>24 URL-safe characters from a CSPRNG (~142 bits). Long enough that it never needs a policy,
    /// and it is replaced by the operator on first login anyway.</summary>
    private static string GeneratePassword() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(18))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
}
