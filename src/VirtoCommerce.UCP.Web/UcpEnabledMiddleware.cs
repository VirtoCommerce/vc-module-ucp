using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using VirtoCommerce.MCP.Core.Services;
using VirtoCommerce.UCP.Core;

namespace VirtoCommerce.UCP.Web;

/// <summary>
/// Answers 404 on every UCP surface while the MCP module's <c>ucp</c> scope is switched off
/// (<c>MCP:Scopes:ucp:Enabled</c> is <c>false</c>), so one switch turns UCP off: the MCP endpoint, the REST API,
/// discovery and the scoped GraphQL schema.
/// </summary>
internal sealed class UcpEnabledMiddleware
{
    private readonly RequestDelegate _next;

    public UcpEnabledMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public Task InvokeAsync(HttpContext context, IMcpScopeRegistry scopeRegistry)
    {
        if (scopeRegistry.GetScope(ModuleConstants.McpScopeName) == null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;

            return Task.CompletedTask;
        }

        return _next(context);
    }
}
