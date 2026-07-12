namespace TimesheetApp.Api.Endpoints;

/// <summary>W2-A. Owns — and owns ONLY — the password-management routes on <c>/api/auth/*</c>:
/// <list type="bullet">
/// <item><c>POST /api/auth/set-password</c> — the caller changes their OWN password (verify the current one
/// first; <c>IUserRepository.SetPasswordHashAsync</c> is bump-only).</item>
/// <item><c>POST /api/auth/users/{id}/set-password</c> — an admin resets someone else's.
/// <c>.RequireAuthorization(AuthSetup.AdminPolicy)</c>.</item>
/// </list>
///
/// <para><b>DO NOT map <c>/api/auth/login</c>, <c>/api/auth/logout</c> or <c>/api/me</c> here.</b> They are
/// already mapped by <c>Auth/AuthSetup.MapAuthMechanism()</c> — they are the auth MECHANISM (they mint and
/// clear the very cookie this API is secured with) and the whole test suite depends on them. Two handlers
/// on one route is an <c>AmbiguousMatchException</c>: HTTP 500 on every request to it.</para>
///
/// <para>Hash with <c>AuthSetup.HashPassword(password)</c> — do not construct a <c>PasswordHasher</c>.</para></summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder api)
    {
        // Wave 2 (W2-A).
        return api;
    }
}
