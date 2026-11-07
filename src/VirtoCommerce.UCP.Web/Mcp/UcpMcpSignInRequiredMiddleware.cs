using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using VirtoCommerce.MCP.Core.Extensions;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.MCP.Core.Services;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Services;

namespace VirtoCommerce.UCP.Web.Mcp;

/// <summary>
/// When the store's catalog is closed to anonymous buyers (<c>UCP:AnonymousCatalog</c> is off, or the default store does
/// not allow anonymous users), asks an anonymous request on the ucp scope to sign in, <c>initialize</c> and
/// <c>tools/list</c> included, so the MCP client starts OAuth when it connects. <c>logout_buyer</c> stays callable
/// without a token. A request with a bearer token goes on to the MCP module, which checks the token.
/// </summary>
internal sealed class UcpMcpSignInRequiredMiddleware
{
    private const string BearerPrefix = "Bearer ";
    private const string SignInRequiredMessage = "This store requires sign-in. Complete Platform OAuth to continue.";

    private readonly RequestDelegate _next;

    public UcpMcpSignInRequiredMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        IUcpProfileService profileService)
    {
        if (!await RequiresSignInAsync(context, profileService))
        {
            await _next(context);
            return;
        }

        var request = context.GetMcpRequest();
        var scope = context.GetMcpScope();
        var toolName = request?.IsToolCall == true ? request.ToolNames.FirstOrDefault(x => !IsLogoutBuyer(x)) : null;

        // The scope's own challenge writer and origin, as the MCP module challenges its requests.
        var originResolver = context.RequestServices.GetMcpScopeService<IMcpPublicOriginResolver>(scope);
        var challengeWriter = context.RequestServices.GetMcpScopeService<McpChallengeWriter>(scope);
        await challengeWriter.WriteChallengeAsync(context, new McpChallenge
        {
            Scope = scope,
            Origin = await originResolver.GetOriginAsync(context, scope),
            EndpointPath = context.GetMcpEndpointPath(),
            ToolName = toolName,
            ErrorDescription = SignInRequiredMessage,
        });
    }

    // False when the request goes on to the MCP module: it carries a token, the catalog is open, it only logs the buyer
    // out, or it comes from an origin the scope refuses.
    private static async Task<bool> RequiresSignInAsync(HttpContext context, IUcpProfileService profileService)
    {
        if (HasBearerHeader(context.Request) ||
            (await profileService.GetProfile(context.RequestAborted)).Auth?.AnonymousCatalog != false)
        {
            return false;
        }

        // The request as the MCP module read it ahead of authentication (UseMcpScopeRequests). A request that names its
        // tool more than once, in properties that differ only in case, goes on only when every name is logout_buyer: the
        // MCP server may bind any of them. A request the MCP module did not read is challenged as any other call.
        var request = context.GetMcpRequest();
        if (request?.IsToolCall == true && request.ToolNames.Count > 0 && request.ToolNames.All(IsLogoutBuyer))
        {
            return false;
        }

        // A browser request from an origin the scope does not allow goes on to the MCP module, which refuses it with 403
        // as the MCP transport requires, rather than being asked to sign in.
        return !await IsRefusedOriginAsync(context, context.GetMcpScope());
    }

    // As the MCP module's Origin check decides: a request without an Origin header is not checked, more than one is refused.
    private static async Task<bool> IsRefusedOriginAsync(HttpContext context, McpScope scope)
    {
        var origins = context.Request.Headers.Origin;
        if (StringValues.IsNullOrEmpty(origins))
        {
            return false;
        }

        var originValidator = context.RequestServices.GetMcpScopeService<IMcpOriginValidator>(scope);

        return origins.Count != 1 || !await originValidator.IsAllowedAsync(context, scope, origins.ToString());
    }

    private static bool IsLogoutBuyer(string toolName)
    {
        return string.Equals(toolName, ModuleConstants.McpTools.LogoutBuyer, StringComparison.Ordinal);
    }

    private static bool HasBearerHeader(HttpRequest request)
    {
        var authorization = request.Headers.Authorization;

        return authorization.Count == 1 &&
            authorization[0].StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase) &&
            authorization[0].Length > BearerPrefix.Length;
    }
}
