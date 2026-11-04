using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using VirtoCommerce.MCP.Core.Extensions;
using VirtoCommerce.Platform.Core.Modularity;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Web.Mcp;

namespace VirtoCommerce.UCP.Web;

// UCP's steps in the platform pipeline, after routing and before authentication: the gate of the ucp scope switch, the
// store of the request, and the sign-in rule of a closed catalog. The MCP module serves /ucp/mcp as an endpoint of the
// same pipeline, so these steps cover it as they cover the REST API and discovery.
public class UcpStartup : IPlatformStartup
{
    public void ConfigureAfterRouting(IApplicationBuilder app, IConfiguration config)
    {
        app.UseUcpRequests();
    }
}

public static class UcpApplicationBuilderExtensions
{
    public static IApplicationBuilder UseUcpRequests(this IApplicationBuilder app)
    {
        app.UseUcpEnabledGate();
        app.UseWhen(IsUcpRequest, branch => branch.UseMiddleware<UcpStoreContextMiddleware>());
        app.UseWhen(IsUcpMcpRequest, branch => branch.UseMiddleware<UcpMcpSignInRequiredMiddleware>());

        return app;
    }

    private static bool IsUcpRequest(HttpContext context)
    {
        return UcpEnabledGateExtensions.IsGatedPath(context.Request.Path) || IsUcpMcpRequest(context);
    }

    // /ucp/mcp, or /mcp on a host of the ucp scope: the scope of the MCP endpoint that routing matched.
    private static bool IsUcpMcpRequest(HttpContext context)
    {
        return string.Equals(context.GetMcpScope()?.Name, ModuleConstants.McpScopeName, StringComparison.OrdinalIgnoreCase);
    }
}
