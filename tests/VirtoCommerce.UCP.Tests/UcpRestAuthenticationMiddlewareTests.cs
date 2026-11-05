using System.IO;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using VirtoCommerce.MCP.Core.Extensions;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.MCP.Core.Services;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Models;
using VirtoCommerce.UCP.Core.Services;
using VirtoCommerce.UCP.Data.Services;
using VirtoCommerce.UCP.Web;
using VirtoCommerce.UCP.Web.Mcp;
using Xunit;

namespace VirtoCommerce.UCP.Tests;

// The buyer of a UCP REST request comes from an MCP access token for the UCP resource only, as on /ucp/mcp.
[Trait("Category", "Unit")]
public class UcpRestAuthenticationMiddlewareTests
{
    private const string Origin = "https://" + StubMcpScopeRegistry.ScopeHost;
    private const string UcpResource = Origin + "/ucp/mcp";

    // A back-office cookie, an API key or Basic credentials made the platform's user: none of them is the buyer.
    [Theory]
    [InlineData("Identity.Application", null)]
    [InlineData("ApiKey", null)]
    [InlineData("Basic", "Basic dXNlcjpwYXNz")]
    public async Task InvokeAsync_PlatformUserWithoutBearerToken_IsAnonymousForUcp(string authenticationType, string authorization)
    {
        // Arrange
        var tokenValidator = new StubTokenValidator();
        var platformUser = CreateUser(authenticationType, "back-office-user");

        // Act
        var (nextCalled, context) = await Invoke(tokenValidator, authorization: authorization, user: platformUser);

        // Assert
        Assert.True(nextCalled);
        Assert.False(context.User.Identity?.IsAuthenticated);
        Assert.False(tokenValidator.AuthenticateCalled);
        Assert.False(ResolveBuyer(context).IsAuthenticated);
    }

    [Fact]
    public async Task InvokeAsync_NoBearerToken_IsAnonymous()
    {
        // Arrange
        var tokenValidator = new StubTokenValidator();

        // Act
        var (nextCalled, context) = await Invoke(tokenValidator);

        // Assert
        Assert.True(nextCalled);
        Assert.False(context.User.Identity?.IsAuthenticated);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_ValidTokenForUcpResource_MakesItsUserTheBuyer()
    {
        // Arrange
        var buyer = CreateUser("OpenIddict.Validation.AspNetCore", "buyer-1");
        var tokenValidator = new StubTokenValidator { Principal = buyer };
        var platformUser = CreateUser("Identity.Application", "back-office-user");

        // Act
        var (nextCalled, context) = await Invoke(tokenValidator, authorization: "Bearer buyer-token", user: platformUser);

        // Assert
        Assert.True(nextCalled);
        Assert.Same(buyer, context.User);
        Assert.Same(buyer, tokenValidator.CheckedPrincipal);
        Assert.Equal(UcpResource, tokenValidator.CheckedResource);
        var buyerContext = ResolveBuyer(context);
        Assert.True(buyerContext.IsAuthenticated);
        Assert.Equal("buyer-1", buyerContext.UserId);
    }

    [Fact]
    public async Task InvokeAsync_TokenOfAnotherAudience_Answers401InvalidToken()
    {
        // Arrange
        const string error = "The access token was not issued for this MCP resource.";
        var tokenValidator = new StubTokenValidator
        {
            Principal = CreateUser("OpenIddict.Validation.AspNetCore", "buyer-1"),
            Error = error,
        };

        // Act
        var (nextCalled, context) = await Invoke(tokenValidator, authorization: "Bearer storefront-token");

        // Assert
        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        var challenge = context.Response.Headers.WWWAuthenticate.ToString();
        Assert.StartsWith("Bearer ", challenge);
        Assert.Contains("error=\"invalid_token\"", challenge);
        Assert.Contains($"error_description=\"{error}\"", challenge);
        Assert.Contains($"resource_metadata=\"{Origin}/.well-known/oauth-protected-resource/ucp/mcp\"", challenge);
        var body = JObject.Parse(await ReadBody(context));
        Assert.Equal(ModuleConstants.ErrorCodes.IdentityRequired, (string)body["code"]);
        Assert.Equal(error, (string)body["message"]);
        Assert.Equal(context.TraceIdentifier, (string)body["correlation_id"]);
    }

    [Fact]
    public async Task InvokeAsync_InvalidToken_Answers401InsteadOfServingAnonymously()
    {
        // Arrange
        var tokenValidator = new StubTokenValidator { Principal = null, Error = "The access token is invalid or expired." };

        // Act
        var (nextCalled, context) = await Invoke(tokenValidator, authorization: "Bearer expired-token");

        // Assert
        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Contains("error=\"invalid_token\"", context.Response.Headers.WWWAuthenticate.ToString());
    }

    // Without a configured origin, the audience would be the Host the client sent, as on /ucp/mcp.
    [Fact]
    public async Task InvokeAsync_OriginNotConfiguredOutsideDevelopment_Answers401()
    {
        // Arrange
        var tokenValidator = new StubTokenValidator { Principal = CreateUser("OpenIddict.Validation.AspNetCore", "buyer-1") };

        // Act
        var (nextCalled, context) = await Invoke(tokenValidator, authorization: "Bearer buyer-token", originConfigured: false);

        // Assert
        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Contains("no public origin configured", context.Response.Headers.WWWAuthenticate.ToString());
        Assert.Null(tokenValidator.CheckedResource);
    }

    [Fact]
    public async Task InvokeAsync_OriginNotConfiguredInDevelopment_ChecksTheToken()
    {
        // Arrange
        var buyer = CreateUser("OpenIddict.Validation.AspNetCore", "buyer-1");
        var tokenValidator = new StubTokenValidator { Principal = buyer };

        // Act
        var (nextCalled, context) = await Invoke(tokenValidator, authorization: "Bearer buyer-token", originConfigured: false, environmentName: Environments.Development);

        // Assert
        Assert.True(nextCalled);
        Assert.Same(buyer, context.User);
        Assert.Equal(UcpResource, tokenValidator.CheckedResource);
    }

    [Theory]
    [InlineData("/ucp/v1/carts", true)]
    [InlineData("/UCP/v1/catalog/search", true)]
    [InlineData("/ucp/v1/checkouts/checkout-1/handoff", true)]
    [InlineData("/ucp/v1/internal/handoff/restore", false)]
    [InlineData("/ucp/mcp", false)]
    [InlineData("/.well-known/ucp", false)]
    [InlineData("/ucp/v1x", false)]
    public void IsRestRequest_MatchesTheRestApiExceptTheStorefrontRestore(string path, bool expected)
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Request.Path = path;

        // Act
        var result = UcpRestAuthenticationMiddleware.IsRestRequest(context);

        // Assert
        Assert.Equal(expected, result);
    }

