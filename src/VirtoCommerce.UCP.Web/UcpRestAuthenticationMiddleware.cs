using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Net.Http.Headers;
using Newtonsoft.Json;
using VirtoCommerce.MCP.Core.Extensions;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.MCP.Core.Services;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Models;
using McpModuleConstants = VirtoCommerce.MCP.Core.ModuleConstants;

namespace VirtoCommerce.UCP.Web;

/// <summary>
/// The buyer of a UCP REST request (<c>/ucp/v1/*</c>), by the token rules of the MCP endpoint: only an OpenIddict bearer
/// token issued for the UCP resource (<c>{public origin}/ucp/mcp</c>), to a client, for a user, and still active in the
/// token store (<see cref="IMcpTokenValidator"/>). A back-office cookie, an API key or Basic credentials never act as
/// the buyer: the request is anonymous for UCP. A bearer token that does not meet the rules is answered with
/// <c>401 invalid_token</c> instead of being served anonymously.
/// </summary>
internal sealed class UcpRestAuthenticationMiddleware
{
    private const string BearerPrefix = "Bearer ";
    private const string OriginNotConfiguredMessage = "This UCP server has no public origin configured (MCP:Scopes:ucp:PublicOrigin or the store's URL), so it cannot check that a token was issued for it. Ask the administrator to set it.";

    private static readonly PathString _restPrefix = new("/ucp/v1");
    private static readonly PathString _storefrontRestorePrefix = new("/ucp/v1/internal/handoff");

    private readonly RequestDelegate _next;

    public UcpRestAuthenticationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        IMcpScopeRegistry scopeRegistry,
        IMcpTokenValidator tokenValidator,
        IHostEnvironment environment)
    {
        // Whatever the platform's default authentication made of a cookie, an API key or Basic credentials is not the buyer.
        context.User = new ClaimsPrincipal(new ClaimsIdentity());

        // The UcpEnabledMiddleware answers 404 before this step when the scope is switched off.
        var scope = scopeRegistry.GetScope(ModuleConstants.McpScopeName);
        if (scope == null || !HasBearerHeader(context.Request))
        {
            await _next(context);
            return;
        }

        var originResolver = context.RequestServices.GetMcpScopeService<IMcpPublicOriginResolver>(scope);
        var origin = await originResolver.GetOriginAsync(context, scope);

        var principal = await tokenValidator.AuthenticateAsync(context);

        // The audience a token must have is UCP's public origin, as for the MCP endpoint: taken from the request's Host and
        // forwarded headers, it would be whatever host the client was allowed to get a token for.
        var tokenError = !originResolver.IsOriginConfigured(context, scope) && !environment.IsDevelopment()
            ? OriginNotConfiguredMessage
            : await tokenValidator.GetTokenErrorAsync(principal, GetResource(origin, scope), context.RequestAborted);
        if (tokenError != null)
        {
            await WriteChallengeAsync(context, origin, scope, tokenError);
            return;
        }

        context.User = principal;
        await _next(context);
    }

    // REST requests with UCP's own buyer identity. The storefront's checkout link restore is left out: the storefront calls
    // it with the buyer's storefront sign-in, which is not a token for the UCP resource, and the restore checks that the
    // signed-in user is the buyer the link was created for.
    internal static bool IsRestRequest(HttpContext context)
    {
        var path = context.Request.Path;

        return path.StartsWithSegments(_restPrefix, StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWithSegments(_storefrontRestorePrefix, StringComparison.OrdinalIgnoreCase);
    }

    // The OAuth resource of UCP, the one discovery and the protected-resource metadata advertise: the MCP endpoint /ucp/mcp
    // at UCP's public origin.
    internal static string GetResource(string origin, McpScope scope)
    {
        return origin + scope.Path;
    }

    private static async Task WriteChallengeAsync(HttpContext context, string origin, McpScope scope, string errorDescription)
    {
        var error = new UcpError
        {
            Code = ModuleConstants.ErrorCodes.IdentityRequired,
            Message = errorDescription,
            CorrelationId = context.TraceIdentifier,
        };

        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate =
            $"Bearer realm=\"{Escape(origin)}\", resource_metadata=\"{Escape(origin + scope.ProtectedResourceMetadataPath)}\", " +
            $"scope=\"{string.Join(' ', McpModuleConstants.Security.OAuthScopes)}\", " +
            $"error=\"invalid_token\", error_description=\"{Escape(errorDescription)}\"";
        context.Response.Headers[HeaderNames.CacheControl] = "no-store";
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(JsonConvert.SerializeObject(error), context.RequestAborted);
    }

    private static string Escape(string value)
    {
        return value?.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private static bool HasBearerHeader(HttpRequest request)
    {
        var authorization = request.Headers.Authorization;

        return authorization.Count == 1 &&
            authorization[0].StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase) &&
            authorization[0].Length > BearerPrefix.Length;
    }
}
