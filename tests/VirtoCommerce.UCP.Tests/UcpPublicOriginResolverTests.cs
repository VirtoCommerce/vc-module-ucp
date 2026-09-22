using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using VirtoCommerce.StoreModule.Core.Model;
using VirtoCommerce.UCP.Core.Options;
using VirtoCommerce.UCP.Web.Services;
using VirtoCommerce.Xapi.Core.Models;
using VirtoCommerce.Xapi.Core.Services;
using Xunit;

namespace VirtoCommerce.UCP.Tests;

[Trait("Category", "Unit")]
public class UcpPublicOriginResolverTests
{
    [Fact]
    public async Task GetOriginAsync_PublicOriginOverridesStoreWithoutLookup()
    {
        var stores = new TestStoreDomainResolver { Store = new Store { SecureUrl = "https://store.example" } };
        var resolver = new UcpPublicOriginResolver(
            Options.Create(new UcpOptions { PublicOrigin = "https://public.example/" }),
            new HttpContextAccessor(), stores);

        Assert.Equal("https://public.example", await resolver.GetOriginAsync());
        Assert.Null(stores.Request);
    }

    [Theory]
    [InlineData(null, "https://shop.example/", "http://shop.example/", "https://shop.example")]
    [InlineData("B2B-store", null, "https://shop.example/", "https://shop.example")]
    [InlineData(null, " ", "https://shop.example/", "https://shop.example")]
    [InlineData(null, null, null, "https://backend.example:8443/platform")]
    public async Task GetOriginAsync_ResolvesStoreBeforeRequestOrigin(string storeId, string secureUrl, string url, string expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("backend.example", 8443);
        context.Request.PathBase = "/platform";
        var stores = new TestStoreDomainResolver { Store = new Store { SecureUrl = secureUrl, Url = url } };
        var resolver = new UcpPublicOriginResolver(
            Options.Create(new UcpOptions { DefaultStoreId = storeId }),
            new HttpContextAccessor { HttpContext = context }, stores);

        Assert.Equal(expected, await resolver.GetOriginAsync());
        Assert.Equal(storeId, stores.Request.StoreId);
        Assert.Equal("backend.example", stores.Request.Domain);
    }

    [Fact]
    public async Task GetOriginAsync_NoStoreOrRequestReturnsNull()
    {
        Assert.Null(await CreateResolver(new HttpContextAccessor()).GetOriginAsync());
    }

    internal static UcpPublicOriginResolver CreateResolver(IHttpContextAccessor accessor, UcpOptions options = null, Store store = null)
    {
        return new UcpPublicOriginResolver(Options.Create(options ?? new UcpOptions()), accessor,
            new TestStoreDomainResolver { Store = store });
    }

    private sealed class TestStoreDomainResolver : IStoreDomainResolverService
    {
        public Store Store { get; init; }
        public StoreDomainRequest Request { get; private set; }

        public Task<Store> GetStoreAsync(StoreDomainRequest request)
        {
            Request = request;
            return Task.FromResult(Store);
        }
    }
}
