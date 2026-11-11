using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VirtoCommerce.MCP.Core.Extensions;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.MCP.Core.Services;
using VirtoCommerce.StoreModule.Core.Model;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Models;
using VirtoCommerce.UCP.Core.Options;
using VirtoCommerce.UCP.Core.Services;
using VirtoCommerce.UCP.Web.Mcp;
using VirtoCommerce.UCP.Web.Services;
using Xunit;

namespace VirtoCommerce.UCP.Tests;

// The sign-in rule of a store whose catalog is closed to anonymous buyers, in front of the MCP module's ucp scope.
[Trait("Category", "Unit")]
public class UcpMcpSignInRequiredMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_ChallengesAnonymousInitializeWhenCatalogIsClosed()
    {
        var (nextCalled, context) = await Invoke(Initialize(), anonymousCatalog: false);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Contains("resource_metadata=\"https://store.example/.well-known/oauth-protected-resource/ucp/mcp\"", context.Response.Headers.WWWAuthenticate.ToString());
    }

    [Fact]
    public async Task InvokeAsync_AllowsAnonymousInitializeWhenCatalogIsOpen()
    {
        var (nextCalled, context) = await Invoke(Initialize(), anonymousCatalog: true);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_LeavesARequestWithABearerTokenToTheMcpModule()
    {
        var (nextCalled, _) = await Invoke(ToolCall("search_products"), anonymousCatalog: false, authorization: "Bearer platform-token");

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_ChallengesARequestWhoseAuthorizationIsNotABearerToken()
    {
        var (nextCalled, context) = await Invoke(ToolCall("search_products"), anonymousCatalog: false, authorization: "Basic dXNlcjpwYXNz");

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_DoesNotChallengeLogoutWhenCatalogIsClosed()
    {
        var (nextCalled, context) = await Invoke(ToolCall("logout_buyer"), anonymousCatalog: false);

        Assert.True(nextCalled);
        Assert.Equal(0, context.Response.Headers.WWWAuthenticate.Count);
    }

    // The MCP module reads the request ignoring case and keeps every tool name it carries: a request that names its tool
    // more than once is challenged unless every name is logout_buyer, as the server may bind either.
    [Theory]
    [InlineData("tools/call", new[] { "logout_buyer", "search_products" }, false)]
    [InlineData("tools/call", new[] { "search_products", "logout_buyer" }, false)]
    [InlineData("tools/call", new[] { "logout_buyer", "logout_buyer" }, true)]
    [InlineData("tools/call", new[] { "Logout_Buyer" }, false)]
    [InlineData("tools/call", new string[0], false)]
    [InlineData("initialize", new[] { "logout_buyer" }, false)]
    public async Task InvokeAsync_ChallengesAnAmbiguousToolNameUnlessEveryNameIsLogout(string method, string[] toolNames, bool expectedNextCalled)
    {
        // Arrange
        var request = new McpRequestInfo(method, toolNames);

        // Act
        var (nextCalled, context) = await Invoke(request, anonymousCatalog: false);

        // Assert
        Assert.Equal(expectedNextCalled, nextCalled);
        Assert.Equal(expectedNextCalled ? StatusCodes.Status200OK : StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    // A request the MCP module did not read is challenged as an unknown call: logout_buyer in its body does not let it through.
    [Fact]
    public async Task InvokeAsync_ChallengesARequestTheMcpModuleDidNotRead()
    {
        // Act
        var (nextCalled, context) = await Invoke(null, anonymousCatalog: false);

        // Assert
        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_KeepsIdentityLinkingMessageWhenCatalogIsClosed()
    {
        var (_, context) = await Invoke(ToolCall("link_buyer_identity"), anonymousCatalog: false);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Contains("link the buyer account", await ReadBody(context));
    }

    // The middleware decides on the request the MCP module read, not on the body, which stays untouched for the MCP server.
    [Fact]
    public async Task InvokeAsync_DoesNotReadTheBody()
    {
        // Arrange
        string bodySeenByNext = null;

        // Act
        await Invoke(ToolCall("search_products"), anonymousCatalog: true, next: async context =>
        {
            using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
            bodySeenByNext = await reader.ReadToEndAsync();
        });

        // Assert
        Assert.Equal(RequestBody, bodySeenByNext);
    }

    [Fact]
    public async Task InvokeAsync_ChallengesAtTheMcpEndpointOfAScopeHost()
    {
        var (_, context) = await Invoke(new McpRequestInfo("tools/list", []), anonymousCatalog: false, path: "/mcp");

        Assert.Contains("resource_metadata=\"https://store.example/.well-known/oauth-protected-resource/mcp\"", context.Response.Headers.WWWAuthenticate.ToString());
    }

    // A browser page of an origin the scope does not allow is left to the MCP module's Origin check, which answers 403;
    // the store's own origin is still asked to sign in.
    [Theory]
    [InlineData("https://evil.example", true)]
    [InlineData("null", true)]
    [InlineData("https://" + StubMcpScopeRegistry.ScopeHost, false)]
    [InlineData(null, false)]
    public async Task InvokeAsync_LeavesARequestFromAnOriginTheScopeDoesNotAllowToTheMcpModule(string origin, bool expectedNextCalled)
    {
        // Act
        var (nextCalled, context) = await Invoke(ToolCall("search_products"), anonymousCatalog: false, origin: origin);

        // Assert
        Assert.Equal(expectedNextCalled, nextCalled);
        Assert.Equal(expectedNextCalled ? StatusCodes.Status200OK : StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    // The body of every request, which names logout_buyer: the middleware must decide on the request info alone.
    private const string RequestBody = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"logout_buyer"}}""";

    private static McpRequestInfo Initialize()
    {
        return new McpRequestInfo("initialize", []);
    }

    private static McpRequestInfo ToolCall(string toolName)
    {
        return new McpRequestInfo("tools/call", [toolName]);
    }

    private static async Task<(bool NextCalled, DefaultHttpContext Context)> Invoke(
        McpRequestInfo request,
        bool anonymousCatalog,
        string authorization = null,
        string path = "/ucp/mcp",
        RequestDelegate next = null,
        string origin = null)
    {
        var nextCalled = false;
        var middleware = new UcpMcpSignInRequiredMiddleware(async context =>
        {
            nextCalled = true;
            if (next != null)
            {
                await next(context);
            }
        });
        // The MCP module's services with the ucp scope's own challenge writer and origin, as UCP declares them
        var scope = new McpScope { Name = ModuleConstants.McpScopeName };
        var services = new ServiceCollection()
            .AddSingleton(Options.Create(new McpOptions()))
            .AddSingleton<IUcpPublicOriginResolver>(new StubUcpOriginResolver())
            .AddTransient<McpChallengeWriter>()
            .AddTransient<IMcpPublicOriginResolver, McpPublicOriginResolver>()
            .AddTransient<IMcpOriginValidator, McpOriginValidator>();
        new McpScopeBuilder(services, scope)
            .WithChallengeWriter<UcpMcpChallengeWriter>()
            .WithPublicOriginResolver<UcpMcpPublicOriginResolver>();

        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.SetMcpScope(scope);
        context.SetMcpEndpointPath(path == McpScope.HostEndpointPath ? McpScope.HostEndpointPath : scope.Path);
        if (request != null)
        {
            // The request as the MCP module reads it after routing (UseMcpScopeRequests), ahead of UCP's steps
            context.SetMcpRequest(request);
        }

        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString(StubMcpScopeRegistry.ScopeHost);
        context.Request.Path = path;
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(RequestBody));
        context.Response.Body = new MemoryStream();
        if (authorization != null)
        {
            context.Request.Headers.Authorization = authorization;
        }

        if (origin != null)
        {
            // The store UcpStoreContextMiddleware resolved for the request, whose URL is the scope's origin
            UcpRequestOptions.SetStore(context, new Store { Id = "store", Url = "https://" + StubMcpScopeRegistry.ScopeHost }, new UcpOptions());
            context.Request.Headers.Origin = origin;
        }

        await middleware.InvokeAsync(context, new StubProfileService(anonymousCatalog));

        return (nextCalled, context);
    }

    private static async Task<string> ReadBody(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);

        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }

    private sealed class StubProfileService(bool anonymousCatalog) : IUcpProfileService
    {
        public Task<UcpProfile> GetProfile(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new UcpProfile { Auth = new UcpProfileAuth { AnonymousCatalog = anonymousCatalog } });
        }
    }

    private sealed class StubUcpOriginResolver : IUcpPublicOriginResolver
    {
        public Task<string> GetOriginAsync()
        {
            return Task.FromResult("https://" + StubMcpScopeRegistry.ScopeHost);
        }
    }
}
