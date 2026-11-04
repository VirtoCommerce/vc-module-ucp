using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.MCP.Core.Services;
using VirtoCommerce.UCP.Core.Services;

namespace VirtoCommerce.UCP.Web.Mcp;

// The OAuth resource of /ucp/mcp is the origin of the store the request came to (UCP:PublicOrigin, then the store
// resolved by the request host), the same origin /.well-known/ucp advertises. Declared with the ucp scope
// (WithPublicOriginResolver), so it decides the origin of that scope only.
public class UcpMcpPublicOriginResolver(
    IOptions<McpOptions> options,
    IUcpPublicOriginResolver ucpOriginResolver)
    : McpPublicOriginResolver(options)
{
    public override Task<string> GetOriginAsync(HttpContext context, McpScope scope)
    {
        return ucpOriginResolver.GetOriginAsync();
    }
}
