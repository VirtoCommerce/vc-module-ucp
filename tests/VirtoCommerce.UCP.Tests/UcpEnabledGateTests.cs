using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using VirtoCommerce.MCP.Core.Services;
using VirtoCommerce.UCP.Web;
using Xunit;

namespace VirtoCommerce.UCP.Tests;

// UCP is switched off with the MCP module's scope configuration, MCP:Scopes:ucp:Enabled = false: the MCP module then
// serves no /ucp/mcp, and the gate answers 404 on the other UCP surfaces.
[Trait("Category", "Unit")]
public class UcpEnabledGateTests
{
    [Fact]
    public async Task InvokeAsync_ScopeOff_Answers404WithoutCallingNext()
    {
        var (nextCalled, context) = await Invoke(new StubMcpScopeRegistry(ucpEnabled: false));

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_ScopeOn_CallsNext()
    {
        var (nextCalled, context) = await Invoke(new StubMcpScopeRegistry(ucpEnabled: true));

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/ucp")]
    [InlineData("/ucp/v1/carts")]
    [InlineData("/UCP/v1/carts")]
    [InlineData("/ucp/mcp")]
    [InlineData("/.well-known/ucp")]
    [InlineData("/.well-known/UCP")]
    [InlineData("/.well-known/oauth-protected-resource/ucp/mcp")]
    public void IsGatedPath_UcpPrefix_ReturnsTrue(string path)
    {
        Assert.True(UcpEnabledGateExtensions.IsGatedPath(path));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/ucpx")]
    [InlineData("/ucpx/v1")]
    [InlineData("/api/platform/modules")]
    [InlineData("/graphql")]
    [InlineData("/graphql/other")]
    [InlineData("/.well-known/openid-configuration")]
    [InlineData("/.well-known/ucpx")]
    [InlineData("/ui/graphiql")]
    public void IsGatedPath_NonUcpPath_ReturnsFalse(string path)
    {
        Assert.False(UcpEnabledGateExtensions.IsGatedPath(path));
    }

    [Fact]
    public async Task UseUcpEnabledGate_ScopeOff_MixedCasePrefixAnswers404()
    {
        var (pipeline, terminal) = BuildPipeline(new StubMcpScopeRegistry(ucpEnabled: false));

        var context = await Send(pipeline, "GET", "/UCP/v1/carts");

        Assert.False(terminal.Reached);
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/ucpx")]
    [InlineData("GET", "/api/platform/modules")]
    [InlineData("POST", "/graphql")]
    public async Task UseUcpEnabledGate_NonUcpPath_PassesThrough(string method, string path)
    {
        var (pipeline, terminal) = BuildPipeline(new StubMcpScopeRegistry(ucpEnabled: false));

        await Send(pipeline, method, path);

        Assert.True(terminal.Reached);
    }

    [Theory]
    [InlineData("GET", "/ucp/v1/catalog/search")]
    [InlineData("POST", "/ucp/v1/carts")]
    [InlineData("GET", "/ucp/mcp")]
    [InlineData("DELETE", "/ucp/mcp")]
    [InlineData("POST", "/ucp/mcp")]
    [InlineData("GET", "/.well-known/ucp")]
    [InlineData("GET", "/.well-known/oauth-protected-resource/ucp/mcp")]
    public async Task UseUcpEnabledGate_ScopeOff_UcpSurfaceAnswers404(string method, string path)
    {
        var (pipeline, terminal) = BuildPipeline(new StubMcpScopeRegistry(ucpEnabled: false));

        var context = await Send(pipeline, method, path, malformedJsonBody: method == "POST");

        Assert.False(terminal.Reached);
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/ucp/v1/catalog/search")]
    [InlineData("POST", "/ucp/v1/carts")]
    [InlineData("GET", "/ucp/mcp")]
    [InlineData("DELETE", "/ucp/mcp")]
    [InlineData("POST", "/ucp/mcp")]
    [InlineData("GET", "/.well-known/ucp")]
    [InlineData("GET", "/.well-known/oauth-protected-resource/ucp/mcp")]
    public async Task UseUcpEnabledGate_ScopeOn_UcpSurfaceReachesTerminal(string method, string path)
    {
        var (pipeline, terminal) = BuildPipeline(new StubMcpScopeRegistry(ucpEnabled: true));

        var context = await Send(pipeline, method, path, malformedJsonBody: method == "POST");

        Assert.True(terminal.Reached);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    private static async Task<(bool NextCalled, DefaultHttpContext Context)> Invoke(IMcpScopeRegistry scopeRegistry)
    {
        var nextCalled = false;
        var middleware = new UcpEnabledMiddleware(_ =>
        {
            nextCalled = true;

            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context, scopeRegistry);

        return (nextCalled, context);
    }

    private static (RequestDelegate Pipeline, Terminal Terminal) BuildPipeline(IMcpScopeRegistry scopeRegistry)
    {
        var services = new ServiceCollection()
            .AddSingleton(scopeRegistry)
            .BuildServiceProvider();
        var terminal = new Terminal();
        var app = new ApplicationBuilder(services);
        app.UseUcpEnabledGate();
        app.Run(terminal.Handle);

        return (app.Build(), terminal);
    }

    private static async Task<DefaultHttpContext> Send(RequestDelegate pipeline, string method, string path, bool malformedJsonBody = false)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        if (malformedJsonBody)
        {
            context.Request.ContentType = "application/json";
            context.Request.Body = new MemoryStream("{ not json"u8.ToArray());
        }

        await pipeline(context);

        return context;
    }

    private sealed class Terminal
    {
        public bool Reached { get; private set; }

        public Task Handle(HttpContext context)
        {
            Reached = true;

            return Task.CompletedTask;
        }
    }
}
