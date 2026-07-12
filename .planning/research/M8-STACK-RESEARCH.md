# M8 STACK RESEARCH — ASP.NET Core 8 (Backend Foundation)

**Agent:** Stack Research (STEP 4, Mode B)
**Date:** 2026-07-12
**Spec:** `docs/superpowers/specs/2026-07-12-m8-backend-foundation-design.md` (M8.1 + M8.2 + M8.3)
**Inputs:** `.planning/M8-FEATURE-INVENTORY.md`, `CLAUDE.md`, `.planning/config.json`
**Scope:** exactly the 7 areas the spec depends on. Nothing beyond it.

> **File-name note:** the plugin's default path is `.planning/research/STACK-RESEARCH.md`, but that file already exists and holds the **M1 WPF** stack research (2026-06-21). This repo's actual convention is `{PHASE}-STACK-RESEARCH.md` (cf. `P8-STACK-RESEARCH.md`). Using `M8-` prefix to avoid destroying the M1 artifact.

**Target:** .NET 8 (`net8.0`). All source claims verified against the **`release/8.0`** branch of `dotnet/aspnetcore` — *not* `main`, because several of the defaults below changed in 9/10 and reading `main` would give the wrong answer.

**Claim tags:** `[VERIFIED]` = I read the actual .NET 8 source · `[CITED]` = documented, URL given · `[ASSUMED]` = inference, NOT checked.

---

## 0. TL;DR — the five things that will actually bite

| # | Trap | Why it is invisible | § |
|---|---|---|---|
| **T1** | On **.NET 8**, an unauthenticated API call gets **`302 → /Account/Login`**, not `401`. The 401-for-API behaviour only became the default in **.NET 10**. | Angular's `HttpClient` follows the redirect, gets a 404, and the error interceptor reports "404" — never "401". Reads like a routing bug. | §1.3 |
| **T2** | `Cookie.SecurePolicy = Always` (spec §8.1) + `WebApplicationFactory`'s default `BaseAddress = http://localhost` ⇒ **`CookieContainer` silently drops the auth cookie**. | *Every* authenticated integration test 401s — while the login test itself passes. Points at the wrong file. | §5.2 |
| **T3** | On IIS, if Data Protection isn't configured the key ring lives **in memory**. App-pool recycle (default: ~daily + after 20 min idle + every deploy) ⇒ **every user is logged out**, 30-day `IsPersistent` cookie notwithstanding. | Silently defeats the spec's own goal ("stay logged in across browser restarts", §2). Invisible in dev. | §6.4 |
| **T4** | `RequireClaim("is_admin", "1")` compares the claim **value with `StringComparer.Ordinal`**. `new Claim("is_admin", user.IsAdmin.ToString())` yields `"True"` ⇒ **the policy always fails**; the admin gets 403 on their own endpoints. | No error, no log. Just a 403. | §3.3 |
| **T5** | `MapFallbackToFile("index.html")` is an **endpoint** ⇒ the `FallbackPolicy` applies to it ⇒ **the Angular shell itself 401s when logged out** ⇒ the user can never reach the login form. | Only appears when Angular is served same-origin from the API — which *is* the production plan. | §3.4 |

Two things the spec was right to worry about turn out to be **non-issues**:

- **`PasswordHasher<T>` standalone drags in nothing.** No EF Core, no Identity schema, no DI stack, nothing auto-registered. Two small crypto assemblies. **Decision D5 is fully vindicated.** (§2)
- **Cookies + SignalR need zero extra configuration.** The browser attaches the cookie to the negotiate *and* the WebSocket handshake automatically. No `accessTokenFactory`, no `?access_token=` query string, no `OnMessageReceived` hook. **This is the concrete payoff of D6 (cookie over JWT) that the spec undersells.** (§4.2)

---

## 1. Cookie auth WITHOUT ASP.NET Core Identity

### 1.1 It is a first-class, documented scenario

Microsoft ships a doc for exactly this: *"Use cookie authentication without ASP.NET Core Identity"*. `AddCookie()` lives in `Microsoft.AspNetCore.Authentication.Cookies`, which is **part of the `Microsoft.AspNetCore.App` shared framework** — no `PackageReference` required. `[CITED]`

Defaults, read from `CookieAuthenticationDefaults.cs` (`release/8.0`) `[VERIFIED]`:

| Constant | Value |
|---|---|
| `AuthenticationScheme` | `"Cookies"` |
| `CookiePrefix` | `".AspNetCore."` |
| `LoginPath` | **`"/Account/Login"`** ← the redirect target in T1 |
| `LogoutPath` | `"/Account/Logout"` |
| `AccessDeniedPath` | `"/Account/AccessDenied"` |
| `ReturnUrlParameter` | `"ReturnUrl"` |

### 1.2 `SignInAsync` — `IsPersistent` and `ExpiresUtc`

`[CITED]` — MS Learn, *Persistent cookies* + *Absolute cookie expiration*:

> `IsPersistent = true`: the cookie *"survives through browser closures. **Any sliding expiration settings previously configured are honored.**"*
>
> `ExpiresUtc`: *"To create a persistent cookie, `IsPersistent` must **also** be set … **When `ExpiresUtc` is set, it overrides the value of the `ExpireTimeSpan` option**"* — and the doc's own sample comment adds that this *"ignores any sliding expiration settings previously configured."*

| | `IsPersistent = false` (default) | `IsPersistent = true` |
|---|---|---|
| Cookie lifetime | **Session** — deleted on browser close | **Absolute** — survives restart |
| Ticket lifetime | `ExpireTimeSpan` | `ExpireTimeSpan`, or `ExpiresUtc` if set |
| Sliding expiration | applies | applies |

⇒ The spec's design is exactly right: `ExpireTimeSpan = 30d` + `SlidingExpiration = true` + `IsPersistent = rememberMe`.

⚠️ **Do NOT also set `ExpiresUtc`.** It *overrides* `ExpireTimeSpan` **and disables sliding expiration**, silently converting "30 days of inactivity" into "30 days from login, hard stop". A user who logs in daily would be kicked out on day 31 for no visible reason. `[CITED]`

Sliding mechanics `[CITED]`: the handler re-issues the cookie once **more than half** of `ExpireTimeSpan` has elapsed. At 30 days that is a re-issue at most every ~15 days. Cheap.

### 1.3 🔴 T1 — on .NET 8 the API **redirects** instead of returning 401

The single most important finding. Confirmed at source level, not inferred.

`CookieAuthenticationEvents.cs` (`release/8.0`) — the **default** `OnRedirectToLogin` `[VERIFIED]`:

```csharp
public Func<RedirectContext<CookieAuthenticationOptions>, Task> OnRedirectToLogin { get; set; } = context =>
{
    if (IsAjaxRequest(context.Request))
    {
        context.Response.Headers.Location = context.RedirectUri;
        context.Response.StatusCode = 401;
    }
    else
    {
        context.Response.Redirect(context.RedirectUri);   // ← 302 → /Account/Login?ReturnUrl=...
    }
    return Task.CompletedTask;
};

private static bool IsAjaxRequest(HttpRequest request)
{
    return string.Equals(request.Query[HeaderNames.XRequestedWith], "XMLHttpRequest", StringComparison.Ordinal) ||
        string.Equals(request.Headers.XRequestedWith,               "XMLHttpRequest", StringComparison.Ordinal);
}
```

The **only** thing that yields a 401 on .NET 8 is the literal header `X-Requested-With: XMLHttpRequest`.

