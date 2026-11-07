using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Options;
using VirtoCommerce.UCP.Core.Services;

namespace VirtoCommerce.UCP.Web.Services;

// The public origin of UCP: a fixed origin when one is configured (MCP:Scopes:ucp:PublicOrigin, then the obsolete
// UCP:PublicOrigin), otherwise the URL of the request's store, so each store of the platform has its own origin, and
// last the origin of the request.
public class UcpPublicOriginResolver : IUcpPublicOriginResolver
{
    private readonly UcpOptions _options;
    private readonly McpOptions _mcpOptions;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly UcpStoreContextResolver _storeContextResolver;

    public UcpPublicOriginResolver(
        IOptions<UcpOptions> options,
        IHttpContextAccessor httpContextAccessor,
        UcpStoreContextResolver storeContextResolver,
        IOptions<McpOptions> mcpOptions = null)
    {
        _options = options.Value;
        _mcpOptions = mcpOptions?.Value;
        _httpContextAccessor = httpContextAccessor;
        _storeContextResolver = storeContextResolver;
    }

    public virtual async Task<string> GetOriginAsync()
    {
        var configuredOrigin = GetConfiguredOrigin();
        if (!string.IsNullOrWhiteSpace(configuredOrigin))
        {
            return configuredOrigin.TrimEnd('/');
        }

        var context = _httpContextAccessor.HttpContext;
        var store = UcpRequestOptions.GetStore(context) ?? await _storeContextResolver.ResolveStoreAsync(context);
        if (!string.IsNullOrWhiteSpace(store?.SecureUrl))
        {
            return store.SecureUrl.TrimEnd('/');
        }

        if (!string.IsNullOrWhiteSpace(store?.Url))
        {
            return store.Url.TrimEnd('/');
        }

        var request = context?.Request;
        return request == null ? null : $"{request.Scheme}://{request.Host}{request.PathBase}".TrimEnd('/');
    }

    protected virtual string GetConfiguredOrigin()
    {
        if (_mcpOptions?.Scopes != null &&
            _mcpOptions.Scopes.TryGetValue(ModuleConstants.McpScopeName, out var scopeOptions) &&
            !string.IsNullOrWhiteSpace(scopeOptions?.PublicOrigin))
        {
            return scopeOptions.PublicOrigin;
        }

        return _options.PublicOrigin;
    }
}
