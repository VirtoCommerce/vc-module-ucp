using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
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

        // A request that names its tool more than once, in properties that differ only in case, goes on only when every
        // name is logout_buyer: the MCP server may bind any of them.
        var toolNames = await ReadToolNamesAsync(context.Request);
        if (toolNames.Count > 0 && toolNames.All(IsLogoutBuyer))
        {
            await _next(context);
            return;
        }

        var toolName = toolNames.FirstOrDefault(x => !IsLogoutBuyer(x));

        // The scope's own challenge writer and origin, as the MCP module challenges its requests.
        var scope = context.GetMcpScope();
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

    private static bool IsLogoutBuyer(string toolName)
    {
        return string.Equals(toolName, ModuleConstants.McpTools.LogoutBuyer, StringComparison.Ordinal);
    }

    // The tool names of a tools/call request; empty for any other request. The MCP server binds the properties of a
    // request ignoring case, so every property that matches ignoring case is read, and a value that is not a string
    // is read as null. The body stays readable for the MCP server.
    private static async Task<IList<string>> ReadToolNamesAsync(HttpRequest request)
    {
        if (!HttpMethods.IsPost(request.Method))
        {
            return [];
        }

        request.EnableBuffering();
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: request.HttpContext.RequestAborted);
            var root = document.RootElement;
            var methods = ReadStrings(root, "method");
            if (methods.Count == 0 || !methods.All(x => x == "tools/call"))
            {
                return [];
            }

            return FindProperties(root, "params")
                .SelectMany(parameters => parameters.ValueKind == JsonValueKind.Object ? ReadStrings(parameters, "name") : [null])
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
        finally
        {
            request.Body.Position = 0;
        }
    }

    private static List<string> ReadStrings(JsonElement element, string propertyName)
    {
        return FindProperties(element, propertyName)
            .Select(value => value.ValueKind == JsonValueKind.String ? value.GetString() : null)
            .ToList();
    }

    private static IEnumerable<JsonElement> FindProperties(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
            ? element.EnumerateObject()
                .Where(x => string.Equals(x.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Value)
            : [];
    }

    private static bool HasBearerHeader(HttpRequest request)
    {
        var authorization = request.Headers.Authorization;

        return authorization.Count == 1 &&
            authorization[0].StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase) &&
            authorization[0].Length > BearerPrefix.Length;
    }
}
