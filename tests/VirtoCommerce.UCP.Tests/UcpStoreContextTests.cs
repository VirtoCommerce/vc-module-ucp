using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using VirtoCommerce.Platform.Core.Settings;
using VirtoCommerce.StoreModule.Core.Model;
using VirtoCommerce.StoreModule.Core.Model.Search;
using VirtoCommerce.UCP.Core.Options;
using VirtoCommerce.UCP.Web.Services;
using VirtoCommerce.Xapi.Core.Models;
using Xunit;
using StoreSettings = VirtoCommerce.UCP.Core.ModuleConstants.Settings.Store;

namespace VirtoCommerce.UCP.Tests;

// One platform, several stores: the store of a UCP request comes from its host, and gives the UCP services their store,
// currency, language, URL and store settings.
[Trait("Category", "Unit")]
public class UcpStoreContextTests
{
    private static readonly Store _electronics = new() { Id = "electronics", SecureUrl = "https://electronics.example/", DefaultCurrency = "USD", DefaultLanguage = "en-US", StoreState = StoreState.Open };
    private static readonly Store _fashion = new() { Id = "fashion", SecureUrl = "https://fashion.example", DefaultCurrency = "EUR", DefaultLanguage = "de-DE", StoreState = StoreState.Open };

    [Fact]
    public async Task ResolveStoreAsync_UsesTheStoreOfTheRequestHost()
    {
        var resolver = CreateResolver(defaultStore: "electronics");

        var store = await resolver.ResolveStoreAsync(Request("fashion.example"));

        Assert.Equal("fashion", store.Id);
    }

    [Fact]
    public async Task ResolveStoreAsync_UsesTheDomainMappingOfThePlatform()
    {
        var resolver = CreateResolver(domains: new() { ["shop.example"] = "fashion" });

        var store = await resolver.ResolveStoreAsync(Request("shop.example"));

        Assert.Equal("fashion", store.Id);
    }

    [Theory]
    [InlineData("Origin", "https://fashion.example")]
    [InlineData("Referer", "https://fashion.example/checkout?step=2")]
    public async Task ResolveStoreAsync_UsesTheOriginOrRefererOfACallFromAStorefront(string header, string value)
    {
        var resolver = CreateResolver(defaultStore: "electronics");
        var context = Request("api.example");
        context.Request.Headers[header] = value;

        var store = await resolver.ResolveStoreAsync(context);

        Assert.Equal("fashion", store.Id);
    }

    [Theory]
    [InlineData("electronics", null, "electronics")]
    [InlineData(null, "fashion", "fashion")]
    [InlineData("electronics", "fashion", "electronics")]
    public async Task ResolveStoreAsync_FallsBackToThePlatformDefaultStore_ThenTheObsoleteUcpKey(string platformDefault, string ucpDefault, string expected)
    {
        var resolver = CreateResolver(defaultStore: platformDefault, ucpDefaultStoreId: ucpDefault);

        var store = await resolver.ResolveStoreAsync(Request("api.example"));

        Assert.Equal(expected, store.Id);
    }

    [Fact]
    public async Task ResolveStoreAsync_UsesTheOnlyOpenStore()
    {
        var resolver = CreateResolver(stores: [_fashion]);

        var store = await resolver.ResolveStoreAsync(Request("api.example"));

        Assert.Equal("fashion", store.Id);
    }

    [Fact]
    public async Task ResolveStoreAsync_ReturnsNoStore_When_SeveralStoresAreOpenAndNoneMatches()
    {
        var store = await CreateResolver().ResolveStoreAsync(Request("api.example"));

        Assert.Null(store);
    }

    [Fact]
    public void Build_TakesTheStoreValuesOverTheConfiguration()
    {
        var configured = new UcpOptions { DefaultStoreId = "other", DefaultCurrency = "GBP", DefaultCultureName = "en-GB", StorefrontOrigin = "https://configured.example", UcpBaseUrl = "https://configured.example/ucp/v1", AnonymousCatalog = false };

        var options = UcpRequestOptions.Build(configured, _fashion);

        Assert.Equal("fashion", options.DefaultStoreId);
        Assert.Equal("EUR", options.DefaultCurrency);
        Assert.Equal("de-DE", options.DefaultCultureName);
        Assert.Equal("https://fashion.example", options.StorefrontOrigin);
        Assert.Equal("https://fashion.example/ucp/v1", options.UcpBaseUrl);
        Assert.False(options.AnonymousCatalog);
    }

