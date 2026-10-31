using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.StoreModule.Core.Model;
using VirtoCommerce.StoreModule.Core.Model.Search;
using VirtoCommerce.StoreModule.Core.Services;
using VirtoCommerce.UCP.Core.Diagnostics;
using VirtoCommerce.UCP.Core.Options;
using VirtoCommerce.Xapi.Core.Models;

namespace VirtoCommerce.UCP.Web.Services;

// The store of a UCP request, so one platform serves several stores with no UCP configuration per store:
// 1. the store of the host the request came to: an assistant connects to the store's own host (/ucp/mcp, or /mcp of a
//    scope host), so it names the store;
// 2. the store of the request's Origin or Referer host, for calls a storefront makes to the platform's API host;
// 3. the platform's default store, VirtoCommerce:Stores:DefaultStore, then the obsolete UCP:DefaultStoreId;
// 4. the only open store.
// A host matches a store through VirtoCommerce:Stores:Domains or the domain of the store's URL.
public class UcpStoreContextResolver(
    IStoreService storeService,
    IStoreSearchService storeSearchService,
    IOptions<StoresOptions> storesOptions,
    IOptions<UcpOptions> ucpOptions)
{
    // Two results are enough to tell whether a store is the only open one.
    private const int OnlyStoreProbeSize = 2;

    private readonly StoresOptions _storesOptions = storesOptions.Value;
    private readonly UcpOptions _ucpOptions = ucpOptions.Value;

    public virtual async Task<Store> ResolveStoreAsync(HttpContext context)
    {
        foreach (var host in GetCandidateHosts(context?.Request))
        {
            var store = await FindStoreByDomainAsync(host);
            if (store != null)
            {
                return store;
            }
        }

        foreach (var storeId in new[] { _storesOptions.DefaultStore, _ucpOptions.DefaultStoreId }.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var store = await GetStoreByIdAsync(storeId);
            if (store != null)
            {
                return store;
            }
        }

        return await GetOnlyOpenStoreAsync();
    }

    protected virtual IEnumerable<string> GetCandidateHosts(HttpRequest request)
    {
        if (request == null)
        {
            return [];
        }

        return new[] { request.Host.Host, GetHost(request.Headers[HeaderNames.Origin]), GetHost(request.Headers[HeaderNames.Referer]) }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    protected virtual async Task<Store> FindStoreByDomainAsync(string host)
    {
        if (_storesOptions.Domains != null && _storesOptions.Domains.TryGetValue(host, out var storeId))
        {
            return await GetStoreByIdAsync(storeId);
        }

        var criteria = AbstractTypeFactory<StoreSearchCriteria>.TryCreateInstance();
        criteria.Domain = host;
        criteria.Take = 1;
        var result = await SearchStoresAsync(criteria);

        return result.Results.FirstOrDefault();
    }

    protected virtual async Task<Store> GetOnlyOpenStoreAsync()
    {
        var criteria = AbstractTypeFactory<StoreSearchCriteria>.TryCreateInstance();
        criteria.StoreStates = [StoreState.Open];
        criteria.Take = OnlyStoreProbeSize;
        var result = await SearchStoresAsync(criteria);

        return result.Results.Count == 1 ? result.Results[0] : null;
    }

    protected virtual Task<Store> GetStoreByIdAsync(string storeId)
    {
        return UcpDiagnostics.ExecuteDependency("stores", "GetStore", () => storeService.GetNoCloneAsync(storeId));
    }

    protected virtual Task<StoreSearchResult> SearchStoresAsync(StoreSearchCriteria criteria)
    {
        return UcpDiagnostics.ExecuteDependency("stores", "SearchStores", () => storeSearchService.SearchNoCloneAsync(criteria));
    }

    private static string GetHost(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;
    }
}
