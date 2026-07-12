using Microsoft.AspNetCore.Mvc;
using TimesheetApp.Api.Auth;
using TimesheetApp.Api.Contracts;
using TimesheetApp.Api.Infrastructure;
using TimesheetApp.Data.Repositories;

namespace TimesheetApp.Api.Endpoints;

/// <summary>W2-A. Owns — and owns ONLY — the password-management routes on <c>/api/auth/*</c>:
/// <list type="bullet">
/// <item><c>POST /api/auth/set-password</c> — the caller changes their OWN password, and must prove the
/// CURRENT one to do it. The actor is ALWAYS <see cref="IClientContext.UserId"/>; there is deliberately no
/// id of any kind on the request DTO (rule 8: never bind an actor id from a request body — a <c>userId</c>
/// field here would let any authenticated user change any other user's password).</item>
/// <item><c>POST /api/auth/users/{id}/set-password</c> — an admin resets someone else's.
/// <c>.RequireAuthorization(AuthSetup.AdminPolicy)</c>; the target id is a ROUTE parameter, never a body
/// field, because it is attacker-controlled either way and the route makes that explicit.</item>
/// </list>
///
/// <para><b>Only the SELF route checks the current password, and that asymmetry is deliberate.</b> Requiring
/// re-proof on self-service is what stops a borrowed session — an unattended laptop, a stolen cookie — from
/// being converted into a permanent account takeover: without it, whoever holds a live session can silently
/// set a new password and own the account for good. An ADMIN resetting someone else's password by definition
/// does not know it, so demanding it there would make the reset impossible.</para>
///
/// <para><b>DO NOT map <c>/api/auth/login</c>, <c>/api/auth/logout</c> or <c>/api/me</c> here.</b> They are
/// already mapped by <c>Auth/AuthSetup.MapAuthMechanism()</c> — they are the auth MECHANISM (they mint and
/// clear the very cookie this API is secured with) and the whole test suite depends on them. Two handlers
/// on one route is an <c>AmbiguousMatchException</c>: HTTP 500 on every request to it.</para>
///
/// <para>Hash with <c>AuthSetup.HashPassword</c> and verify with <c>AuthSetup.VerifyPassword</c> — both go
/// through the ONE <c>PasswordHasher</c> instance the login path uses. Never construct a second one: a
/// hasher with different options produces hashes login cannot verify, and the breakage appears only for
/// users who actually changed their password. <c>IUserRepository.SetPasswordHashAsync</c> is BUMP-ONLY (see
/// the interface doc): no checked sibling, no client-held <c>rowVersion</c> to accept or return, so neither
/// request DTO below carries one.</para>
///
/// <para><b>KNOWN GAP, reported rather than closed privately: no <see cref="IChangeNotifier"/> call after
/// either write.</b> <c>DataChangedAsync(DataKind, int teamId, …)</c> requires a real team id, but
/// <c>Users</c> is a GLOBAL entity (no team column — see the <c>SettingsEndpoints</c> header) and
/// <c>password_hash</c> specifically is never serialized onto any DTO, so there is no client-visible state
/// for a notification to refresh and no non-guessed <c>teamId</c> to hand the frozen signature. Rather than
/// invent a convention for global-entity notifications unilaterally (the exact way M8.2 ended up with three
/// incompatible concurrency APIs), the call is omitted here and the question — what should global entities
/// pass as <c>teamId</c> — is raised in the wave report for the controller to answer once, centrally, since
/// it equally affects <c>SettingsEndpoints</c> (Users, Teams, Tags, PcaContacts).</para></summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder api)
    {
        // POST /api/auth/set-password — self service. The actor is ALWAYS ctx.UserId (rule 8); the request
        // carries no id of any kind, so there is nothing in the body that could redirect the write to
        // another user's row.
        api.MapPost("/api/auth/set-password", async (
                [FromBody] AuthSetPasswordRequest req,
                IClientContext ctx,
                IUserRepository users) =>
            {
                if (string.IsNullOrWhiteSpace(req.NewPassword))
                    return Results.BadRequest(new ValidationBody("New password is required."));

                // ---------------------------------------------------------------------------------------
                // Reading the caller's stored hash needs a USERNAME: GetCredentialsAsync is keyed by the
                // `username` column, and IUserRepository has no by-id credentials lookup (Core is frozen,
                // so one cannot be added here).
                //
                // ctx.UserName IS THE DISPLAY NAME, NOT THE USERNAME — ClientContextFilter populates it
                // from User.Name (see IClientContext: "the changedByName on every audited write"). Passing
                // it to GetCredentialsAsync would be a silent bug of the worst kind: ApiFactory.SeedUserAsync
                // writes `name` and `username` as the SAME string, so a display-name lookup PASSES EVERY
                // TEST IN THIS SUITE and fails only in production, where people are called "Alice Nguyen"
                // and log in as "alice". So resolve the username from the actor id instead.
                // ---------------------------------------------------------------------------------------
                var actor = await users.GetByIdAsync(ctx.UserId);
                var username = actor?.WindowsUsername;   // the `username` column; the property kept its old name
                if (string.IsNullOrEmpty(username))
                    return Results.Unauthorized();

                var creds = await users.GetCredentialsAsync(username);
                if (creds is null)
                    return Results.Unauthorized();

                // A NULL hash means "has never had a password set": the caller has no current password to
                // prove, so they cannot self-serve and need an admin reset. It must NEVER mean "any current
                // password matches" — that is the authentication-bypass shape, and it is the state every
                // user is in on a freshly migrated v10 database.
                if (string.IsNullOrEmpty(creds.PasswordHash))
                    return Results.BadRequest(new ValidationBody(
                        "No password is set for this account. Ask an administrator to set one."));

                // 400, NOT 401. The CALLER is authenticated — the cookie is valid and the session is live;
                // it is the PASSWORD that is wrong. A 401 here would tell the SPA "your session died" and
                // bounce the user out to the login screen mid-form over a typo.
                if (!AuthSetup.VerifyPassword(creds.PasswordHash, req.CurrentPassword))
                    return Results.BadRequest(new ValidationBody("Current password is incorrect."));

                await users.SetPasswordHashAsync(ctx.UserId, AuthSetup.HashPassword(req.NewPassword));
                return Results.NoContent();
            })
            .WithName("AuthSetPassword")
            .WithTags("Auth")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ValidationBody>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized);

        // POST /api/auth/users/{id}/set-password — admin resets ANOTHER user's password. NO current-password
        // check: an admin performing a reset does not know it (see the class header). The target id is
        // attacker-controlled (rule 8) and comes ONLY from the path. A 404 pre-check on GetByIdAsync keeps
        // an unknown id from silently no-op-ing: SetPasswordHashAsync's UPDATE simply touches zero rows for
        // an id that does not exist -- it never throws, so without this check the caller could not tell "it
        // worked" from "there was nobody to change".
        api.MapPost("/api/auth/users/{id:int}/set-password", async (
                int id,
                [FromBody] AuthAdminSetPasswordRequest req,
                IUserRepository users) =>
            {
                if (string.IsNullOrWhiteSpace(req.NewPassword))
                    return Results.BadRequest(new ValidationBody("New password is required."));

                var target = await users.GetByIdAsync(id);
                if (target is null)
                    return Results.NotFound();

                await users.SetPasswordHashAsync(id, AuthSetup.HashPassword(req.NewPassword));
                return Results.NoContent();
            })
            .RequireAuthorization(AuthSetup.AdminPolicy)
            .WithName("AuthAdminSetPassword")
            .WithTags("Auth")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ValidationBody>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        return api;
    }
}

/// <summary>Self set-password. No <c>userId</c> field on purpose: the actor is always
/// <see cref="IClientContext.UserId"/> (rule 8) — adding one would let any authenticated user change any
/// other user's password by supplying their id.
///
/// <para>Both fields are nullable, matching <c>LoginRequest</c>: the C# type may say non-null, but JSON can
/// simply omit a property, so the endpoint treats a missing <c>currentPassword</c> as a failed proof rather
/// than trusting the compiler about what arrived on the wire.</para></summary>
public sealed record AuthSetPasswordRequest(string? CurrentPassword, string? NewPassword);

/// <summary>Admin set-password-for-user. The target id is a ROUTE parameter (see
/// <c>/api/auth/users/{id}/set-password</c>), never a body field. No <c>CurrentPassword</c>: an admin
/// resetting someone else's password does not know it.</summary>
public sealed record AuthAdminSetPasswordRequest(string NewPassword);
