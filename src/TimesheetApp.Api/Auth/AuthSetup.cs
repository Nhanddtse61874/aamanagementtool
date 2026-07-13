using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using TimesheetApp.Api.Contracts;
using TimesheetApp.Api.Infrastructure;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Models;
using TimesheetApp.Services;

namespace TimesheetApp.Api.Auth;

/// <summary>Cookie authentication over the existing <c>Users</c> table, plus the two sign-in/sign-out
/// routes. No Identity stack, no Identity schema, no EF Core — only <see cref="PasswordHasher{TUser}"/>
/// (PBKDF2 / HMAC-SHA512 / 100,000 iterations on .NET 8), which needs no DI and ignores its TUser argument.
///
/// <para><b>Route ownership.</b> <c>/api/auth/login</c> and <c>/api/auth/logout</c> live HERE, not in
/// <c>Endpoints/AuthEndpoints.cs</c>: they are the auth MECHANISM (they mint and clear the cookie this file
/// configures), and every auth test in the suite depends on them. The FEATURE routes on <c>/api/auth/*</c> —
/// <c>/api/me</c>, self set-password, admin set-password-for-user — belong to <c>AuthEndpoints.cs</c>.
/// Wave 2 must NOT re-map login or logout: two handlers on one route is an
/// <c>AmbiguousMatchException</c>, i.e. HTTP 500 on every login.</para></summary>
public static class AuthSetup
{
    /// <summary>Authorization policy name for the destructive/admin-only routes.</summary>
    public const string AdminPolicy = "Admin";

    /// <summary>The admin claim type, and the ONLY value that passes the policy.
    ///
    /// <para><b>The literal "1" is load-bearing.</b> <c>RequireClaim</c> compares with
    /// <c>StringComparer.Ordinal</c>. Writing the claim from a <c>bool</c> yields <c>"True"</c>, the policy
    /// then NEVER matches, and a real admin gets a silent 403 with nothing logged.</para></summary>
    public const string AdminClaimType = "is_admin";
    public const string AdminClaimValue = "1";

    /// <summary>The hasher ignores its TUser argument entirely (see the ASP.NET Core source: neither
    /// HashPassword nor VerifyHashedPassword reads it), so one instance and one throwaway user is correct.</summary>
    private static readonly PasswordHasher<User> Hasher = new();
    private static readonly User HashUser = new(0, "", null, true);

    public static string HashPassword(string password) => Hasher.HashPassword(HashUser, password);

    /// <summary>Verifies a candidate password against a stored hash, through THE SAME hasher instance that
    /// <see cref="HashPassword"/> and the login path use. Verification cannot be done by re-hashing and
    /// comparing strings: <see cref="HashPassword"/> salts randomly on every call, so the same password
    /// hashes to a different string every time and a string compare would never match, for anyone.
    ///
    /// <para><b>FAIL CLOSED on a null/empty hash.</b> A null <c>password_hash</c> means "has never had a
    /// password set" — the state EVERY user is in on a freshly migrated v10 database — and it must NEVER
    /// verify, for ANY candidate, including null and empty string. Treating it as "any password matches" is
    /// an authentication bypass. The guard has to live here because <c>VerifyHashedPassword</c> THROWS
    /// <c>ArgumentNullException</c> on a null hash rather than returning <c>Failed</c> — so a caller that
    /// forgets to pre-check gets a safe <c>false</c> from this method instead of an exception.</para>
    ///
    /// <para><b><c>SuccessRehashNeeded</c> IS A SUCCESS.</b> <c>VerifyHashedPassword</c> has THREE outcomes,
    /// not two. A hash written under older options — an <c>IdentityV2</c> format marker, or a lower
    /// iteration count — verifies correctly but asks to be re-hashed. Comparing <c>== Success</c> would
    /// reject it, locking out every pre-existing user the day the hasher's options are ever changed. Hence
    /// <c>!= Failed</c>, which is exactly what the login path in <see cref="MapAuthMechanism"/> does; the
    /// two must not drift apart.</para></summary>
    public static bool VerifyPassword(string? hash, string? password)
    {
        if (string.IsNullOrEmpty(hash) || password is null)
            return false;

        return Hasher.VerifyHashedPassword(HashUser, hash, password) != PasswordVerificationResult.Failed;
    }

