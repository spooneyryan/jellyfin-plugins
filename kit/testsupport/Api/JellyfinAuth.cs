using System;
using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jellyfin.Plugin.Kit.TestSupport.Api;

/// <summary>
/// A copy of Jellyfin 10.11.5's authentication claims and its <c>RequiresElevation</c> policy, for tests of
/// <c>CurrentUser</c> and of controllers. Jellyfin.Api has no package, so the literals are copied and cited.
/// </summary>
internal static class JellyfinAuth
{
    /// <summary>The scheme (v10.11.5 Jellyfin.Api/Constants/AuthenticationSchemes.cs:11).</summary>
    public const string Scheme = "CustomAuthentication";

    /// <summary>The administrator role (Jellyfin.Api/Constants/UserRoles.cs:21).</summary>
    public const string AdministratorRole = "Administrator";

    /// <summary>The user role (Jellyfin.Api/Constants/UserRoles.cs:16).</summary>
    public const string UserRole = "User";

    /// <summary>The request header <see cref="TestAuthenticationHandler"/> reads: <c>admin</c>, <c>user</c> or <c>apikey</c>.</summary>
    public const string CallerHeader = "X-Test-Caller";

    /// <summary>The request header with the caller's user id (N format); a fixed id is used without it.</summary>
    public const string UserIdHeader = "X-Test-UserId";

    /// <summary>
    /// Registers Jellyfin's default policy and <c>RequiresElevation</c> as
    /// <c>ApiServiceCollectionExtensions.AddJellyfinApiAuthorization</c> does (v10.11.5 Jellyfin.Server/Extensions/ApiServiceCollectionExtensions.cs:57-100).
    /// The default policy's <c>DefaultAuthorizationRequirement</c> is internal to Jellyfin.Api; an authenticated user stands in for it.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddJellyfinAuthorization(this IServiceCollection services)
        => services.AddAuthorizationCore(options =>
        {
            options.DefaultPolicy = new AuthorizationPolicyBuilder(Scheme).RequireAuthenticatedUser().Build();
            options.AddPolicy(
                Policies.RequiresElevation,
                policy => policy.AddAuthenticationSchemes(Scheme).RequireClaim(ClaimTypes.Role, AdministratorRole));
        });

    /// <summary>
    /// Registers <see cref="TestAuthenticationHandler"/> as the <see cref="Scheme"/> scheme.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddJellyfinTestAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(Scheme).AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(Scheme, null);
        return services;
    }

    /// <summary>
    /// Builds the principal Jellyfin's <c>CustomAuthenticationHandler</c> creates (v10.11.5 Jellyfin.Api/Auth/CustomAuthenticationHandler.cs:53-78):
    /// role Administrator for API keys and administrators, the user id in N format (empty for API keys), and
    /// <c>IsApiKey</c> as <c>bool.ToString</c>.
    /// </summary>
    /// <param name="userId">The user id; ignored for API keys.</param>
    /// <param name="isAdministrator">Whether the user's policy says administrator.</param>
    /// <param name="isApiKey">Whether the request used an API key.</param>
    /// <returns>The principal.</returns>
    public static ClaimsPrincipal CreatePrincipal(Guid userId, bool isAdministrator, bool isApiKey = false)
    {
        var role = isApiKey || isAdministrator ? AdministratorRole : UserRole;
        var id = isApiKey ? Guid.Empty : userId;
        Claim[] claims =
        [
            new(ClaimTypes.Name, isApiKey ? string.Empty : "test-user"),
            new(ClaimTypes.Role, role),
            new("Jellyfin-UserId", id.ToString("N", CultureInfo.InvariantCulture)),
            new("Jellyfin-DeviceId", "test-device"),
            new("Jellyfin-Device", "test"),
            new("Jellyfin-Client", "kit-tests"),
            new("Jellyfin-Version", "1.0.0"),
            new("Jellyfin-Token", "test-token"),
            new("Jellyfin-IsApiKey", isApiKey.ToString(CultureInfo.InvariantCulture))
        ];
        return new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme));
    }

    /// <summary>
    /// Authenticates test requests from <see cref="CallerHeader"/>; without the header the request is anonymous, as a
    /// request without a token is in Jellyfin.
    /// </summary>
    internal sealed class TestAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        /// <summary>The user id used when <see cref="UserIdHeader"/> is absent.</summary>
        public static readonly Guid DefaultUserId = new("5a1e0c5e-7d2a-4c38-9a8e-1f0d2b3c4d5e");

        /// <summary>
        /// Initializes a new instance of the <see cref="TestAuthenticationHandler"/> class.
        /// </summary>
        /// <param name="options">The options.</param>
        /// <param name="logger">The logger factory.</param>
        /// <param name="encoder">The URL encoder.</param>
        public TestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        /// <inheritdoc />
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var caller = Request.Headers[CallerHeader].ToString();
            if (caller.Length == 0)
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var userId = Guid.TryParse(Request.Headers[UserIdHeader].ToString(), out var id) ? id : DefaultUserId;
            var principal = caller switch
            {
                "admin" => CreatePrincipal(userId, isAdministrator: true),
                "user" => CreatePrincipal(userId, isAdministrator: false),
                "apikey" => CreatePrincipal(Guid.Empty, isAdministrator: false, isApiKey: true),
                _ => null
            };
            return Task.FromResult(principal is null
                ? AuthenticateResult.Fail("Unknown test caller.")
                : AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
