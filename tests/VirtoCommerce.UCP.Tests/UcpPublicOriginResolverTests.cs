using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.StoreModule.Core.Model;
using VirtoCommerce.UCP.Core.Options;
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

    internal static UcpPublicOriginResolver CreateResolver(IHttpContextAccessor accessor, UcpOptions options = null, Store store = null)
    {
        return new UcpPublicOriginResolver(Options.Create(options ?? new UcpOptions()), accessor, new TestStoreContextResolver { Store = store });
    }

    internal sealed class TestStoreContextResolver()
        : UcpStoreContextResolver(null, null, Options.Create(new StoresOptions { Domains = new Dictionary<string, string>() }), Options.Create(new UcpOptions()))
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
