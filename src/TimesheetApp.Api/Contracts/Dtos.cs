namespace TimesheetApp.Api.Contracts;

// ---------------------------------------------------------------------------------------------------
// Wire contracts. Every DTO here is what the Angular client (M8.4) generates its TypeScript from, via
// the OpenAPI document — so a field that is missing here is a field the client cannot send back.
// ---------------------------------------------------------------------------------------------------

/// <summary><c>Username</c> is the <c>Users.username</c> column (schema v10 renamed it from
/// windows_username), NOT the display name.</summary>
public sealed record LoginRequest(string? Username, string? Password);

/// <summary>Returned on a successful login so the SPA does not need a second round-trip for identity.
/// <c>Name</c> is the display name; <c>Username</c> is the login handle.</summary>
public sealed record LoginResponse(int Id, string Username, string Name, bool IsAdmin);

/// <summary>The authenticated caller's own context — the wire projection of <c>IClientContext</c>.
///
/// <para><c>MemberTeamIds</c> is the user's AUTHORIZATION BOUND; <c>ActiveTeamId</c> is their current
/// WORKING SCOPE (a single team). They are different things and the client needs both: the first drives
/// which teams a filter may offer, the second is the team a new timesheet or standup row lands in.</para></summary>
public sealed record MeResponse(
    int Id,
    string Name,
    bool IsAdmin,
    IReadOnlyList<int> MemberTeamIds,
    int ActiveTeamId);
