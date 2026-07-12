using Microsoft.AspNetCore.Mvc;
using TimesheetApp.Api.Auth;
using TimesheetApp.Api.Contracts;
using TimesheetApp.Api.Infrastructure;
using TimesheetApp.Data.Repositories;

namespace TimesheetApp.Api.Endpoints;

/// <summary>W2-A. Owns — and owns ONLY — the password-management routes on <c>/api/auth/*</c>:
/// <list type="bullet">
/// <item><c>POST /api/auth/set-password</c> — the caller changes their OWN password. The actor is ALWAYS
/// <see cref="IClientContext.UserId"/>; there is deliberately no id of any kind on the request DTO (rule 8:
/// never bind an actor id from a request body — a <c>userId</c> field here would let any authenticated user
/// change any other user's password).</item>
/// <item><c>POST /api/auth/users/{id}/set-password</c> — an admin resets someone else's.
/// <c>.RequireAuthorization(AuthSetup.AdminPolicy)</c>; the target id is a ROUTE parameter, never a body
/// field, because it is attacker-controlled either way and the route makes that explicit.</item>
/// </list>
///
/// <para><b>DO NOT map <c>/api/auth/login</c>, <c>/api/auth/logout</c> or <c>/api/me</c> here.</b> They are
/// already mapped by <c>Auth/AuthSetup.MapAuthMechanism()</c> — they are the auth MECHANISM (they mint and
/// clear the very cookie this API is secured with) and the whole test suite depends on them. Two handlers
/// on one route is an <c>AmbiguousMatchException</c>: HTTP 500 on every request to it.</para>
///
/// <para>Hash with <c>AuthSetup.HashPassword(password)</c> — do not construct a <c>PasswordHasher</c>.
/// <c>IUserRepository.SetPasswordHashAsync</c> is BUMP-ONLY (see the interface doc): no checked sibling, no
/// client-held <c>rowVersion</c> to accept or return, so neither request DTO below carries one.</para>
///
/// <para><b>KNOWN GAP, reported rather than closed privately: self-service does NOT verify the caller's
/// current password.</b> The original stub for this file called for verifying it first. Verifying a hash
/// needs a VERIFIER, not a hasher — and the only exposed primitive on the frozen surface is
/// <c>AuthSetup.HashPassword</c>, which is one-way (it salts randomly per call, so re-hashing the candidate
/// and comparing strings can never match even for the right password). The actual verifier
/// (<c>PasswordHasher&lt;User&gt;.VerifyHashedPassword</c>) is reachable only through a private field inside
/// <c>Auth/AuthSetup.cs</c>, which this wave may not edit, and the brief separately forbids constructing a
/// second <c>PasswordHasher&lt;&gt;</c> here. Closing this by adding a public verify method to
/// <c>AuthSetup</c> is the controller's call, not this file's — so today, "self" set-password relies solely
/// on the auth cookie already proving identity: anyone holding a live session can set a new password without
/// re-entering the old one. Flagged in the wave report.</para>
///
/// <para><b>Second flagged gap: no <see cref="IChangeNotifier"/> call after either write.</b>
/// <c>DataChangedAsync(DataKind, int teamId, …)</c> requires a real team id, but <c>Users</c> is a GLOBAL
/// entity (no team column — see the <c>SettingsEndpoints</c> header) and <c>password_hash</c> specifically is
/// never serialized onto any DTO, so there is no client-visible state for a notification to refresh and no
/// non-guessed <c>teamId</c> to hand the frozen signature. Rather than invent a convention for global-entity
/// notifications unilaterally (the exact way M8.2 ended up with three incompatible concurrency APIs), the
/// call is omitted here and the question — what should global entities pass as <c>teamId</c> — is raised in
/// the wave report for the controller to answer once, centrally, since it equally affects
/// <c>SettingsEndpoints</c> (Users, Teams, Tags, PcaContacts).</para></summary>
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

                await users.SetPasswordHashAsync(ctx.UserId, AuthSetup.HashPassword(req.NewPassword));
                return Results.NoContent();
            })
            .WithName("AuthSetPassword")
            .WithTags("Auth")
            .Produces(StatusCodes.Status204NoContent)
            .Produces<ValidationBody>(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized);

        // POST /api/auth/users/{id}/set-password — admin resets ANOTHER user's password. The target id is
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
/// other user's password by supplying their id.</summary>
public sealed record AuthSetPasswordRequest(string NewPassword);

/// <summary>Admin set-password-for-user. The target id is a ROUTE parameter (see
/// <c>/api/auth/users/{id}/set-password</c>), never a body field.</summary>
public sealed record AuthAdminSetPasswordRequest(string NewPassword);
