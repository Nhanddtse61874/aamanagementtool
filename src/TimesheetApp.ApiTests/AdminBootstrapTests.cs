using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using TimesheetApp.Api.Contracts;
using TimesheetApp.Config;
using TimesheetApp.Data;
using Xunit;

namespace TimesheetApp.ApiTests;

/// <summary>Schema v10 added <c>password_hash</c> and promoted the lowest-id user to <c>is_admin = 1</c>, but
/// gave nobody a hash — so as it stands NOBODY CAN LOG IN and nobody can set a password. AdminBootstrap is
/// the only thing that breaks that deadlock, which makes it the one code path every deployment depends on
/// and the one nothing else would have exercised.</summary>
public sealed class AdminBootstrapTests
{
    /// <summary>End to end: the password that gets written to the console ACTUALLY WORKS. Asserting only
    /// that a hash appeared in the column would pass even if the logged password did not match it.</summary>
    [Fact]
    public async Task The_generated_admin_password_is_announced_once_and_actually_logs_in()
    {
        var root = ApiFactory.NewRoot();
        try
        {
            await SeedAdminWithNoPasswordAsync(root, "boss");

            using var factory = new ApiFactory(root);
            _ = factory.Services;   // boot the host -> AdminBootstrap runs

            var announcement = factory.Logs.SingleOrDefault(l => l.Contains("ADMIN PASSWORD GENERATED"));
            Assert.NotNull(announcement);

            var password = ExtractPassword(announcement!);
            Assert.False(string.IsNullOrWhiteSpace(password));

            using var client = factory.AnonymousClient();
            var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("boss", password));

            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            var body = await login.Content.ReadFromJsonAsync<LoginResponse>();
            Assert.True(body!.IsAdmin);
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>The <c>WHERE password_hash IS NULL</c> in <c>TryBootstrapAdminPasswordAsync</c> is what makes
    /// a restart safe: it can never OVERWRITE a password the operator has already changed. Without it, every
    /// reboot would silently reset the admin's password and lock them out of their own account.</summary>
    [Fact]
    public async Task A_restart_never_overwrites_an_existing_admin_password()
    {
        var root = ApiFactory.NewRoot();
        try
        {
            await SeedAdminWithNoPasswordAsync(root, "boss");

            string hashAfterFirstBoot;
            using (var first = new ApiFactory(root))
            {
                _ = first.Services;
                hashAfterFirstBoot = await ReadHashAsync(first, "boss");
                Assert.False(string.IsNullOrEmpty(hashAfterFirstBoot));
                Assert.Contains(first.Logs, l => l.Contains("ADMIN PASSWORD GENERATED"));
            }

            // The "reboot".
            using var second = new ApiFactory(root);
            _ = second.Services;

            Assert.Equal(hashAfterFirstBoot, await ReadHashAsync(second, "boss"));
            Assert.DoesNotContain(second.Logs, l => l.Contains("ADMIN PASSWORD GENERATED"));
        }
        finally
        {
            Cleanup(root);
        }
    }

    /// <summary>An admin who is an admin but has no <c>username</c> cannot be logged in as at all (login looks
    /// up by username), so bootstrap must say so rather than silently generating a password nobody can use.</summary>
    [Fact]
    public async Task An_admin_with_no_username_is_reported_not_silently_skipped()
    {
        var root = ApiFactory.NewRoot();
        try
        {
            await SeedAdminWithNoPasswordAsync(root, username: null);

            using var factory = new ApiFactory(root);
            _ = factory.Services;

            Assert.Contains(factory.Logs, l => l.Contains("has no username"));
            Assert.DoesNotContain(factory.Logs, l => l.Contains("ADMIN PASSWORD GENERATED"));
        }
        finally
        {
            Cleanup(root);
        }
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    /// <summary>Builds the database and its admin row BEFORE the API host starts, which is the only way to
    /// reproduce the real deployment: an existing desktop database, migrated to v10, whose admin has no hash.</summary>
    private static async Task SeedAdminWithNoPasswordAsync(string root, string? username)
    {
        Directory.CreateDirectory(root);

        var config = new JsonAppConfig(
            Path.Combine(root, "appsettings.json"), Path.Combine(root, "timesheet.db"));
        var connections = new SqliteConnectionFactory(config, SqliteProfile.Server);
        await new DatabaseInitializer(connections).InitializeAsync();

        using var db = connections.Create();
        await db.ExecuteAsync(
            @"INSERT INTO Users(name, username, is_active, is_admin, password_hash)
              VALUES('Boss', @username, 1, 1, NULL);",
            new { username });
    }

    private static async Task<string> ReadHashAsync(ApiFactory factory, string username)
    {
        using var db = factory.OpenDb();
        return await db.ExecuteScalarAsync<string>(
            "SELECT password_hash FROM Users WHERE username = @u;", new { u = username }) ?? "";
    }

    private static string ExtractPassword(string announcement) =>
        Regex.Match(announcement, @"password:\s*(\S+)").Groups[1].Value;

    private static void Cleanup(string root)
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // Temp file occasionally held briefly on Windows; safe to leave for the OS to reap.
        }
    }
}