    public static IServiceCollection AddApiAuth(this IServiceCollection services, string keyRingPath)
    {
        // ---------------------------------------------------------------------------------------------
        // Data Protection. THE HIGHEST-SEVERITY GAP IN THE DESIGN, AND INVISIBLE UNTIL PRODUCTION.
        //
        // The auth cookie is encrypted with a Data Protection key ring. UNCONFIGURED, THAT RING LIVES IN
        // MEMORY — so every process restart invalidates every cookie and logs everyone out. The host is a
        // workstation: it reboots, it updates, it recycles. That is mass logouts roughly daily, with
        // NOTHING in the logs, silently defeating the "stay logged in" requirement that is the entire
        // reason a persistent cookie was chosen.
        //
        // SetApplicationName pins the key-ring "purpose" discriminator, which otherwise derives from the
        // content-root PATH — so a service that is ever moved or reinstalled elsewhere would silently stop
        // being able to decrypt its own existing cookies.
        // ---------------------------------------------------------------------------------------------
        Directory.CreateDirectory(keyRingPath);
        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath))
            .SetApplicationName("TimesheetApp");

        services
            .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.Cookie.Name = "TimesheetApp.Auth";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Lax;
                // HTTP is a knowingly accepted risk on the internal network (recorded in the spec):
                // SameAsRequest keeps the cookie usable over plain HTTP while still marking it Secure the
                // moment the deployment gains TLS.
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.ExpireTimeSpan = TimeSpan.FromDays(30);
                o.SlidingExpiration = true;

                // -------------------------------------------------------------------------------------
                // COOKIE AUTH DOES NOT 401 ON .NET 8 — IT 302s.
                //
                // The ONLY thing that triggers a 401 by default is the literal request header
                // `X-Requested-With: XMLHttpRequest`. Everything else gets Response.Redirect to
                // "/Account/Login" — a route that does not exist here — so the SPA sees 404, never 401,
                // and can never tell "logged out" from "broken". Microsoft changed this default only in
                // .NET 10.
                //
                // The twist that costs a day: the SignalR JS client DOES send that header, so the hub
                // 401s correctly while the API 404s. The asymmetry sends you hunting in the wrong file.
                // -------------------------------------------------------------------------------------
                o.Events.OnRedirectToLogin = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                o.Events.OnRedirectToAccessDenied = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });

        services.AddAuthorization(o =>
        {
            o.AddPolicy(AdminPolicy, p => p
                .RequireAuthenticatedUser()
                .RequireClaim(AdminClaimType, AdminClaimValue));

            // -----------------------------------------------------------------------------------------
            // FallbackPolicy applies to every endpoint WITHOUT its own authorization metadata, AND to
            // "requests served by other middleware after the authorization middleware, such as static
            // files". That is what makes the whole API secure-by-default — and it is also why login,
            // health, the SPA fallback and static files MUST be explicitly [AllowAnonymous]: without it a
            // logged-out user requesting index.html gets 401 and can never reach the form that would log
            // them in.
            //
            // (SignalR's /negotiate being blocked by this is CORRECT — leave it.)
            // -----------------------------------------------------------------------------------------
            o.FallbackPolicy = o.DefaultPolicy;
        });

        return services;
    }

    /// <summary>Sign-in / sign-out. Mapped by Program.cs into the API group, so
    /// <see cref="Infrastructure.ClientContextFilter"/> runs on them too (it no-ops for the anonymous
    /// login call, and populates the context for logout).</summary>
    public static IEndpointRouteBuilder MapAuthMechanism(this IEndpointRouteBuilder api)
    {
        api.MapPost("/api/auth/login", async (
                [FromBody] LoginRequest req,
                IUserRepository users,
                HttpContext http) =>
            {
                var creds = await users.GetCredentialsAsync(req.Username ?? "");

                // Unknown user. GetCredentialsAsync deliberately does NOT filter on is_active — it
                // PROJECTS it — precisely so this path can tell "no such user" from "deactivated user".
                if (creds is null)
                    return Results.Unauthorized();

                // A soft-deleted user who still has a password must not be able to log in.
                if (!creds.IsActive)
                    return Results.Unauthorized();

                // A NULL password_hash means "has never had a password set" => CANNOT LOG IN. Never treat
                // it as "any password matches": that is an authentication bypass, and it is exactly the
                // state every user is in on a freshly migrated database (v10 added the column empty).
                if (string.IsNullOrEmpty(creds.PasswordHash))
                    return Results.Unauthorized();

                var verify = Hasher.VerifyHashedPassword(HashUser, creds.PasswordHash, req.Password ?? "");
                if (verify == PasswordVerificationResult.Failed)
                    return Results.Unauthorized();

                // ClaimTypes.Name MUST be the USERNAME (the `username` column), not the display name:
                // CurrentUserService's seam is `() => HttpContext.User.Identity.Name` and it feeds that
                // straight into IUserRepository.GetByUsernameAsync. Putting the display name here makes
                // every authenticated request fail to resolve its own user.
                var identity = new ClaimsIdentity(
                    new[]
                    {
                        new Claim(ClaimTypes.Name, req.Username!),
                        new Claim(ClaimTypes.NameIdentifier, creds.Id.ToString()),
                        // The literal "1" — see AdminClaimValue. A bool would render "True" and the
                        // Admin policy would never match.
                        new Claim(AdminClaimType, creds.IsAdmin ? AdminClaimValue : "0"),
                    },
                    CookieAuthenticationDefaults.AuthenticationScheme);

                await http.SignInAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    new ClaimsPrincipal(identity),
                    // IsPersistent is what makes the cookie survive a browser restart. It is worthless
                    // without the Data Protection key ring above surviving a PROCESS restart.
                    new AuthenticationProperties { IsPersistent = true });

                return Results.Ok(new LoginResponse(creds.Id, req.Username!, creds.Name, creds.IsAdmin));
            })
            .AllowAnonymous()   // or the FallbackPolicy locks the user out of the login route itself
            .WithName("Login")
            .WithTags("Auth")
            .Produces<LoginResponse>()
            .Produces(StatusCodes.Status401Unauthorized);

        api.MapPost("/api/auth/logout", async (HttpContext http) =>
            {
                await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return Results.NoContent();
            })
            .WithName("Logout")
            .WithTags("Auth")
            .Produces(StatusCodes.Status204NoContent);

        // GET /api/me — the identity echo of the auth mechanism, and the ONLY endpoint that proves
        // ClientContextFilter actually ran: a non-zero ActiveTeamId here means the per-request team
        // initializer fired, and a non-empty MemberTeamIds means the authorization bound was loaded.
        // It lives with the mechanism, not in Endpoints/AuthEndpoints.cs — Wave 2 must NOT re-map it.
        api.MapGet("/api/me", (IClientContext ctx, ICurrentTeamService currentTeam) =>
                Results.Ok(new MeResponse(
                    ctx.UserId, ctx.UserName, ctx.IsAdmin, ctx.MemberTeamIds, currentTeam.ActiveTeamId)))
            .WithName("Me")
            .WithTags("Auth")
            .Produces<MeResponse>()
            .Produces(StatusCodes.Status401Unauthorized);

        return api;
    }
}
