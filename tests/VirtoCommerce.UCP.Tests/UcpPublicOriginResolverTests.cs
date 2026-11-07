using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using VirtoCommerce.MCP.Core.Extensions;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.StoreModule.Core.Model;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Options;
using VirtoCommerce.UCP.Web.Mcp;
using VirtoCommerce.UCP.Web.Services;
using VirtoCommerce.Xapi.Core.Models;
using Xunit;

namespace VirtoCommerce.UCP.Tests;

[Trait("Category", "Unit")]
public class UcpPublicOriginResolverTests
{
    [Fact]
    public async Task GetOriginAsync_McpScopeOriginOverridesStoreWithoutLookup()
    {
        var stores = new TestStoreContextResolver { Store = new Store { SecureUrl = "https://store.example" } };
        var mcpOptions = new McpOptions();
        mcpOptions.Scopes["ucp"] = new McpScopeOptions { PublicOrigin = "https://agents.example/" };
        var resolver = new UcpPublicOriginResolver(
            Options.Create(new UcpOptions { PublicOrigin = "https://public.example/" }),
            new HttpContextAccessor(), stores, Options.Create(mcpOptions));

        Assert.Equal("https://agents.example", await resolver.GetOriginAsync());
        Assert.False(stores.Called);
    }

    [Fact]
    public async Task GetOriginAsync_ObsoletePublicOriginStillOverridesStore()
    {
        var stores = new TestStoreContextResolver { Store = new Store { SecureUrl = "https://store.example" } };
        var resolver = new UcpPublicOriginResolver(
            Options.Create(new UcpOptions { PublicOrigin = "https://public.example/" }),
            new HttpContextAccessor(), stores);

        Assert.Equal("https://public.example", await resolver.GetOriginAsync());
        Assert.False(stores.Called);
    }

    [Theory]
    [InlineData("https://shop.example/", "http://shop.example/", "https://shop.example")]
    [InlineData(null, "https://shop.example/", "https://shop.example")]
    [InlineData(" ", "https://shop.example/", "https://shop.example")]
    [InlineData(null, null, "https://backend.example:8443/platform")]
    public async Task GetOriginAsync_UsesTheRequestsStoreBeforeRequestOrigin(string secureUrl, string url, string expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("backend.example", 8443);
        context.Request.PathBase = "/platform";
        var stores = new TestStoreContextResolver { Store = new Store { SecureUrl = secureUrl, Url = url } };
        var resolver = new UcpPublicOriginResolver(Options.Create(new UcpOptions()), new HttpContextAccessor { HttpContext = context }, stores);

        Assert.Equal(expected, await resolver.GetOriginAsync());
    }

    [Fact]
    public async Task GetOriginAsync_UsesTheStoreTheMiddlewareResolved()
    {
        var context = new DefaultHttpContext();
        var stores = new TestStoreContextResolver { Store = new Store { SecureUrl = "https://other.example" } };
        UcpRequestOptions.SetStore(context, new Store { Id = "a", SecureUrl = "https://a.example/" }, new UcpOptions());
        var resolver = new UcpPublicOriginResolver(Options.Create(new UcpOptions()), new HttpContextAccessor { HttpContext = context }, stores);

        Assert.Equal("https://a.example", await resolver.GetOriginAsync());
        Assert.False(stores.Called);
    }

    [Fact]
    public async Task GetOriginAsync_NoStoreOrRequestReturnsNull()
    {
        Assert.Null(await CreateResolver(new HttpContextAccessor()).GetOriginAsync());
    }

