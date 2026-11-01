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
// A host matches a store through VirtoCommerce:Stores:Domains or the host of the store's URL or secure URL.
// An operation that names another store with store_id switches the request to that store (UseOperationStoreAsync).
public class UcpStoreContextResolver(
    IStoreService storeService,
    IStoreSearchService storeSearchService,
    IOptions<StoresOptions> storesOptions,
    IOptions<UcpOptions> ucpOptions)
{
    // Two results are enough to tell whether a store is the only open one.
    private const int OnlyStoreProbeSize = 2;

    // The store search's domain filter is a substring match on the URL, so a few candidates are checked for the exact host.
    private const int DomainProbeSize = 20;

    // The stores read to match a host by the secure URL, which the store search does not filter by.
    private const int AllStoresProbeSize = 1000;

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

    // The store an operation names with store_id, when it is not the store of the request: the operation runs with that
    // store's options (its currency, language, URL and UCP settings) for the rest of the request. Null when no store has
    // that id; the request keeps its store then.
    public virtual async Task<Store> UseOperationStoreAsync(HttpContext context, string storeId)
    {
        if (context == null || string.IsNullOrWhiteSpace(storeId))
        {
            return null;
        }

        var current = UcpRequestOptions.GetStore(context);
        if (current != null && string.Equals(current.Id, storeId, StringComparison.Ordinal))
        {
            return current;
        }

        var store = await GetStoreByIdAsync(storeId);
        if (store != null)
        {
            UcpRequestOptions.SetStore(context, store, _ucpOptions);
        }

        return store;
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
        criteria.Take = DomainProbeSize;
        var match = FindByHost((await SearchStoresAsync(criteria)).Results, host);
        if (match != null)
        {
            return match;
        }

        // A store whose host is only in its secure URL
        var allStores = AbstractTypeFactory<StoreSearchCriteria>.TryCreateInstance();
        allStores.Take = AllStoresProbeSize;

        return FindByHost((await SearchStoresAsync(allStores)).Results, host);
    }

    protected static Store FindByHost(IEnumerable<Store> stores, string host)
    {
        return stores?.FirstOrDefault(store =>
            string.Equals(GetHost(store.SecureUrl), host, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(GetHost(store.Url), host, StringComparison.OrdinalIgnoreCase));
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
