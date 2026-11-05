using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using VirtoCommerce.MCP.Core.Extensions;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.MCP.Core.Services;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Options;
using VirtoCommerce.UCP.Core.Services;
using VirtoCommerce.UCP.Web.Services;

namespace VirtoCommerce.UCP.Web.Mcp;

// The OAuth resource of /ucp/mcp is the origin of the store the request came to (UCP:PublicOrigin, then the store
// resolved by the request host), the same origin /.well-known/ucp advertises. Declared with the ucp scope
// (WithPublicOriginResolver), so it decides the origin of that scope only.
public class UcpMcpPublicOriginResolver : McpPublicOriginResolver
{
    private readonly McpOptions _mcpOptions;
    private readonly UcpOptions _ucpOptions;
    private readonly IUcpPublicOriginResolver _ucpOriginResolver;

    public UcpMcpPublicOriginResolver(
        IOptions<McpOptions> options,
        IUcpPublicOriginResolver ucpOriginResolver,
        IOptions<UcpOptions> ucpOptions = null)
        : base(options)
    {
        _mcpOptions = options.Value;
        _ucpOptions = ucpOptions?.Value;
        _ucpOriginResolver = ucpOriginResolver;
    }

    public override async Task<string> GetOriginAsync(HttpContext context, McpScope scope)
    {
        var origin = await _ucpOriginResolver.GetOriginAsync();

        // At /mcp of a scope host, the resource is the host the request came to, as the MCP module decides for its
        // scopes: a configured origin of another host would expect an audience the client was not issued.
        return context.GetMcpEndpointPath() == McpScope.HostEndpointPath && !IsOriginOfHost(origin, context.Request.Host)
            ? GetRequestOrigin(context)
            : origin;
    }

    // The origin is configured when it is MCP:Scopes:ucp:PublicOrigin (or the obsolete UCP:PublicOrigin), the URL of
    // the request's store, or a host the scope is configured to serve /mcp on. The origin of the request itself, the
    // last fallback of the origin, is not: the client chose its Host and forwarded headers. The MCP module's
    // MCP:PublicOrigin is not the origin of this scope, so it does not count either.
    public override bool IsOriginConfigured(HttpContext context, McpScope scope)
    {
        if (context.GetMcpEndpointPath() == McpScope.HostEndpointPath || HasConfiguredOrigin())
        {
            return true;
        }

        // The store UcpStoreContextMiddleware resolved for the request, ahead of the MCP module's token check.
        var store = UcpRequestOptions.GetStore(context);

        return !string.IsNullOrWhiteSpace(store?.SecureUrl) || !string.IsNullOrWhiteSpace(store?.Url);
    }

    private bool HasConfiguredOrigin()
    {
        return (_mcpOptions.Scopes != null &&
            _mcpOptions.Scopes.TryGetValue(ModuleConstants.McpScopeName, out var scopeOptions) &&
            !string.IsNullOrWhiteSpace(scopeOptions?.PublicOrigin)) ||
            !string.IsNullOrWhiteSpace(_ucpOptions?.PublicOrigin);
    }
}
