using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.StoreModule.Core.Model;
using VirtoCommerce.StoreModule.Core.Model.Search;
using VirtoCommerce.StoreModule.Core.Services;
using VirtoCommerce.UCP.Core.Diagnostics;
using VirtoCommerce.UCP.Core.Options;
using VirtoCommerce.Xapi.Core.Models;
using VirtoCommerce.Xapi.Core.Services;

namespace VirtoCommerce.UCP.Web.Services;

// The store of a UCP request, so one platform serves several stores with no UCP configuration per store:
// 1. the store of the host the request came to, found as XAPI finds the store of a storefront's domain
//    (IStoreDomainResolverService: VirtoCommerce:Stores:Domains, then the store's URL): an assistant connects to the
//    store's own host (/ucp/mcp, or /mcp of a scope host), so it names the store;
// 2. the platform's default store, VirtoCommerce:Stores:DefaultStore, then the obsolete UCP:DefaultStoreId;
// 3. the only open store.
// Origin and Referer are not read: the caller sets them, and the store decides the sign-in gate and the token audience.
// An operation that names another store with store_id switches the request to that store (UseOperationStoreAsync).
public class UcpStoreContextResolver(
    IStoreDomainResolverService storeDomainResolverService,
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
        var host = context?.Request.Host.Host;
        if (!string.IsNullOrEmpty(host))
        {
            var store = await FindStoreByDomainAsync(host);
            if (store != null)
            {
                return store;
            }
        }

        // The domain resolver already fell back to the platform's default store; a request with no host did not reach it.
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

    protected virtual Task<Store> FindStoreByDomainAsync(string host)
    {
        var request = AbstractTypeFactory<StoreDomainRequest>.TryCreateInstance();
        request.Domain = host;

        return UcpDiagnostics.ExecuteDependency("stores", "ResolveStoreByDomain", () => storeDomainResolverService.GetStoreAsync(request));
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
}
