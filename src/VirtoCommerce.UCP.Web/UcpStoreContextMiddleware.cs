using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
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

    public async Task InvokeAsync(HttpContext context, UcpStoreContextResolver storeContextResolver, IOptions<UcpOptions> options)
    {
        var store = await storeContextResolver.ResolveStoreAsync(context);
        UcpRequestOptions.SetStore(context, store, options.Value);

        await _next(context);
    }
}
