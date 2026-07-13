using Microsoft.AspNetCore.Mvc;
using TimesheetApp.Api.Auth;
using TimesheetApp.Api.Contracts;
using TimesheetApp.Api.Infrastructure;
using TimesheetApp.Data.Repositories;
using TimesheetApp.Services;

namespace TimesheetApp.Api.Endpoints;

/// <summary>M9 P3c/P3d/P3e. Owns <c>PUT /api/users/{id}/admin</c> · <c>GET|PUT /api/settings/{key}</c> ·
/// <c>POST /api/standup/archive</c> — three administrative surfaces that existed in the domain and had NO
/// web route at all. They belong by area in <c>SettingsEndpoints.cs</c> (which already owns
/// <c>/api/users/*</c>, <c>/api/standup/*</c> and <c>/api/ops/*</c>), and they are here instead only because
/// that file was closed to this task; the split is organisational, not architectural.
///
/// <para><b>What was missing.</b> <c>ISettingsRepository</c> — the DB-backed key/value store — was reachable
/// from no route: SET-02's "not logged for N days" warning window could be neither read nor written from the
/// browser, even though <c>GET /api/reports/missing-logs</c> reads that very key server-side to decide who to
/// flag. DR-09's weekly standup archive had no API surface either, and <c>Program.cs</c> never calls
/// <c>BackfillMissingWeeksAsync</c> (only the WPF <c>App.xaml.cs</c> does), so on a web-only deployment
/// nothing ever wrote it. And there was no way to grant or revoke the admin flag itself.</para>
///
/// <para><b>THERE ARE TWO ADMIN CHECKS IN THIS API AND THEY DISAGREE FOR UP TO THIRTY DAYS.</b></para>
/// <list type="number">
/// <item><c>.RequireAuthorization(AuthSetup.AdminPolicy)</c> gates on the <c>is_admin</c> CLAIM, which is
/// written ONCE, at login, into a 30-day sliding cookie. Demote an admin in the database and the cookie they
/// are already holding still satisfies this policy until it expires.</item>
/// <item><see cref="IClientContext.IsAdmin"/> is read FRESH FROM THE DATABASE on every request by
/// <c>ClientContextFilter</c>. The four <c>/api/ops/*</c> routes check it IN ADDITION to the policy, and so
/// refuse a demoted admin immediately.</item>
/// </list>
///
/// <para><b>The three admin-gated routes here take BOTH, exactly as <c>/api/ops/*</c> does</b>, and for
/// <c>PUT /api/users/{id}/admin</c> that is not defence in depth but a correctness requirement: it is the
/// route that GRANTS the flag, so gating it on the stale claim alone would let a just-demoted admin hand the
/// flag straight back to themselves with the cookie they are still holding, and the demotion would be
/// worthless. <c>AdminEndpointsTests.A_demoted_admin_cannot_re_promote_themselves</c> pins it. Closing the
/// 30-day window generally (re-issuing or revoking the cookie on demotion) is a change to the auth mechanism,
/// is out of scope here, and is recorded as an open risk — the two tests either side of that one pin the
/// window's CURRENT behaviour precisely so that whoever closes it sees them fail.</para>
///
/// <para><b><c>GET /api/settings/{key}</c> is the one route here that is deliberately NOT admin-gated.</b>
/// The Reports screen every user sees renders the "not logged for N days" banner from that window, so an
/// admin gate on the READ would 403 the ordinary user whose screen needs it. The store holds operational
/// scalars, not secrets; the WRITE is gated.</para></summary>
public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder api)
    {
        // ==== The admin flag ================================================================================

        api.MapPut("/api/users/{id:int}/admin", async (
            int id,
            [FromBody] AdminSetIsAdminRequest req,
            IClientContext ctx,
            IUserRepository users) =>
        {
            // THE DB-FRESH CHECK, not merely the policy above it -- see the class doc. Without this line a
            // demoted admin re-promotes themselves with the cookie they already hold.
            if (!ctx.IsAdmin) return Results.StatusCode(StatusCodes.Status403Forbidden);

            // 404 BEFORE the write, and it is not optional. SetIsAdminCheckedAsync THROWS
            // ConcurrencyConflictException with deleted: true for a row that is not there -- which
            // ExceptionMapper turns into a 409 -- so without this pre-read the route would answer "version
            // conflict" for a user that never existed. Backlogs/Tasks shadow the same path the same way.
            if (await users.GetByIdAsync(id) is null) return Results.NotFound();

            // CHECKED, not bump-only: this is a PRIVILEGE change, and two admins racing on one user's row
            // must not silently lose one of the two decisions. A stale version throws and is LET THROUGH to
            // ExceptionMapper -> 409 + ConflictBody.
            var newVersion = await users.SetIsAdminCheckedAsync(id, req.IsAdmin, req.ExpectedVersion);

            // The version the checked call returned is the caller's next expectedVersion -- never re-read it.
            return Results.Ok(new SavedBody(newVersion));
        })
        .RequireAuthorization(AuthSetup.AdminPolicy)
        .WithName("UserSetAdmin")
        .WithTags("Users")
        .Produces<SavedBody>()
        .Produces(StatusCodes.Status404NotFound)
        .Produces<ConflictBody>(StatusCodes.Status409Conflict);

        // ==== The key/value settings store ==================================================================

        // OPEN to any authenticated caller (no .RequireAuthorization) -- see the class doc. The `api`
        // MapGroup carries only ClientContextFilter, and AuthSetup's FallbackPolicy already requires an
        // authenticated cookie on every route that declares no authorization metadata of its own, so this is
        // "any logged-in user", not "anonymous".
        api.MapGet("/api/settings/{key}", async (string key, ISettingsRepository settings) =>
                Results.Ok(new SettingDto(key, await settings.GetAsync(key))))
            .WithName("SettingGet")
            .WithTags("Settings")
            // An UNSET key is 200 with a null value, NOT a 404: every key is unset on a fresh database, and
            // the caller's correct response is to fall back to the documented default (3, for the N-days
            // window). A 404 would force every settings form to treat "never written" as a failure.
            .Produces<SettingDto>();

        api.MapPut("/api/settings/{key}", async (
            string key,
            [FromBody] AdminSettingRequest req,
            IClientContext ctx,
            ISettingsRepository settings) =>
        {
            if (!ctx.IsAdmin) return Results.StatusCode(StatusCodes.Status403Forbidden);

            // Settings.value is TEXT NOT NULL and ISettingsRepository.SetAsync takes a non-nullable string,
            // so a null from the wire is a caller error (a 400), not a SQL exception (a 500).
            if (req.Value is null)
                return Results.BadRequest(new ValidationBody("A setting value is required."));

            await settings.SetAsync(key, req.Value);
            return Results.NoContent();
        })
        .RequireAuthorization(AuthSetup.AdminPolicy)
        .WithName("SettingSet")
        .WithTags("Settings")
        // 204, and NO expectedVersion anywhere in this route: Settings is DELIBERATELY UNVERSIONED
        // (DatabaseInitializer: key-addressed, "last-write-wins IS the correct semantics there"). There is no
        // version to check, none to hand back, and this route cannot 409.
        .Produces(StatusCodes.Status204NoContent)
        .Produces<ValidationBody>(StatusCodes.Status400BadRequest);

        // ==== The standup weekly archive (DR-09) ============================================================

        // ExportWeekAsync WRITES A FILE on the server and returns its path, and that is the point of this
        // route rather than a defect: DR-09's archive exists to accumulate markdown next to the database,
        // where the backup job picks it up. It is an ADMIN ARCHIVE ACTION, not a download -- the route that
        // hands markdown CONTENT to a browser is GET /api/tasklist/export, which is a different thing.
        //
        // Note that ExportWeekAsync is whole-organisation by construction (it builds with teamIds: null) and
        // is not team-scoped like the read routes. That is deliberate for an archive of the record, and it is
        // precisely why this route is admin-gated twice over.
        api.MapPost("/api/standup/archive", async (
            [FromQuery] DateOnly date,
            IClientContext ctx,
            IStandupArchiveService archive) =>
        {
            if (!ctx.IsAdmin) return Results.StatusCode(StatusCodes.Status403Forbidden);

            var path = await archive.ExportWeekAsync(date);

            // "No data => no file" is the service's own rule -- it returns null rather than writing an empty
            // archive. Answering 200 with a null path would give the client a success toast pointing at
            // nothing.
            if (path is null)
                return Results.BadRequest(new ValidationBody("No standup data this week to archive."));

            return Results.Ok(new ArchivedFileDto(path));
        })
        .RequireAuthorization(AuthSetup.AdminPolicy)
        .WithName("StandupArchiveWeek")
        .WithTags("Standup")
        .Produces<ArchivedFileDto>()
        .Produces<ValidationBody>(StatusCodes.Status400BadRequest);

        return api;
    }
}

// ==== Request DTOs ======================================================================================
// Prefixed with the owning area (Admin...) -- every endpoint file shares the TimesheetApp.Api.Endpoints
// namespace, so an unprefixed `SettingRequest` in two of them is a duplicate-type compile error found only
// at the merge. Internal, matching SettingsEndpoints' request records.

/// <summary>The target user id is a ROUTE parameter, never a body field — it is attacker-controlled either
/// way, and the route makes that explicit (the same reasoning as
/// <c>POST /api/auth/users/{id}/set-password</c>).</summary>
internal sealed record AdminSetIsAdminRequest(bool IsAdmin, long ExpectedVersion);

/// <summary>No <c>ExpectedVersion</c>: the Settings table is deliberately unversioned (last-write-wins is the
/// correct semantics for a key-addressed store), so there is no token to check. <c>Value</c> is nullable ON
/// THE WIRE so that a null can be REJECTED with a 400 rather than reaching a NOT NULL column as a 500.</summary>
internal sealed record AdminSettingRequest(string? Value);
