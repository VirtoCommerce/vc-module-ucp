using System;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
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
        IMcpScopeRegistry scopeRegistry,
        IUcpProfileService profileService,
        IMcpPublicOriginResolver originResolver,
        McpChallengeWriter challengeWriter)
    {
        if (HasBearerHeader(context.Request))
        {
            await _next(context);
            return;
        }

        if ((await profileService.GetProfile(context.RequestAborted)).Auth?.AnonymousCatalog != false)
        {
            await _next(context);
            return;
        }

        var toolName = await ReadToolNameAsync(context.Request);
        if (string.Equals(toolName, ModuleConstants.McpTools.LogoutBuyer, StringComparison.Ordinal))
        {
            await _next(context);
            return;
        }

        var scope = scopeRegistry.FindScope(context.Request.Host, context.Request.Path);
        await challengeWriter.WriteChallengeAsync(context, new McpChallenge
        {
            Scope = scope,
            Origin = await originResolver.GetOriginAsync(context, scope),
            EndpointPath = IsHostEndpointPath(context.Request.Path) ? McpScope.HostEndpointPath : scope.Path,
            ToolName = toolName,
            ErrorDescription = SignInRequiredMessage,
        });
    }

    // The tool of a tools/call request; the body stays readable for the MCP server.
    private static async Task<string> ReadToolNameAsync(HttpRequest request)
    {
        if (!HttpMethods.IsPost(request.Method))
        {
            return null;
        }

        request.EnableBuffering();
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: request.HttpContext.RequestAborted);
            var root = document.RootElement;

            return ReadString(root, "method") == "tools/call" && root.TryGetProperty("params", out var parameters)
                ? ReadString(parameters, "name")
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            request.Body.Position = 0;
        }
    }

    private static string ReadString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private static bool HasBearerHeader(HttpRequest request)
    {
        var authorization = request.Headers.Authorization;

        return authorization.Count == 1 &&
            authorization[0].StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase) &&
            authorization[0].Length > BearerPrefix.Length;
    }

    private static bool IsHostEndpointPath(PathString path)
    {
        return string.Equals(path.Value?.TrimEnd('/'), McpScope.HostEndpointPath, StringComparison.OrdinalIgnoreCase);
    }
}