    // Without MCP:Scopes:ucp:PublicOrigin, the MCP module accepts a bearer token outside Development only when the
    // origin is configured: the store's URL is, the request's own origin is not.
    [Theory]
    [InlineData("https://shop.example/", null, true)]
    [InlineData(null, "http://shop.example/", true)]
    [InlineData(null, null, false)]
    [InlineData(" ", " ", false)]
    public void IsOriginConfigured_TheRequestsStoreUrlIsAConfiguredOrigin(string secureUrl, string url, bool expected)
    {
        // Arrange
        var context = CreateMcpContext(_ucpScope.Path);
        UcpRequestOptions.SetStore(context, new Store { Id = "a", SecureUrl = secureUrl, Url = url }, new UcpOptions());
        var resolver = CreateMcpResolver(new McpOptions());

        // Act
        var actual = resolver.IsOriginConfigured(context, _ucpScope);

        // Assert
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void IsOriginConfigured_NoStoreFallsBackToTheRequestOrigin_NotConfigured()
    {
        // Arrange
        var context = CreateMcpContext(_ucpScope.Path);
        var resolver = CreateMcpResolver(new McpOptions { PublicOrigin = "https://admin.example" });

        // Act
        var actual = resolver.IsOriginConfigured(context, _ucpScope);

        // Assert
        Assert.False(actual);
    }

    [Fact]
    public void IsOriginConfigured_ScopeOriginOrObsoleteUcpOriginIsConfigured()
    {
        // Arrange
        var context = CreateMcpContext(_ucpScope.Path);
        var mcpOptions = new McpOptions();
        mcpOptions.Scopes[ModuleConstants.McpScopeName] = new McpScopeOptions { PublicOrigin = "https://agents.example" };

        // Act
        var scopeOrigin = CreateMcpResolver(mcpOptions).IsOriginConfigured(context, _ucpScope);
        var obsoleteOrigin = CreateMcpResolver(new McpOptions(), new UcpOptions { PublicOrigin = "https://public.example" })
            .IsOriginConfigured(context, _ucpScope);

        // Assert
        Assert.True(scopeOrigin);
        Assert.True(obsoleteOrigin);
    }

    [Fact]
    public void IsOriginConfigured_TheMcpEndpointOfAScopeHostIsConfigured()
    {
        // Arrange
        var context = CreateMcpContext(McpScope.HostEndpointPath);
        var resolver = CreateMcpResolver(new McpOptions());

        // Act
        var actual = resolver.IsOriginConfigured(context, _ucpScope);

        // Assert
        Assert.True(actual);
    }

    [Theory]
    [InlineData("https://agents.example", "https://backend.example")]
    [InlineData("https://backend.example", "https://backend.example")]
    public async Task GetOriginAsync_TheMcpEndpointOfAScopeHostIsThatHost(string scopeOrigin, string expected)
    {
        // Arrange: /mcp on a host of the scope, with the scope's origin configured for another host or for that one
        var context = CreateMcpContext(McpScope.HostEndpointPath);
        var mcpOptions = new McpOptions();
        mcpOptions.Scopes[ModuleConstants.McpScopeName] = new McpScopeOptions { PublicOrigin = scopeOrigin };

        // Act
        var origin = await CreateMcpResolver(mcpOptions).GetOriginAsync(context, _ucpScope);

        // Assert
        Assert.Equal(expected, origin);
    }

    [Fact]
    public async Task GetOriginAsync_TheScopePathKeepsTheConfiguredOrigin()
    {
        // Arrange
        var context = CreateMcpContext(_ucpScope.Path);
        var mcpOptions = new McpOptions();
        mcpOptions.Scopes[ModuleConstants.McpScopeName] = new McpScopeOptions { PublicOrigin = "https://agents.example" };

        // Act
        var origin = await CreateMcpResolver(mcpOptions).GetOriginAsync(context, _ucpScope);

        // Assert
        Assert.Equal("https://agents.example", origin);
    }

    private readonly McpScope _ucpScope = new() { Name = ModuleConstants.McpScopeName };

    private DefaultHttpContext CreateMcpContext(string endpointPath)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("backend.example");
        context.SetMcpScope(_ucpScope);
        context.SetMcpEndpointPath(endpointPath);

        return context;
    }

    private static UcpMcpPublicOriginResolver CreateMcpResolver(McpOptions mcpOptions, UcpOptions ucpOptions = null)
    {
        ucpOptions ??= new UcpOptions();

        return new UcpMcpPublicOriginResolver(
            Options.Create(mcpOptions),
            new UcpPublicOriginResolver(Options.Create(ucpOptions), new HttpContextAccessor(), new TestStoreContextResolver(), Options.Create(mcpOptions)),
            Options.Create(ucpOptions));
    }

    internal static UcpPublicOriginResolver CreateResolver(IHttpContextAccessor accessor, UcpOptions options = null, Store store = null)
    {
        return new UcpPublicOriginResolver(Options.Create(options ?? new UcpOptions()), accessor, new TestStoreContextResolver { Store = store });
    }

    internal sealed class TestStoreContextResolver()
        : UcpStoreContextResolver(null, null, null, Options.Create(new StoresOptions { Domains = new Dictionary<string, string>() }), Options.Create(new UcpOptions()))
    {
        public Store Store { get; init; }
        public bool Called { get; private set; }

        public override Task<Store> ResolveStoreAsync(HttpContext context)
        {
            Called = true;
            return Task.FromResult(Store);
        }
    }
}