- **Angular `HttpClient` does NOT send it.** `[CITED]` — AngularJS deliberately removed it from the default `$http` config (angular/angular.js#1004, #11008: it forces a CORS preflight for no benefit) and Angular 2+ `HttpClient` never had it. ⇒ **every unauthenticated Angular API call gets a 302.** The browser's fetch/XHR layer *follows* it, `/Account/Login` doesn't exist, so Angular receives **`404`** — not 401. An interceptor written as `if (err.status === 401) → router.navigate(['/login'])` **will never fire.**
- **The SignalR JS client DOES send it.** `XhrHttpClient.ts` (`release/8.0`) `[VERIFIED]`: `xhr.setRequestHeader("X-Requested-With", "XMLHttpRequest");` ⇒ the hub's `negotiate` correctly 401s **even without the fix**.

⚠️ **That asymmetry is itself a debugging trap**: the hub "works", the API doesn't, and the two are configured by the same `AddCookie` call — which sends you looking in entirely the wrong place.

Microsoft has since agreed this is wrong for APIs and **changed the default in .NET 10** (new `IApiEndpointMetadata`; `[ApiController]` endpoints and SignalR endpoints now 401/403 by default). `[CITED]` — <https://learn.microsoft.com/en-us/dotnet/core/compatibility/aspnet-core/10/cookie-authentication-api-endpoints>. **We are on 8. We must do it by hand.**

### 1.4 ✅ The correct .NET 8 wiring

```csharp
using Microsoft.AspNetCore.Authentication.Cookies;

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.ExpireTimeSpan      = TimeSpan.FromDays(30);
        o.SlidingExpiration   = true;

        o.Cookie.Name         = "__Host-timesheet.auth";  // see note below
        o.Cookie.HttpOnly     = true;                     // immune to XSS token theft (spec D6)
        o.Cookie.SameSite     = SameSiteMode.Lax;         // CSRF baseline — see §1.5
        o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        o.Cookie.Path         = "/";

        // ── The whole point: this is an API. It must never redirect. ────────
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
```

Notes:

- **Do not set `Response.Headers.Location`** in the overrides (the framework's XHR branch does, for legacy JS). An Angular app has no use for it, and a `Location` header on a 401 confuses proxies, dev tools and future readers.
- Once both events are overridden, `LoginPath` / `AccessDeniedPath` become **dead configuration** — the handler never reaches them. Leaving them at their defaults is harmless; *setting* them is actively misleading (it implies pages exist). **Don't set them.**
- **`__Host-` prefix** `[CITED]` (RFC 6265bis): a browser will only accept a `__Host-`-prefixed cookie if it is `Secure`, has `Path=/`, and has **no `Domain`**. It turns `SecurePolicy = Always` from *configured* into *browser-enforced*, and blocks cookie-fixation from a sibling subdomain. It fails **loudly** (cookie simply not stored) rather than silently. Free. It does hard-require HTTPS in dev — which the spec (§10) has already committed to.

### 1.5 CSRF: `SameSite=Lax` is load-bearing — write the invariant down

Choosing cookies over JWT buys XSS immunity (`HttpOnly`). The flip side is that **cookies are auto-attached by the browser**, which is precisely the CSRF attack shape. `SameSite=Lax` is what closes it: the browser will not attach the cookie to a **cross-site POST/PUT/PATCH/DELETE** — only to top-level GET navigations. `[CITED]` — MDN / RFC 6265bis.

⇒ With `Lax` + an API whose every mutation is non-GET, **antiforgery tokens are not required.** `[ASSUMED]` — this is the standard argument and I believe it holds here, but it is a **security** claim resting on an invariant ("no state-changing GET endpoint exists") that **nothing in the codebase enforces**.

**Recommendation:** state it in the spec as an explicit design invariant, so that "add a state-changing GET" is understood to reopen the CSRF question. Cheap to write now; expensive to rediscover from an incident.

---

## 2. `PasswordHasher<T>` standalone — the dependency question, answered

**The package is `Microsoft.Extensions.Identity.Core`.** (The *namespace* is `Microsoft.AspNetCore.Identity`, which is why people reach for the wrong package.) `[VERIFIED]`

### 2.1 Full transitive closure — no EF Core, no Identity stack

`[VERIFIED]` — nuget.org, `Microsoft.Extensions.Identity.Core` **8.0.11**, `net8.0` target:

```
Microsoft.Extensions.Identity.Core  8.0.11
├── Microsoft.AspNetCore.Cryptography.KeyDerivation  (>= 8.0.11)
│   └── Microsoft.AspNetCore.Cryptography.Internal   (>= 8.0.11)    ← and nothing else
├── Microsoft.Extensions.Logging                     (>= 8.0.1)
└── Microsoft.Extensions.Options                     (>= 8.0.2)
```

- ❌ **No `Microsoft.EntityFrameworkCore`.** EF only enters via the *separate* `Microsoft.AspNetCore.Identity.EntityFrameworkCore` package, which we do not reference.
- ❌ **No `AspNetUsers` schema, no migrations, no `IdentityDbContext`** — those live in the EF package.
- ❌ **Nothing is auto-registered.** The Identity machinery activates only when you call `AddIdentity()` / `AddIdentityCore()`. **We call neither.** Referencing the package registers exactly zero services.
- ⚠️ The assembly *does* contain `UserManager<T>` etc. They are inert types on disk. Cost: a few hundred KB.

`Microsoft.Extensions.Logging` and `.Options` are already transitively present via `Microsoft.Extensions.DependencyInjection` (already referenced by `TimesheetApp.csproj`). ⇒ **The net-new footprint is 2 small crypto assemblies.** Decision D5 in the spec is fully vindicated.

### 2.2 The algorithm — .NET 8 specifically

`PasswordHasherOptions.cs` (`release/8.0`) `[VERIFIED]`:

```csharp
public PasswordHasherCompatibilityMode CompatibilityMode { get; set; } = PasswordHasherCompatibilityMode.IdentityV3;
public int IterationCount { get; set; } = 100_000;   // "Default is 100,000." Only used when mode is V3.
```

`PasswordHasher.cs` (`release/8.0`) `[VERIFIED]` — the V3 format:

| Property | .NET 8 value |
|---|---|
| KDF | **PBKDF2** |
| PRF | **HMAC-SHA512** (note: **512**, not the 256 many people assume) |
| Iterations | **100,000** (raised from 10,000; the change shipped in the .NET 8 wave) |
| Salt | 128-bit, from `RandomNumberGenerator` |
| Subkey | 256-bit |
| Encoding | Base64 of `[0x01][prf:4][iter:4][saltlen:4][salt:16][subkey:32]` = 61 bytes → **84 chars** |

⇒ `Users.password_hash TEXT` (spec §6.2) is correctly typed and needs no length constraint. `[VERIFIED]`

**Two source facts that make standalone use trivial** `[VERIFIED]`:

```csharp
public class PasswordHasher<TUser> : IPasswordHasher<TUser> where TUser : class
{
    public PasswordHasher(IOptions<PasswordHasherOptions>? optionsAccessor = null) { … }
    //                    ^^^^^^^^^ optional AND nullable ⇒ `new PasswordHasher<User>()` just works, no DI
}
```

- The generic constraint is **only** `class`. ⇒ **Use the app's own `Models.User`.** No Identity user type anywhere.
- **The `user` parameter is ignored** — neither `HashPassword` nor `VerifyHashedPassword` references it in the method body. It exists solely to satisfy the interface. (Passing `null!` would even work; passing the real user is just tidier.)

`PasswordVerificationResult` has exactly three members: `Failed`, `Success`, `SuccessRehashNeeded`. `[VERIFIED]`

### 2.3 Usable code

```csharp
// TimesheetApp.Api — the entire setup.
builder.Services.AddSingleton<IPasswordHasher<User>, PasswordHasher<User>>();
```

`PasswordHasher<T>` is stateless and `RandomNumberGenerator` is thread-safe ⇒ **singleton is correct**. `[ASSUMED]` — `AddIdentityCore` registers it `TryAddScoped`, but that is for uniformity with the rest of Identity, not a thread-safety requirement. Scoped also works if you'd rather not reason about it.

```csharp
public sealed class AuthService(IUserRepository users, IPasswordHasher<User> hasher) : IAuthService
{
    public async Task<User?> ValidateCredentialsAsync(string username, string password)
    {
        var user = await users.GetByUsernameAsync(username);

        // ── Timing-attack guard ──────────────────────────────────────────────
        // If the user doesn't exist we must STILL burn ~100k PBKDF2 iterations.
        // Otherwise "unknown user" returns in ~0ms and "wrong password" in ~50ms,
        // which is a free username-enumeration oracle — on an intranet where the
        // usernames ARE the staff list.
        if (user is null || user.PasswordHash is null || !user.IsActive)
        {
            hasher.VerifyHashedPassword(DummyUser, DummyHash, password);
            return null;
        }

        return hasher.VerifyHashedPassword(user, user.PasswordHash, password) switch
        {
            PasswordVerificationResult.Success             => user,
            PasswordVerificationResult.SuccessRehashNeeded => await RehashAsync(user, password),
            _                                              => null,   // Failed
        };
    }

    // Fires when the stored hash used an older format or a lower iteration count.
    // Free forward-compat: raise IterationCount later and passwords upgrade on next login.
    private async Task<User> RehashAsync(User user, string password)
    {
        user.PasswordHash = hasher.HashPassword(user, password);
        await users.SetPasswordHashAsync(user.Id, user.PasswordHash);
        return user;
    }

    private static readonly User   DummyUser = new() { Id = 0, Name = "-" };
    private static readonly string DummyHash = new PasswordHasher<User>().HashPassword(DummyUser, "•");
}
```

⚠️ **`is_active` must be checked at login** (see above). The spec's Users screen soft-deletes (`is_active = 0`, USR-03) but says **nothing about login**. A deactivated user who already has a `password_hash` would otherwise keep full access. **This is a gap in the spec — see §10.1.**

---

## 3. Authorization: the `is_admin` claim, `FallbackPolicy`, `AllowAnonymous`

### 3.1 Wiring

```csharp
builder.Services.AddAuthorization(o =>
{
    o.FallbackPolicy = o.DefaultPolicy;                   // DefaultPolicy == RequireAuthenticatedUser()
    o.AddPolicy("Admin", p => p.RequireClaim("is_admin")); // presence, not value — see §3.3
});
```

`FallbackPolicy` semantics `[CITED]` — MS Learn:

> *"The fallback authorization policy requires **all** users to be authenticated, **except** for Razor Pages, controllers, or action methods with an authorization attribute. For example, … with `[AllowAnonymous]` or `[Authorize(PolicyName="MyPolicy")]` use the applied authorization attribute rather than the fallback authorization policy."*

Precisely, from `AuthorizationMiddleware.cs` (`release/8.0`) `[VERIFIED]`: if the endpoint carries **`IAllowAnonymous`** metadata, the middleware calls `await _next(context)` and **returns immediately** — bypassing challenge/forbid entirely. If the endpoint carries any `IAuthorizeData`, that is combined and wins. Only an endpoint with **neither** receives the fallback.

### 3.2 Issuing the claim at login

```csharp
[ApiController]
[Route("api/auth")]
public sealed class AuthController(IAuthService auth) : ControllerBase
{
    [HttpPost("login")]
    [AllowAnonymous]                 // ← MANDATORY. Without it the FallbackPolicy 401s the login endpoint itself.
    public async Task<IActionResult> Login([FromBody] LoginRequest req)
    {
        var user = await auth.ValidateCredentialsAsync(req.Username, req.Password);
        if (user is null)
            return Unauthorized(new { message = "Invalid username or password." });  // never reveal WHICH

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),  // SignalR's default user id (§4)
            new(ClaimTypes.Name,           user.Username!),      // ← CurrentUserService reads this (spec §8.1)
        };
        if (user.IsAdmin)
            claims.Add(new Claim("is_admin", "1"));              // ← literal "1". NOT bool.ToString(). See §3.3.

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = req.RememberMe });
            // NB: no ExpiresUtc — let ExpireTimeSpan + SlidingExpiration do their job. §1.2.

        return Ok(new { user.Id, user.Name, user.Username, user.IsAdmin });
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return NoContent();
    }

    [HttpGet("me")]
    public IActionResult Me() => Ok(new
    {
        Name    = User.Identity!.Name,
        IsAdmin = User.HasClaim("is_admin", "1"),
    });
}
```

⚠️ **`GET /api/auth/me` inherits the FallbackPolicy** ⇒ it 401s when logged out. That is almost certainly what you want (Angular calls it on boot: 200 = restore session, 401 = show login). **But make it a deliberate decision.** If Angular's `APP_INITIALIZER` treats a 401 from `/me` as a hard error rather than "not logged in", **the app dead-ends on first load** — a bootstrap deadlock that looks a lot like T5.

The spec's `CurrentUserService` seam works unchanged:

```csharp
// WPF (unchanged)
new CurrentUserService(users, () => Environment.UserName)
// API — reads ClaimTypes.Name, which is what the login above puts in the cookie
new CurrentUserService(users, () => ctx.HttpContext!.User.Identity!.Name!)
```

### 3.3 🔴 T4 — `RequireClaim`'s value matching is `Ordinal` (case-sensitive)

`ClaimsAuthorizationRequirement.cs` (`release/8.0`) `[VERIFIED]`:

```csharp
string.Equals(claim.Type, requirement.ClaimType, StringComparison.OrdinalIgnoreCase)   // TYPE:  case-INsensitive
requirement.AllowedValues!.Contains(claim.Value, StringComparer.Ordinal)               // VALUE: case-SENSITIVE, exact
```

⇒ `RequireClaim("is_admin", "1")` matches **only** the exact string `"1"`.

| If you write | Claim value | Result |
|---|---|---|
| `new Claim("is_admin", "1")` | `"1"` | ✅ pass |
| `new Claim("is_admin", user.IsAdmin.ToString())` | `"True"` | ❌ **403, silently** |
| `new Claim("is_admin", "true")` | `"true"` | ❌ **403, silently** |

The bug is **not diagnosable from the response** (a bare 403) or from any log line.

**Recommended hardening (improves on the spec):** add the claim **only when the user IS an admin**, and require *presence* rather than value:

```csharp
o.AddPolicy("Admin", p => p.RequireClaim("is_admin"));   // no string can be got wrong
```

Costs nothing; removes the whole failure mode.

### 3.4 🔴 T5 — `FallbackPolicy` also blocks the Angular SPA itself

Production is same-origin (spec §10: *"Angular is served same-origin"*). That means:

```csharp
app.UseStaticFiles();                  // runs BEFORE routing/authorization ⇒ real files (main.js, css) are fine
…
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<DataHub>("/hubs/data");
app.MapFallbackToFile("index.html");   // ← ⚠️ this is an ENDPOINT
```

`MapFallbackToFile` registers a **catch-all endpoint** (`{**path:nonfile}` → `/index.html`). It has no authorization metadata ⇒ **the `FallbackPolicy` applies** ⇒ a logged-out user navigating to `https://host/login` gets **401 instead of the Angular shell**. The SPA can never load, so the login form can never render. **Deadlock.** `[CITED]` — dotnet/aspnetcore#18787 (*"UseSpa can't serve anonymous requests if there's a fallback policy in place"*), #35922; Andrew Lock, *Adding metadata to fallback endpoints*.

**Fix — one call:**

```csharp
app.MapFallbackToFile("index.html").AllowAnonymous();
```

The asymmetry that makes this easy to miss: **static files** are served by `UseStaticFiles()`, which runs *before* `UseAuthorization()` and is not an endpoint — those work. Only the **deep-link fallback** breaks. So `https://host/` may appear to work while `https://host/tasklist` 401s. `[VERIFIED]` by middleware-ordering; the `.AllowAnonymous()` remedy is `[CITED]`.

### 3.5 The three admin endpoints (spec §8.3) — and why 401-vs-403 works for free

```csharp
[HttpPost("retention/run")]
[Authorize(Policy = "Admin")]
public Task<IActionResult> RunRetention() => …
```

The split the spec's error contract (§7.1) requires is **not luck** — `PolicyEvaluator` decides `[VERIFIED]`:

| Situation | Result | Cookie event fired | Status |
|---|---|---|---|
| Not authenticated | `Challenge` | `OnRedirectToLogin` (overridden) | **401** |
| Authenticated, policy fails | `Forbid` | `OnRedirectToAccessDenied` (overridden) | **403** |

`AuthorizationMiddleware` delegates to `IAuthorizationMiddlewareResultHandler`, which branches on `authorizeResult.Challenged` vs `.Forbidden`. `[VERIFIED]`

⇒ **Spec §7.1's 401/403 rows need zero custom middleware.** Only the **409** (`ConcurrencyConflictException`) needs an exception filter / `IExceptionHandler`.

---

## 4. SignalR

### 4.1 Setup

```csharp
builder.Services.AddSignalR();          // in Microsoft.AspNetCore.App shared framework — no PackageReference
…
app.MapHub<DataHub>("/hubs/data");
```

### 4.2 ✅ Cookies "just work" on the hub — zero configuration

`[CITED]` — MS Learn, *SignalR authentication and authorization* → *Cookie authentication*:

> *"In a browser-based app, cookie authentication allows existing user credentials to **automatically flow to SignalR connections**. When the browser client is used, **no extra configuration is needed**. If the user is signed in to an app, the SignalR connection automatically inherits this authentication."*

**This is the concrete payoff of D6 that the spec undersells.** With JWT you would be forced into `accessTokenFactory` + `JwtBearerEvents.OnMessageReceived` + `?access_token=` in the query string — because `[CITED]` *"SignalR is unable to set these headers in browsers when WebSockets and Server-Sent Events are used"* — and the token then lands in server access logs (MS explicitly warns about this). With cookies **none of that exists**.

Two consequences:

- `[Authorize]` on the hub works out of the box.
- The **`FallbackPolicy` already protects `/hubs/data`** (`MapHub` creates an endpoint with no auth metadata ⇒ fallback applies). The explicit `[Authorize]` below is therefore redundant — **keep it anyway**: it makes the hub's requirement local and survives someone later removing the fallback.

### 4.3 The hub, with team groups (spec §7.2 · R6 no-leak rule)

```csharp
[Authorize]
public sealed class DataHub(ITeamRepository teams) : Hub
{
    public override async Task OnConnectedAsync()
    {
        // ⚠️ T6: group membership is NOT preserved across reconnects. Rejoin here — EVERY time.
        var userId = int.Parse(Context.User!.FindFirstValue(ClaimTypes.NameIdentifier)!);

        foreach (var teamId in await teams.GetTeamIdsForUserAsync(userId))
            await Groups.AddToGroupAsync(Context.ConnectionId, TeamGroup(teamId));

        await base.OnConnectedAsync();
    }

    // Group names are CASE-SENSITIVE (MS Learn). Build them in ONE place; never inline the string.
    internal static string TeamGroup(int teamId) => $"team-{teamId}";
}
```

**R6 maps onto groups cleanly — but not automatically.** The WPF rule is: `teamIds` empty / `teamId == 0` ⇒ match nothing (never "all"). A user with **no teams** joins **no groups** and therefore receives nothing — the correct R6 behaviour, and it falls out for free. ✅

But MS is blunt about the limit `[CITED]`:

> *"To protect access to resources while using groups, use authentication and authorization … However, **groups are not a security feature**. Authentication claims have features that groups don't, such as expiry and revocation. **If a user's permission to access the group is revoked, the app must remove the user from the group explicitly.**"*

⇒ When an admin changes team membership (Settings → Teams → Members, *replace-all*), **existing connections keep their old groups until they reconnect.**

**The spec is already safe from this — by construction, and it should say so.** The broadcast payload is `DataChanged(DataKind, teamId)` — a pure **invalidation signal carrying no business data**. The client then refetches through the API, which re-checks authorization per request. **Record this as a deliberate constraint: never put row data in a SignalR message.** `[VERIFIED]` the spec's current design satisfies it; `[CITED]` that it must.

### 4.4 Broadcasting from a controller/service — `IHubContext<T>`

`[CITED]` — MS Learn, *SignalR HubContext*. Inject and call; **no reference to the `Hub` class needed at the call site**:

```csharp
public interface IDataClient { Task DataChanged(string kind, int teamId); }

public sealed class DataHub : Hub<IDataClient> { … }      // strongly typed ⇒ no magic method-name string

public sealed class DataChangeNotifier(IHubContext<DataHub, IDataClient> hub) : IDataChangeNotifier
{
    public Task NotifyAsync(DataKind kind, int teamId) =>
        hub.Clients.Group(DataHub.TeamGroup(teamId)).DataChanged(kind.ToString(), teamId);
}
```

```csharp
[HttpPut("{id:int}")]
public async Task<IActionResult> Update(int id, UpdateBacklogRequest req)
{
    await backlogs.UpdateAsync(id, req.ToModel(), req.ExpectedVersion);   // throws ConcurrencyConflictException → 409
    await notifier.NotifyAsync(DataKind.Backlogs, req.TeamId);            // ← replaces WeakReferenceMessenger
    return NoContent();
}
```

⚠️ **T7 — the self-echo.** MS Learn `[CITED]`:

> *"When client methods are called from outside of the `Hub` class, there's no caller associated with the invocation. Therefore, **there's no access to the `ConnectionId`, `Caller`, and `Others` properties**."*

⇒ **You cannot exclude the originating client from an `IHubContext` broadcast.** The user who made the edit receives their own `DataChanged` echo.

Harmless *if* the Angular invalidation is idempotent. **But the spec's own §6.1 UX** — an optimistic-concurrency dialog offering *"See their change"* / *"Overwrite with mine"* — is exactly the kind of in-flight editing state that a spurious self-invalidation can corrupt (refetch clobbers the user's uncommitted edit; or worse, the user's own write triggers their own "someone else changed this" prompt).

**Mitigation:** have the client send its `connectionId` on the mutating request and broadcast with `Clients.GroupExcept(group, connectionId)`. **The spec does not address this — see §10.2.**

### 4.5 Two behaviours that produce "ghost bugs"

| Behaviour | Source | Consequence here |
|---|---|---|
| **T6 — "Group membership isn't preserved when a connection reconnects. The connection needs to rejoin the group when it's re-established."** | `[CITED]` MS Learn, *groups* | Every laptop-sleep / Wi-Fi blip silently drops the user out of their team group. Cross-user sync just **stops**, with no error and no reconnect failure. ⇒ **`OnConnectedAsync` MUST rejoin, unconditionally.** (Done in §4.3.) |
| **T9 — "SignalR captures the authenticated user when a connection is established and **caches it for the lifetime of the connection** … SignalR doesn't automatically revalidate the user during the life of the connection, **regardless of the authentication scheme**."** | `[CITED]` MS Learn, *authn-and-authz* | Demote an admin → their **open hub connection keeps the `is_admin` claim** until they reconnect. Irrelevant *today* (our hub has no admin methods; the 3 admin endpoints are HTTP, where the cookie IS re-evaluated per request). **But write it down** — the day someone adds `[Authorize("Admin")]` to a hub method it becomes a real hole. |

Also: **groups are in-memory** `[CITED]` ⇒ they do not survive an app-pool recycle, and they do not work across 2 IIS worker processes. Single-worker on-prem (D1/D2) ⇒ fine today. **Add "SignalR needs a backplane (Redis / Azure SignalR) to scale out" to the spec's §13 porting-surface list** — same reasoning the spec already applies to SQLite: the cost of leaving should be a known number, not a discovery.

---

## 5. Testing: `WebApplicationFactory` + cookie auth

### 5.1 Project setup — this needs a 4th project, not a modified 3rd

```xml
<!-- TimesheetApp.Api.Tests.csproj -->
<Project Sdk="Microsoft.NET.Sdk.Web">   <!-- ← Web SDK is required to reference an ASP.NET Core app -->
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>   <!-- ← NOT net8.0-windows -->
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="8.0.11" />
    <PackageReference Include="Microsoft.NET.Test.Sdk"           Version="17.11.1" />
    <PackageReference Include="xunit"                            Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio"        Version="2.8.2" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\TimesheetApp.Api\TimesheetApp.Api.csproj" />
  </ItemGroup>
</Project>
```

⚠️ **The existing `TimesheetApp.Tests.csproj` is `Microsoft.NET.Sdk` + `net8.0-windows` + `<ProjectReference>` to the WPF project.** `[VERIFIED]` — read the file. It **cannot** host `WebApplicationFactory` as-is (no Web SDK ⇒ the ASP.NET Core shared framework won't resolve).

**Do not retarget it.** That project holds the **548 tests that ARE the M8.1 acceptance gate** (spec §5.3: *"548/548 green … If not, stop"*). Changing its SDK and TFM is the single change most likely to break that gate for reasons entirely unrelated to the Core extraction — and you'd be debugging the safety net instead of using it.

⇒ **Add `TimesheetApp.Api.Tests` as a separate project.** See §10.3.

`Program` must be reachable — with top-level statements, add at the bottom of `Program.cs` `[CITED]`:

```csharp
public partial class Program { }   // makes Program public for WebApplicationFactory<Program>
```

### 5.2 🔴 T2 — `SecurePolicy=Always` + the default `BaseAddress` ⇒ every auth test 401s

`WebApplicationFactoryClientOptions` defaults `[CITED]` — MS Learn:

| Property | Default |
|---|---|
| `BaseAddress` | **`http://localhost`** ← plain HTTP |
| `AllowAutoRedirect` | **`true`** |
| `HandleCookies` | `true` |
| `MaxAutomaticRedirections` | `7` |

`HandleCookies: true` wraps the handler in a `CookieContainerHandler` backed by `System.Net.CookieContainer`. And `CookieContainer` (`dotnet/runtime`, `release/8.0`) `[VERIFIED]`:

```csharp
bool isSecure = (uri.Scheme == UriScheme.Https || uri.Scheme == UriScheme.Wss);
…
// Refuse to add a secure cookie into an 'unsecure' destination
if (cookie.Secure && !isSecure)
{
    to_add = false;
}
```

⇒ The server issues `Set-Cookie: …; secure`. The container **stores** it. On the next request to `http://localhost` it **refuses to send it**. The test client is silently anonymous.

**The failure mode is maximally misleading:** every *authenticated* assertion 401s, while **the login test itself passes** (its 200 + `Set-Cookie` come back fine). So the evidence points squarely at "the protected endpoints are broken" — which they aren't.

**Fix — one line:**

```csharp
var client = factory.CreateClient(new WebApplicationFactoryClientOptions
{
    BaseAddress       = new Uri("https://localhost"),  // ← T2: scheme must be https, or Secure cookies are dropped
    AllowAutoRedirect = false,                         // ← T1 canary: fail loudly on a 302 instead of chasing it
});
```

`TestServer` performs **no real TLS** — it is an in-memory pipeline — so `https://` costs nothing and needs no dev cert. It is purely the **scheme string on the request URI** that `CookieContainer` inspects. `[VERIFIED]`

`AllowAutoRedirect = false` is the **regression canary for T1**: if anyone reverts the `OnRedirectToLogin` override, the test asserts `401`, sees `302`, and fails unambiguously — instead of following the redirect and reporting a baffling `404`.

### 5.3 The tests the spec asks for (§9)

```csharp
public class AuthApiTests(TimesheetApiFactory factory) : IClassFixture<TimesheetApiFactory>
{
    private HttpClient NewClient() => factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress       = new Uri("https://localhost"),   // T2
        AllowAutoRedirect = false,                          // T1
    });

    [Fact]
    public async Task Login_then_call_protected_endpoint_succeeds()
    {
        var client = NewClient();                            // HandleCookies:true ⇒ the cookie round-trips

        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { username = "nhan", password = "Correct-Horse-1", rememberMe = true });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);

        var setCookie = Assert.Single(login.Headers.GetValues("Set-Cookie"));
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure",   setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("expires",  setCookie, StringComparison.OrdinalIgnoreCase);  // ⇐ proves IsPersistent worked

        var me = await client.GetAsync("/api/auth/me");       // same client ⇒ cookie attached automatically
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
    }

    [Fact]                                                    // ← THE regression test for T1
    public async Task Protected_endpoint_without_cookie_returns_401_not_302()
    {
        var res = await NewClient().GetAsync("/api/backlogs");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.False(res.Headers.Contains("Location"));       // and it must not even smell like a redirect
    }

    [Fact]                                                    // ← proves the Challenge/Forbid split (§3.5)
    public async Task Admin_policy_denies_authenticated_non_admin_with_403()
    {
        var client = NewClient();
        await client.PostAsJsonAsync("/api/auth/login",
            new { username = "chi.le", password = "Correct-Horse-2", rememberMe = false });   // is_admin = 0

        var res = await client.PostAsync("/api/ops/retention/run", null);

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);   // 403, NOT 401
    }

    [Fact]
    public async Task Bad_password_returns_401_and_issues_no_cookie()
    {
        var res = await NewClient().PostAsJsonAsync("/api/auth/login",
            new { username = "nhan", password = "wrong", rememberMe = false });

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.False(res.Headers.Contains("Set-Cookie"));
    }
}
```

Tests 2 and 3 are the ones with teeth: between them they prove the **entire** auth pipeline — challenge path, forbid path, claim value, fallback policy, and the T1 override — in four assertions.

### 5.4 The factory

```csharp
public sealed class TimesheetApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ts-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Timesheet"] = $"Data Source={_dbPath}",
            ["Bootstrap:AdminPassword"]     = "Correct-Horse-1",   // exercises the spec §8.2 bootstrap path
        }));

        // ConfigureTestServices runs AFTER Program.cs ⇒ this is where you REPLACE registrations. [CITED]
        builder.ConfigureTestServices(services => { /* e.g. a fake IClock */ });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
    }
}
```

⚠️ **T11 — do NOT use `Data Source=:memory:` here.** With `Pooling=true` (the API connection profile, spec §5.2) a SQLite in-memory database is scoped to a **connection**; the instant the pool hands out a second connection you are talking to an **empty database**. Tests then pass in isolation and fail in a class. **Use a temp file** — boring and correct. `[ASSUMED]` — standard `Microsoft.Data.Sqlite` semantics; the `file:x?mode=memory&cache=shared` form exists but interacts badly with pooling and is not worth the cleverness here.

This also means the **spec's own new test category — "WAL behaviour under concurrent writers" (§9) — requires a real file**, which the above gives you for free.

---

## 6. Hosting on IIS (on-prem)

### 6.1 What the server needs

1. **Windows / Windows Server with the Web Server (IIS) role.** For SignalR, **enable the WebSocket Protocol feature**: *World Wide Web Services → Application Development Features → WebSocket Protocol*. `[CITED]`
   ⚠️ **Without it, SignalR silently degrades to long-polling** — the app still "works", just slower and with mysteriously laggy cross-user updates. Same failure signature as a missing `"ws": true` in the Angular proxy (§7), from a completely different cause.
2. **The .NET 8 Hosting Bundle** — installs the .NET runtime, the ASP.NET Core Shared Framework, and **ASP.NET Core Module v2 (ANCM)**. `[CITED]`
   ⚠️ **T12:** *"If the Hosting Bundle is installed **before** IIS, the bundle installation must be repaired. Run the Hosting Bundle installer again after installing IIS."* `[CITED]` — a classic first-deploy 500.19.
   After installing: `net stop was /y && net start w3svc`. `[CITED]`
3. **App pool → .NET CLR version = `No Managed Code`.** `[CITED]` (ASP.NET Core boots CoreCLR itself; it never loads the desktop CLR.)
4. **App pool identity = `ApplicationPoolIdentity`** (default), granted read/write on the app folder **and on the SQLite DB file's folder** — SQLite needs write access to the *directory* (for `-wal`/`-shm`), not just the file. `[CITED]` MS Learn says the app pool *"requires read and write access to folders where the app reads and writes files"*; `[ASSUMED]` for the specific WAL sidecar-file point, though it follows directly from how SQLite WAL works.

### 6.2 `Microsoft.AspNetCore.Server.IIS` — you do **not** reference it

`[VERIFIED]` — it is part of the `Microsoft.AspNetCore.App` **shared framework**. With `<Project Sdk="Microsoft.NET.Sdk.Web">` on .NET 8, `WebApplication.CreateBuilder` calls `UseIIS()` automatically when it detects it is running behind ANCM.

⇒ **No `PackageReference`. No `UseIIS()` call in your code.** Adding the package explicitly is a widespread cargo-cult and can produce version-conflict warnings against the shared framework.

### 6.3 `web.config` — generated, not hand-written

The Web SDK's `_TransformWebConfig` MSBuild target emits it on `dotnet publish`. `[CITED]` This is the published output for a framework-dependent deployment `[CITED]`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <location path="." inheritInChildApplications="false">
    <system.webServer>
      <handlers>
        <add name="aspNetCore" path="*" verb="*" modules="AspNetCoreModuleV2" resourceType="Unspecified" />
      </handlers>
      <aspNetCore processPath="dotnet"
                  arguments=".\TimesheetApp.Api.dll"
                  stdoutLogEnabled="false"
                  stdoutLogFile=".\logs\stdout"
                  hostingModel="inprocess" />
    </system.webServer>
  </location>
</configuration>
```

- **`hostingModel="inprocess"` is the default** on ASP.NET Core 3.0+. `[VERIFIED]` — *"In-process hosting is set with `InProcess`, which is the **default value**."* In-process = the app runs **inside `w3wp.exe`** on IIS HTTP Server rather than on Kestrel behind a reverse proxy. Faster, and correct here. **Do not set `<AspNetCoreHostingModel>` at all.**
- *"The `web.config` file must be present in the deployment at all times, correctly named, and able to configure the site for normal start up. **Never remove the `web.config` file from a production deployment.**"* `[CITED]`
- **Passing `Bootstrap:AdminPassword`** (spec §8.2): either an `<environmentVariables>` element inside `<aspNetCore>` (env-var name `Bootstrap__AdminPassword` — **double underscore**) or `appsettings.Production.json` deployed alongside. `[CITED]`
  ⚠️ **`web.config` is readable by anyone with file access to the site folder.** For the bootstrap password this is tolerable **only because** the spec constrains it to be effectively single-use (applied only when `password_hash IS NULL`, §8.2) and the admin changes it immediately. **Put that sentence in the deploy runbook**, or the password will still be sitting there in a year.

### 6.4 🔴 T3 — Data Protection keys, or: why everyone gets logged out at 3 a.m.

**This is the finding that most directly threatens the spec's own stated goal** — *"Users log in with username + password, **and stay logged in across browser restarts**"* (§2).

The auth cookie is **encrypted with the Data Protection key ring**. `[CITED]` — the cookie doc says so explicitly: *"ASP.NET Core's Data Protection system is used for encryption."*

And `[CITED]` — MS Learn, *Host ASP.NET Core on Windows with IIS → Data protection*:

> *"Even if Data Protection APIs aren't called by user code, data protection **should be configured** with a deployment script or in user code to create a persistent cryptographic key store. **If data protection isn't configured, the keys are held in memory and discarded when the app restarts.**"*
>
> *"If the key ring is stored in memory when the app restarts:*
> - ***All cookie-based authentication tokens are invalidated.***
> - ***Users are required to sign in again on their next request.***
> - *Any data protected with the key ring can no longer be decrypted."*

IIS app pools recycle **by default**: on a ~29-hour timer, after 20 minutes idle, and on every deploy. ⇒ **Without this, the 30-day persistent cookie is a lie.** Users get bounced to the login screen roughly daily, at unpredictable times, and it will be reported as *"the session thing is flaky"* — with nothing in the logs.

**Three sanctioned fixes** `[CITED]`. Pick one; put it in the deploy runbook:

| Option | How | Notes |
|---|---|---|
| **A. App pool: Load User Profile = `True`** | IIS Manager → app pool → Advanced Settings → Process Model → **Load User Profile = True**. Keys land in `%LOCALAPPDATA%\ASP.NET\DataProtection-Keys`, DPAPI-encrypted to the app-pool account. | ⚠️ **Also requires `setProfileEnvironment="true"`** in `%windir%\system32\inetsrv\config\applicationHost.config` under `<processModel>`. It *defaults* to true, **but MS explicitly warns: "In some scenarios (for example, Windows OS), `setProfileEnvironment` is set to `false`."** ⇒ **verify it; do not assume.** |
| **B. Registry keys per app pool** | Run MS's `Provision-AutoGenKeys.ps1` for the app pool. Keys in HKLM, DPAPI machine key, readable only by the worker-process account. | MS recommends this for *"standalone, non-webfarm IIS installations"* — i.e. exactly D1. |
| **C. File system, in code** ⭐ | See below. | ⚠️ *"**If you change the key persistence location, the system no longer automatically encrypts keys at rest**"* `[CITED]` ⇒ **must** be paired with `ProtectKeysWithDpapi()` and a folder ACL restricted to the app-pool identity. |

**Recommendation → C**, precisely *because* A and B are invisible tribal knowledge that a server rebuild silently loses. C lives **in the repo**, is reviewable, and fails **visibly** (missing directory at startup) rather than silently (a fresh key ring, and everyone logged out).

```csharp
builder.Services.AddDataProtection()
    .SetApplicationName("TimesheetApp")                                   // ← see below. Do not omit.
    .PersistKeysToFileSystem(new DirectoryInfo(@"D:\keys\timesheet"))     // ACL: app-pool identity only
    .ProtectKeysWithDpapi();                                              // ← restores the at-rest encryption that
                                                                          //   PersistKeysToFileSystem turns off
```

`SetApplicationName` `[CITED]`: *"sets the unique name of this app within the data protection system. **The value should match across deployments of the app.**"* **Set it.** Without it the discriminator is derived from the content-root path — so deploying to a new folder (`…\site_v2\`) invalidates **every** cookie. Another 3-a.m. mass-logout, with an even less obvious cause.

### 6.5 HTTPS and `SecurePolicy = Always`

`Cookie.SecurePolicy = Always` ⇒ the browser sends the cookie **only over HTTPS**. On IIS:

1. **Bind the site to `https` (443)** with a certificate in `LocalMachine\My`.
   ⚠️ **If it is self-signed it must be pushed to Trusted Root on every client machine** (Group Policy). Otherwise Chrome's interstitial blocks the app — and, worse, some `fetch`/WebSocket paths fail *without* an interstitial to explain why. `[ASSUMED]` — the GPO mechanism is standard practice; I have not verified this environment's AD/CA setup. **This has procurement/AD lead time and is on the critical path for M8.3 UAT — see §10.5.**
2. **In-process hosting ⇒ TLS terminates at IIS**, and IIS HTTP Server surfaces the scheme directly to the app. So `Request.IsHttps` is `true` and `SecurePolicy=Always` behaves. `[ASSUMED]` — native for **in-process** ANCM (no forwarded-headers dance needed). For **out-of-process**, `UseIISIntegration` wires up Forwarded Headers Middleware to preserve it `[CITED]`. Either works; **staying in-process makes it a non-question.**
3. `app.UseHttpsRedirection();` — keep. `app.UseHsts()` — production only, never in Development. `[CITED]`

**Dev:** `dotnet dev-certs https --trust` (one-time). If it misbehaves: `dotnet dev-certs https --clean && dotnet dev-certs https --trust`. `[CITED]`

⚠️ **The spec's §10 instruction — *"Do not weaken the cookie policy per-environment"* — is correct and should be enforced as a code-review rule.** A `CookieSecurePolicy.SameAsRequest` guarded by `if (env.IsDevelopment())` is exactly the shape of thing that gets copy-pasted into a production config six months later, by someone debugging something else.

---

## 7. Angular dev proxy (`/api` + `/hubs`)

**No Angular app exists in the repo yet** `[VERIFIED]` — no `angular.json`, no `package.json`, no `proxy.conf.json` anywhere. Greenfield; we can set this up right the first time.

### 7.1 `proxy.conf.json`

```json
{
  "/api": {
    "target": "https://localhost:7001",
    "secure": false,
    "changeOrigin": false,
    "logLevel": "debug"
  },
  "/hubs": {
    "target": "https://localhost:7001",
    "secure": false,
    "changeOrigin": false,
    "ws": true,
    "logLevel": "debug"
  }
}
```

Wire it in `angular.json` `[CITED]` — angular.dev:

```json
{
  "projects": {
    "timesheet-web": {
      "architect": {
        "serve": {
          "builder": "@angular/build:dev-server",
          "options": { "proxyConfig": "src/proxy.conf.json" }
        }
      }
    }
  }
}
```

### 7.2 Why each key is there — and the two that are load-bearing

| Key | Why |
|---|---|
| **`"ws": true`** on `/hubs` | 🔴 **T8. Without it the WebSocket `Upgrade` is not proxied.** SignalR then falls back to long-polling — **or fails outright**. It fails *quietly*: the app still works, just slower, with mysteriously dropped updates. Angular's dev server delegates to **Vite's `server.proxy`, which is built on `http-proxy-3` and documents `ws: true` for exactly this** `[CITED]` — <https://vite.dev/config/server-options> (their own sample: `'/socket.io': { target: 'ws://localhost:5174', ws: true }`). The spec (§10) already flags this — **confirmed correct.** |
| **`"secure": false`** | The dev API runs on the **self-signed** `dotnet dev-certs` certificate. Node (which *is* the proxy) does not trust it and will reject the **upstream** TLS handshake. `secure: false` disables upstream cert validation **inside the proxy's Node process only** — the browser→Vite hop is untouched. Dev-only; there is no proxy in production. `[CITED]` — angular.dev's own sample uses `"secure": false`. |
| `"changeOrigin": false` | **Deliberately false.** `changeOrigin: true` rewrites the `Host` header to the target — unnecessary same-origin, and rewriting `Origin` on a WebSocket is the shape of a CSRF hole. Vite's docs warn: *"Exercise caution using `rewriteWsOrigin` as it can leave the proxying open to CSRF attacks."* `[CITED]` |
| `"logLevel": "debug"` | The proxy fails **silently** by default. When `/hubs` 404s, this is the difference between five minutes and an afternoon. Remove once stable. |

⚠️ **Changes to `proxy.conf.json` require restarting `ng serve`.** `[CITED]` — angular.dev. Nothing hot-reloads it, and roughly half of all *"the proxy doesn't work"* reports are exactly this.

### 7.3 The Angular client side

```typescript
// The proxy makes /api and /hubs SAME-ORIGIN ⇒ the browser attaches the cookie automatically.
this.http.post('/api/auth/login', { username, password, rememberMe });

// SignalR: relative URL ⇒ goes through the proxy ⇒ cookie flows. Zero auth configuration. (§4.2)
this.connection = new signalR.HubConnectionBuilder()
    .withUrl('/hubs/data')
    .withAutomaticReconnect()
    .build();
```

⚠️ **T10 — use relative URLs (`/api/...`); never absolute (`https://localhost:7001/api/...`).** An absolute URL **bypasses the proxy**, makes the request **cross-origin**, and the browser then refuses to attach a `SameSite=Lax` cookie.

**This produces precisely the same symptom as T1** — a request that looks unauthenticated — from an entirely unrelated cause. Between T1 and T10, *"401/302 in dev"* has **two independent explanations**; knowing both up front is worth an afternoon.

### 7.4 Production: no proxy

In production Angular is served **same-origin** from the API — `app.UseStaticFiles()` + `app.MapFallbackToFile("index.html").AllowAnonymous()` (**T5**, §3.4). `proxy.conf.json` is a **dev-only artefact**, read only by the `serve` builder; it is not part of `ng build`. `[VERIFIED]`

---

## 8. Package manifest — everything the API project needs

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <!-- Do NOT set AspNetCoreHostingModel — "inprocess" is already the default. §6.3 -->
  </PropertyGroup>

  <ItemGroup>
    <!-- The ONLY new dependency for auth. Pulls KeyDerivation + Cryptography.Internal. No EF Core. §2.1 -->
    <PackageReference Include="Microsoft.Extensions.Identity.Core" Version="8.0.11" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\TimesheetApp.Core\TimesheetApp.Core.csproj" />
  </ItemGroup>

</Project>
```

**Already in the `Microsoft.AspNetCore.App` shared framework — no `PackageReference` needed** `[VERIFIED]`:

`Microsoft.AspNetCore.Authentication.Cookies` · `Microsoft.AspNetCore.Authorization` · `Microsoft.AspNetCore.SignalR` · `Microsoft.AspNetCore.DataProtection` · `Microsoft.AspNetCore.Server.IIS`

**Version discipline:** pin `8.0.11` to sit in the same band as the existing `Microsoft.Data.Sqlite 8.0.10`. Any `8.0.*` is fine, but **do not mix 8.x and 9.x** — `Microsoft.Extensions.Identity.Core 9.x` would drag `Microsoft.Extensions.Logging 9.x` into a `net8.0` app and produce NU1605 downgrade warnings against the shared framework.

---

## 9. Consolidated pitfall register

| # | Pitfall | How it shows up | Fix | § | Tag |
|---|---|---|---|---|---|
| **T1** | .NET 8 cookie handler returns **302 → `/Account/Login`**, not 401, unless `X-Requested-With: XMLHttpRequest` is present. Angular never sends it; SignalR always does. | Angular sees **404**, not 401. The login redirect never fires. Hub works, API doesn't. | Override `OnRedirectToLogin`→401 and `OnRedirectToAccessDenied`→403. | §1.4 | `[VERIFIED]` src |
| **T2** | `SecurePolicy=Always` + `WebApplicationFactory` default `BaseAddress = http://localhost` ⇒ `CookieContainer` **drops the cookie**. | *Every* authed test 401s — while the login test passes. | `BaseAddress = new Uri("https://localhost")`. | §5.2 | `[VERIFIED]` src |
| **T3** | IIS + unconfigured Data Protection ⇒ key ring **in memory** ⇒ app-pool recycle **logs everyone out**. | Random mass logouts, ~daily. Defeats the 30-day cookie. Nothing in the logs. | `PersistKeysToFileSystem` + `ProtectKeysWithDpapi` + `SetApplicationName`. | §6.4 | `[CITED]` |
| **T4** | `RequireClaim("is_admin","1")` matches the **value Ordinal / case-sensitively**. `bool.ToString()` → `"True"` → always fails. | Silent 403 for the admin. No log. | Emit the claim only for admins; use `RequireClaim("is_admin")` (presence). | §3.3 | `[VERIFIED]` src |
| **T5** | `FallbackPolicy` applies to the `MapFallbackToFile` **endpoint** ⇒ the SPA shell 401s when logged out ⇒ login page unreachable. | Deep links 401 while `/` works. Bounce loop. | `.AllowAnonymous()` on `MapFallbackToFile`. | §3.4 | `[CITED]` |
| **T6** | SignalR **group membership is lost on every reconnect**. | Cross-user sync silently stops after a Wi-Fi blip. No error. | Rejoin groups in `OnConnectedAsync`. | §4.3 | `[CITED]` |
| **T7** | `IHubContext` has **no `Others`/`Caller`** ⇒ the editor receives their own `DataChanged` echo. | Optimistic-update flicker; possible self-triggered 409 dialog. | Send `connectionId` on the mutation → `Clients.GroupExcept(...)`. | §4.4 | `[CITED]` |
| **T8** | Missing IIS **WebSocket Protocol** feature, or missing `"ws": true` in the proxy. | SignalR silently degrades to long-polling. | Enable the IIS feature; set `"ws": true`. | §6.1, §7.2 | `[CITED]` |
| **T9** | SignalR **caches the principal for the connection's lifetime** — no revalidation, any scheme. | A demoted admin keeps `is_admin` on an open hub connection. | Harmless today (no admin hub methods). Document before anyone adds one. | §4.5 | `[CITED]` |
| **T10** | Absolute URLs in Angular bypass the proxy ⇒ cross-origin ⇒ `SameSite=Lax` cookie not attached. | **Looks identical to T1.** | Relative URLs only. | §7.3 | `[VERIFIED]` |
| **T11** | `Data Source=:memory:` + `Pooling=true` ⇒ the second pooled connection sees an **empty DB**. | Tests pass alone, fail in a class. | Temp-file DB per test factory. | §5.4 | `[ASSUMED]` |
| **T12** | Hosting Bundle installed **before** IIS ⇒ ANCM unregistered ⇒ **500.19**. | First deploy fails cryptically. | Re-run / repair the Hosting Bundle after IIS. | §6.1 | `[CITED]` |

---

## 10. Gaps in the spec worth raising before STEP 5

Surfaced by the stack research; **not currently answered by the spec**. All are cheap now and expensive later.

### 10.1 Login does not check `is_active`
A soft-deleted user (USR-03: `is_active = 0`, TimeLogs preserved) who already has a `password_hash` **can still log in**. One-line fix (§2.3), but it must be *decided*: today "deactivate" means *"hide from lists"*; after M8 it must also mean *"revoke access"*. That is a semantic change to an existing feature, not a new one — it belongs in the spec, not in a code review.

### 10.2 The SignalR self-echo (T7)
The mutating user receives their own `DataChanged`. The spec's §6.1 Angular contract — an optimistic-concurrency dialog offering *"See their change"* / *"Overwrite with mine"* — is precisely the in-flight editing state that a spurious self-invalidation can corrupt. Needs a decision: **`GroupExcept(connectionId)`**, or **an idempotent client-side invalidation** that provably cannot clobber an in-flight edit.

### 10.3 Integration tests need a **4th project**
`TimesheetApp.Tests` is `Microsoft.NET.Sdk` + `net8.0-windows` and holds the **548 tests that ARE the M8.1 acceptance gate**. It cannot host `WebApplicationFactory`, and retargeting it is the single change most likely to break the gate for reasons unrelated to the extraction. ⇒ **Add `TimesheetApp.Api.Tests` (`Microsoft.NET.Sdk.Web`, `net8.0`).** The spec's §5 solution structure shows 3 projects; it should show 4.

### 10.4 Data Protection is **absent from the spec entirely**
It is the load-bearing dependency of the *"stay logged in"* goal (§2) and it is **invisible until production**. It needs a line in the design (§8) and a line in the deploy runbook. This is the highest-severity gap on the list.

### 10.5 HTTPS certificate provenance is unresolved
`SecurePolicy = Always` is a hard dependency on a **trusted** certificate on **every client machine**. Internal CA, or self-signed + GPO push? This has procurement / AD lead time and sits on the **critical path for M8.3's UAT** — a slice that cannot be demonstrated without it. **Ask the user now, not at UAT.**

### 10.6 CSRF rests on an unstated invariant
`SameSite=Lax` is sufficient **only while** no state-changing endpoint accepts GET. Nothing enforces that. Write it into the spec as an explicit invariant so that violating it is understood to reopen the CSRF question.

### 10.7 SignalR groups are in-memory ⇒ single worker process
Fine for D1/D2 (10–50 users, one IIS box). But it belongs in the spec's **§13 porting-surface list** alongside the SQLite constructs, for the identical stated reason: *"the cost of leaving should be a number we should know, not discover."* Scaling out ⇒ Redis backplane or Azure SignalR.

---

## 11. Sources

**Microsoft Learn (aspnetcore-8.0 moniker):**
- [Use cookie authentication without ASP.NET Core Identity](https://learn.microsoft.com/en-us/aspnet/core/security/authentication/cookie?view=aspnetcore-8.0)
- [Cookie login redirects are disabled for known API endpoints](https://learn.microsoft.com/en-us/dotnet/core/compatibility/aspnet-core/10/cookie-authentication-api-endpoints) — the .NET 10 breaking change; **documents the .NET 8 behaviour we must work around (T1)**
- [Authorize with a specific scheme](https://learn.microsoft.com/en-us/aspnet/core/security/authorization/limitingidentitybyscheme?view=aspnetcore-8.0)
- [Create an ASP.NET Core app with user data protected by authorization](https://learn.microsoft.com/en-us/aspnet/core/security/authorization/secure-data?view=aspnetcore-8.0) — `FallbackPolicy`
- [SignalR authentication and authorization](https://learn.microsoft.com/en-us/aspnet/core/signalr/authn-and-authz?view=aspnetcore-8.0)
- [Manage users and groups in SignalR](https://learn.microsoft.com/en-us/aspnet/core/signalr/groups?view=aspnetcore-8.0)
- [SignalR HubContext](https://learn.microsoft.com/en-us/aspnet/core/signalr/hubcontext?view=aspnetcore-8.0)
- [Integration tests in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/test/integration-tests?view=aspnetcore-8.0)
- [Host ASP.NET Core on Windows with IIS](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/iis/?view=aspnetcore-8.0)
- [ASP.NET Core Module (ANCM) for IIS](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/aspnet-core-module?view=aspnetcore-8.0)
- [Configure ASP.NET Core Data Protection](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/overview?view=aspnetcore-8.0)
- [Enforce HTTPS in ASP.NET Core (`dotnet dev-certs`)](https://learn.microsoft.com/en-us/aspnet/core/security/enforcing-ssl?view=aspnetcore-8.0)
- [PasswordHasher&lt;TUser&gt; Class](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.identity.passwordhasher-1?view=aspnetcore-8.0)

**Source read directly (`release/8.0`) — every `[VERIFIED]` claim above:**
- [`PasswordHasherOptions.cs`](https://github.com/dotnet/aspnetcore/blob/release/8.0/src/Identity/Extensions.Core/src/PasswordHasherOptions.cs) — `IterationCount = 100_000`, `CompatibilityMode = IdentityV3`
- [`PasswordHasher.cs`](https://github.com/dotnet/aspnetcore/blob/release/8.0/src/Identity/Extensions.Core/src/PasswordHasher.cs) — HMACSHA512; `where TUser : class`; optional ctor arg; `user` param ignored
- [`CookieAuthenticationEvents.cs`](https://github.com/dotnet/aspnetcore/blob/release/8.0/src/Security/Authentication/Cookies/src/CookieAuthenticationEvents.cs) — the `IsAjaxRequest` / `Response.Redirect` default (**T1**)
- [`CookieAuthenticationDefaults.cs`](https://github.com/dotnet/aspnetcore/blob/release/8.0/src/Security/Authentication/Cookies/src/CookieAuthenticationDefaults.cs) — `LoginPath = "/Account/Login"`
- [`ClaimsAuthorizationRequirement.cs`](https://github.com/dotnet/aspnetcore/blob/release/8.0/src/Security/Authorization/Core/src/ClaimsAuthorizationRequirement.cs) — `StringComparer.Ordinal` on the claim value (**T4**)
- [`AuthorizationMiddleware.cs`](https://github.com/dotnet/aspnetcore/blob/release/8.0/src/Security/Authorization/Policy/src/AuthorizationMiddleware.cs) — `IAllowAnonymous` short-circuit; Challenge/Forbid split
- [`XhrHttpClient.ts`](https://github.com/dotnet/aspnetcore/blob/release/8.0/src/SignalR/clients/ts/signalr/src/XhrHttpClient.ts) — the SignalR JS client **does** send `X-Requested-With`
- [`CookieContainer.cs` (dotnet/runtime, release/8.0)](https://github.com/dotnet/runtime/blob/release/8.0/src/libraries/System.Net.Primitives/src/System/Net/CookieContainer.cs) — *"Refuse to add a secure cookie into an 'unsecure' destination"* (**T2**)

**NuGet (dependency graphs):**
- [Microsoft.Extensions.Identity.Core 8.0.11](https://www.nuget.org/packages/Microsoft.Extensions.Identity.Core/8.0.11) — **no EF Core**
- [Microsoft.AspNetCore.Cryptography.KeyDerivation 8.0.11](https://www.nuget.org/packages/Microsoft.AspNetCore.Cryptography.KeyDerivation/8.0.11)

**Angular / Vite:**
- [Angular CLI — `ng serve` / proxying to a backend server](https://angular.dev/tools/cli/serve)
- [Vite — `server.proxy` (built on `http-proxy-3`; documents `ws: true`)](https://vite.dev/config/server-options)
- [angular/angular.js#1004 — dropping `X-Requested-With` from the default `$http` config](https://github.com/angular/angular.js/issues/1004)

**SPA + FallbackPolicy (T5):**
- [dotnet/aspnetcore#18787 — UseSpa can't serve anonymous requests if there's a fallback policy in place](https://github.com/dotnet/aspnetcore/issues/18787)
- [dotnet/aspnetcore#35922 — 404 page with FallbackPolicy](https://github.com/dotnet/aspnetcore/issues/35922)
- [Andrew Lock — Adding metadata to fallback endpoints in ASP.NET Core](https://andrewlock.net/adding-metadata-to-fallback-endpoints-in-aspnetcore/)