    [Fact]
    public void Build_TakesTheUcpSettingsSetOnTheStore()
    {
        var store = new Store
        {
            Id = "fashion",
            Settings =
            [
                new ObjectSettingEntry(StoreSettings.StoreManagedAddresses) { Value = true },
                new ObjectSettingEntry(StoreSettings.CheckoutLinkLifetimeMinutes) { Value = 30 },
                new ObjectSettingEntry(StoreSettings.CheckoutUrlTemplate) { Value = "https://fashion.example/buy?s={token}" },
            ],
        };

        var options = UcpRequestOptions.Build(new UcpOptions(), store);

        Assert.True(options.StoreManagedAddresses);
        Assert.Equal(30, options.HandoffTokenTtlMinutes);
        Assert.Equal("https://fashion.example/buy?s={token}", options.HandoffUrlTemplate);
    }

    [Fact]
    public void Build_KeepsTheConfiguredValue_When_TheStoreLeavesASettingUnset()
    {
        var store = new Store { Id = "fashion", Settings = [new ObjectSettingEntry(StoreSettings.CheckoutLinkLifetimeMinutes)] };
        var configured = new UcpOptions { HandoffTokenTtlMinutes = 20, StoreManagedAddresses = true, HandoffUrlTemplate = "https://x.example/c?s={token}" };

        var options = UcpRequestOptions.Build(configured, store);

        Assert.Equal(20, options.HandoffTokenTtlMinutes);
        Assert.True(options.StoreManagedAddresses);
        Assert.Equal("https://x.example/c?s={token}", options.HandoffUrlTemplate);
    }

    [Fact]
    public void Current_IsTheConfiguration_When_NoStoreWasResolved()
    {
        var configured = new UcpOptions { DefaultStoreId = "configured" };
        var requestOptions = new UcpRequestOptions(new HttpContextAccessor { HttpContext = new DefaultHttpContext() }, Options.Create(configured));

        Assert.Same(configured, requestOptions.Current);
    }

    [Fact]
    public void Current_IsTheStoresOptions_When_TheMiddlewareResolvedAStore()
    {
        var context = new DefaultHttpContext();
        UcpRequestOptions.SetStore(context, _fashion, new UcpOptions());
        var requestOptions = new UcpRequestOptions(new HttpContextAccessor { HttpContext = context }, Options.Create(new UcpOptions()));

        Assert.Equal("fashion", requestOptions.Current.DefaultStoreId);
        Assert.Same(_fashion, UcpRequestOptions.GetStore(context));
    }

    private static DefaultHttpContext Request(string host)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString(host);
        return context;
    }

    private static UcpStoreContextResolver CreateResolver(
        string defaultStore = null,
        string ucpDefaultStoreId = null,
        Dictionary<string, string> domains = null,
        IList<Store> stores = null)
    {
        return new InMemoryStoreContextResolver(
            stores ?? [_electronics, _fashion],
            new StoresOptions { DefaultStore = defaultStore, Domains = domains ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) },
            new UcpOptions { DefaultStoreId = ucpDefaultStoreId });
    }

    private sealed class InMemoryStoreContextResolver(IList<Store> stores, StoresOptions storesOptions, UcpOptions ucpOptions)
        : UcpStoreContextResolver(null, null, Options.Create(storesOptions), Options.Create(ucpOptions))
    {
        protected override Task<Store> GetStoreByIdAsync(string storeId)
        {
            return Task.FromResult(stores.FirstOrDefault(x => x.Id == storeId));
        }

        protected override Task<StoreSearchResult> SearchStoresAsync(StoreSearchCriteria criteria)
        {
            var found = stores
                .Where(x => criteria.Domain == null || new Uri(x.SecureUrl ?? x.Url).Host == criteria.Domain)
                .Where(x => criteria.StoreStates == null || criteria.StoreStates.Contains(x.StoreState))
                .Take(criteria.Take)
                .ToList();

            return Task.FromResult(new StoreSearchResult { Results = found, TotalCount = found.Count });
        }
    }
}
