using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.MCP.Core.Services;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Services;
using McpScopes = VirtoCommerce.MCP.Core.McpScopes;

namespace VirtoCommerce.UCP.Web.Mcp;

// The OAuth resource of /ucp/mcp is the origin of the store the request came to (UCP:PublicOrigin, then the store
// resolved by the request host), the same origin /.well-known/ucp advertises. Other scopes keep the MCP module's origin.
public class UcpMcpPublicOriginResolver(
    IOptions<McpOptions> options,
    IUcpPublicOriginResolver ucpOriginResolver)
    : McpPublicOriginResolver(options)
{
    public override async Task<string> GetOriginAsync(HttpContext context, McpScope scope)
    {
        return string.Equals(scope?.Name, ModuleConstants.McpScopeName, StringComparison.OrdinalIgnoreCase)
            ? await ucpOriginResolver.GetOriginAsync()
            : await base.GetOriginAsync(context, scope);
    }
}
