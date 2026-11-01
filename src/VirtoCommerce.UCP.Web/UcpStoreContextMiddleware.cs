using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VirtoCommerce.StoreModule.Core.Model;
using VirtoCommerce.UCP.Core.Options;
using VirtoCommerce.UCP.Web.Services;

namespace VirtoCommerce.UCP.Web;

// Resolves the store of a UCP request once, before the REST API, the MCP endpoint and the scoped GraphQL schema run,
// so every UCP service of the request sees the same store and its options (IUcpRequestOptions).
internal sealed class UcpStoreContextMiddleware
{
    private readonly RequestDelegate _next;

    public UcpStoreContextMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, UcpStoreContextResolver storeContextResolver, IOptions<UcpOptions> options, ILogger<UcpStoreContextMiddleware> logger)
    {
        // This runs ahead of the platform's exception handling: a store that cannot be read leaves the request with the
        // configuration, as a request with no store has, instead of a bare error response.
        Store store = null;
        try
        {
            store = await storeContextResolver.ResolveStoreAsync(context);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "UCP could not resolve the store of {Host}{Path}; the request uses the configuration", context.Request.Host, context.Request.Path);
        }

        UcpRequestOptions.SetStore(context, store, options.Value);

        await _next(context);
    }
}