    // The buyer the UCP services see for the request
    private static UcpBuyerContext ResolveBuyer(HttpContext context)
    {
        return new UcpBuyerContextAccessor(new HttpContextAccessor { HttpContext = context }).Resolve();
    }

    private static ClaimsPrincipal CreateUser(string authenticationType, string userId)
    {
        return new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", userId), new Claim(ClaimTypes.NameIdentifier, userId)], authenticationType));
    }

    private static async Task<(bool NextCalled, DefaultHttpContext Context)> Invoke(
        StubTokenValidator tokenValidator,
        string authorization = null,
        ClaimsPrincipal user = null,
        bool originConfigured = true,
        string environmentName = "Production")
    {
        var nextCalled = false;
        var middleware = new UcpRestAuthenticationMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        // The ucp scope's own origin resolver, as UCP declares it; MCP:Scopes:ucp:PublicOrigin makes the origin configured
        var mcpOptions = new McpOptions();
        if (originConfigured)
        {
            mcpOptions.Scopes[ModuleConstants.McpScopeName] = new McpScopeOptions { PublicOrigin = Origin };
        }

        var scopeRegistry = new StubMcpScopeRegistry();
        var services = new ServiceCollection()
            .AddSingleton(Options.Create(mcpOptions))
            .AddSingleton<IUcpPublicOriginResolver>(new StubUcpOriginResolver())
            .AddTransient<IMcpPublicOriginResolver, McpPublicOriginResolver>();
        new McpScopeBuilder(services, scopeRegistry.GetScope(ModuleConstants.McpScopeName))
            .WithPublicOriginResolver<UcpMcpPublicOriginResolver>();

        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Request.Method = HttpMethods.Get;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString(StubMcpScopeRegistry.ScopeHost);
        context.Request.Path = "/ucp/v1/carts";
        context.Response.Body = new MemoryStream();
        if (authorization != null)
        {
            context.Request.Headers.Authorization = authorization;
        }

        if (user != null)
        {
            context.User = user;
        }

        await middleware.InvokeAsync(context, scopeRegistry, tokenValidator, new StubHostEnvironment { EnvironmentName = environmentName });

        return (nextCalled, context);
    }

    private static async Task<string> ReadBody(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);

        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }

    // The MCP module's token rules: the principal of the bearer token, and the error the validator finds for the resource.
    private sealed class StubTokenValidator : IMcpTokenValidator
    {
        public ClaimsPrincipal Principal { get; init; }

        public string Error { get; init; }

        public bool AuthenticateCalled { get; private set; }

        public ClaimsPrincipal CheckedPrincipal { get; private set; }

        public string CheckedResource { get; private set; }

        public Task<ClaimsPrincipal> AuthenticateAsync(HttpContext context)
        {
            AuthenticateCalled = true;

            return Task.FromResult(Principal);
        }

        public Task<bool> IsActiveAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
        {
            return Task.FromResult(Error == null);
        }

        public Task<string> GetTokenErrorAsync(ClaimsPrincipal principal, string expectedResource, CancellationToken cancellationToken)
        {
            CheckedPrincipal = principal;
            CheckedResource = expectedResource;

            return Task.FromResult(Error);
        }
    }

    private sealed class StubUcpOriginResolver : IUcpPublicOriginResolver
    {
        public Task<string> GetOriginAsync()
        {
            return Task.FromResult(Origin);
        }
    }

    private sealed class StubHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; }

        public string ApplicationName { get; set; } = "VirtoCommerce.UCP.Tests";

        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
