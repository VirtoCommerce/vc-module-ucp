using System;
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
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Models;
using VirtoCommerce.UCP.Core.Services;
using VirtoCommerce.UCP.Web.Mcp;
using Xunit;

namespace VirtoCommerce.UCP.Tests;

// The sign-in rule of a store whose catalog is closed to anonymous buyers, in front of the MCP module's ucp scope.
[Trait("Category", "Unit")]
public class UcpMcpSignInRequiredMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_ChallengesAnonymousInitializeWhenCatalogIsClosed()
    {
        var (nextCalled, context) = await Invoke("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""", anonymousCatalog: false);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Contains("resource_metadata=\"https://store.example/.well-known/oauth-protected-resource/ucp/mcp\"", context.Response.Headers.WWWAuthenticate.ToString());
    }

    [Fact]
    public async Task InvokeAsync_AllowsAnonymousInitializeWhenCatalogIsOpen()
    {
        var (nextCalled, context) = await Invoke("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""", anonymousCatalog: true);

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

    [Fact]
    public async Task InvokeAsync_KeepsIdentityLinkingMessageWhenCatalogIsClosed()
    {
        var (_, context) = await Invoke(ToolCall("link_buyer_identity"), anonymousCatalog: false);

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Contains("link the buyer account", await ReadBody(context));
    }

    [Fact]
    public async Task InvokeAsync_KeepsTheBodyReadableForTheMcpServer()
    {
        var json = ToolCall("search_products");
        string bodySeenByNext = null;

        await Invoke(json, anonymousCatalog: true, next: async context =>
        {
            using var reader = new StreamReader(context.Request.Body, leaveOpen: true);
            bodySeenByNext = await reader.ReadToEndAsync();
        });

        Assert.Equal(json, bodySeenByNext);
    }

    [Fact]
    public async Task InvokeAsync_ChallengesAtTheMcpEndpointOfAScopeHost()
    {
        var (_, context) = await Invoke("""{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}""", anonymousCatalog: false, path: "/mcp");

        Assert.Contains("resource_metadata=\"https://store.example/.well-known/oauth-protected-resource/mcp\"", context.Response.Headers.WWWAuthenticate.ToString());
    }

    private static string ToolCall(string toolName)
    {
        return """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"TOOL_NAME","arguments":{"query":"Epson"}}}"""
            .Replace("TOOL_NAME", toolName, StringComparison.Ordinal);
    }

    private static async Task<(bool NextCalled, DefaultHttpContext Context)> Invoke(
        string json,
        bool anonymousCatalog,
        string authorization = null,
        string path = "/ucp/mcp",
        RequestDelegate next = null)
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
            .AddTransient<IMcpPublicOriginResolver, McpPublicOriginResolver>();
        new McpScopeBuilder(services, scope)
            .WithChallengeWriter<UcpMcpChallengeWriter>()
            .WithPublicOriginResolver<UcpMcpPublicOriginResolver>();

        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.SetMcpScope(scope);
        context.SetMcpEndpointPath(path == McpScope.HostEndpointPath ? McpScope.HostEndpointPath : scope.Path);
        context.Request.Method = HttpMethods.Post;
        context.Request.Scheme = "https";
        context.Request.Host = new HostString(StubMcpScopeRegistry.ScopeHost);
        context.Request.Path = path;
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        context.Response.Body = new MemoryStream();
        if (authorization != null)
        {
            context.Request.Headers.Authorization = authorization;
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
