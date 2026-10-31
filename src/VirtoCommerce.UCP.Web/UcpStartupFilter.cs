using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using VirtoCommerce.MCP.Core.Services;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Web.Mcp;

namespace VirtoCommerce.UCP.Web;

// Adds UCP's gate and the sign-in rule of a closed catalog at the start of the request pipeline. Module startups run
// in dependency order, and the MCP module serves /ucp/mcp in a branch that never rejoins the main pipeline, so a UCP
// startup would come too late to cover it.
internal sealed class UcpStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.UseUcpEnabledGate();
            app.UseWhen(IsUcpMcpRequest, branch => branch.UseMiddleware<UcpMcpSignInRequiredMiddleware>());
            next(app);
        };
    }

    private static bool IsUcpMcpRequest(Microsoft.AspNetCore.Http.HttpContext context)
    {
        var scope = context.RequestServices.GetRequiredService<IMcpScopeRegistry>().FindScope(context.Request.Host, context.Request.Path);

        return string.Equals(scope?.Name, ModuleConstants.McpScopeName, StringComparison.OrdinalIgnoreCase);
    }
}
