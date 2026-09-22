using System;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Options;
using VirtoCommerce.UCP.Data.Services;
using VirtoCommerce.UCP.Web.Services;

namespace VirtoCommerce.UCP.Web.Mcp;

internal sealed class UcpMcpBuyerAuthenticationMiddleware
{
    private const string _bearerPrefix = "Bearer ";
    private const string _requiredScopes = "openid profile offline_access";

    private static readonly string[] _agentIdClaimTypes = ["client_id", "azp", "oi_prst"];
    private static readonly string[] _buyerIdClaimTypes = ["sub", ClaimTypes.NameIdentifier];

    private readonly RequestDelegate _next;
    private readonly UcpOptions _options;

    public UcpMcpBuyerAuthenticationMiddleware(RequestDelegate next, IOptions<UcpOptions> options = null)
    {
        _next = next;
        _options = options?.Value;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var hasBearerHeader = HasBearerHeader(context.Request);

        if (!hasBearerHeader)
        {
            context.User = RemoveUnboundBuyerIdentities(context.User);
        }

        var hasValidBearerPrincipal = HasValidBearerPrincipal(context, hasBearerHeader);
        var (requiresAuthenticatedBuyer, isLogout, requestError) = await InspectRequest(context.Request);
        if (requestError.HasValue)
        {
            await WriteRequestError(context, requestError.Value);
            return;
        }

        if (HasAuthorizationHeader(context.Request) && !hasValidBearerPrincipal)
        {
            if (isLogout)
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity());
                await _next(context);
                return;
            }

            await WriteChallenge(context, "invalid_token", "The Platform access token is invalid or does not identify a buyer.");
            return;
        }

        if (hasValidBearerPrincipal && !HasExpectedAudience(context))
        {
            await WriteChallenge(context, "invalid_token", "The Platform access token was not issued for this MCP resource.");
            return;
        }

        if (hasValidBearerPrincipal &&
            !await context.RequestServices.GetRequiredService<UcpMcpSessionService>().IsActive(context.User, context.RequestAborted))
        {
            if (!isLogout)
            {
                await WriteChallenge(context, "invalid_token", "The Platform OAuth session has ended. Sign in again to continue.");
                return;
            }

            context.User = new ClaimsPrincipal(new ClaimsIdentity());
        }

        if (requiresAuthenticatedBuyer && !HasAuthenticatedBuyerPrincipal(context.User))
        {
            await WriteChallenge(context, null, "Platform OAuth user identity is required to link the buyer account.");
            return;
        }

        await _next(context);
    }

    private static bool HasAuthorizationHeader(HttpRequest request)
    {
        return request.Headers.Authorization.Count > 0;
    }

    private static bool HasBearerHeader(HttpRequest request)
    {
        var authorization = request.Headers.Authorization;
        if (authorization.Count != 1)
        {
            return false;
        }

        var value = authorization[0];
        return value.StartsWith(_bearerPrefix, StringComparison.OrdinalIgnoreCase) &&
            value.Length > _bearerPrefix.Length;
    }

    private static bool HasValidBearerPrincipal(HttpContext context, bool hasBearerHeader)
    {
        return hasBearerHeader &&
            context.User?.Identities.Any(identity => identity.IsAuthenticated) == true;
    }

    private bool HasExpectedAudience(HttpContext context)
    {
        var expected = new Uri(GetOrigin(context.Request) + ModuleConstants.Endpoints.Mcp);

        return context.User.Claims
            .Where(claim => string.Equals(claim.Type, "aud", StringComparison.Ordinal))
            .Select(claim => claim.Value)
            .Any(value => IsExpectedAudience(value, expected));
    }

    private static bool IsExpectedAudience(string value, Uri expected)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var audience))
        {
            return false;
        }

        return HasExpectedAuthority(audience, expected) &&
            HasExpectedResourcePath(audience, expected);
    }

    private static bool HasExpectedAuthority(Uri audience, Uri expected)
    {
        return string.Equals(audience.Scheme, expected.Scheme, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(audience.IdnHost, expected.IdnHost, StringComparison.OrdinalIgnoreCase) &&
            audience.Port == expected.Port;
    }

    private static bool HasExpectedResourcePath(Uri audience, Uri expected)
    {
        return string.Equals(audience.PathAndQuery, expected.PathAndQuery, StringComparison.Ordinal) &&
            string.IsNullOrEmpty(audience.Fragment) &&
            string.IsNullOrEmpty(audience.UserInfo);
    }

    private static bool HasAuthenticatedBuyerPrincipal(ClaimsPrincipal principal)
    {
        var authenticatedClaims = principal?.Identities
            .Where(identity => identity.IsAuthenticated)
            .SelectMany(identity => identity.Claims)
            .ToArray() ?? [];
        var buyerIds = authenticatedClaims
            .Where(claim => _buyerIdClaimTypes.Contains(claim.Type, StringComparer.Ordinal))
            .Select(claim => claim.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var agentIds = authenticatedClaims
            .Where(claim => _agentIdClaimTypes.Contains(claim.Type, StringComparer.Ordinal))
            .Select(claim => claim.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return buyerIds.Length == 1 && !agentIds.Contains(buyerIds[0], StringComparer.OrdinalIgnoreCase);
    }

    private static ClaimsPrincipal RemoveUnboundBuyerIdentities(ClaimsPrincipal principal)
    {
        var identities = principal?.Identities
            .Where(identity => !HasAuthenticatedBuyerPrincipal(new ClaimsPrincipal(identity)))
            .ToArray() ?? [];

        return new ClaimsPrincipal(identities);
    }

    private static async Task<(bool RequiresIdentityLinking, bool IsLogout, McpErrorCode? Error)> InspectRequest(HttpRequest request)
    {
        if (!HttpMethods.IsPost(request.Method) || !request.HasJsonContentType())
        {
            return (false, false, null);
        }

        request.EnableBuffering();
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: request.HttpContext.RequestAborted);
            var root = document.RootElement;
            if (!IsValidMessage(root))
            {
                return (false, false, McpErrorCode.InvalidRequest);
            }

            return (RequestsTool(root, ModuleConstants.McpTools.LinkBuyerIdentity),
                RequestsTool(root, ModuleConstants.McpTools.LogoutBuyer), null);
        }
        catch (JsonException)
        {
            return (false, false, McpErrorCode.ParseError);
        }
        catch (IOException)
        {
            return (false, false, McpErrorCode.ParseError);
        }
        finally
        {
            request.Body.Position = 0;
        }
    }

    private static bool RequestsTool(JsonElement root, string toolName)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("method", out var method) ||
            method.ValueKind != JsonValueKind.String ||
            !method.ValueEquals("tools/call"))
        {
            return false;
        }

        if (!root.TryGetProperty("params", out var parameters) ||
            parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("name", out var name))
        {
            return false;
        }

        return name.ValueKind == JsonValueKind.String &&
            string.Equals(name.GetString(), toolName, StringComparison.Ordinal);
    }

    private static bool IsValidMessage(JsonElement root)
    {
        try
        {
            return root.Deserialize<JsonRpcMessage>(McpJsonUtilities.DefaultOptions) != null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task WriteRequestError(HttpContext context, McpErrorCode errorCode)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = (object)null,
            error = new
            {
                code = (int)errorCode,
                message = errorCode == McpErrorCode.ParseError ? "Parse error" : "Invalid Request",
            },
        }), context.RequestAborted);
    }

    private async Task WriteChallenge(HttpContext context, string error, string message)
    {
        var origin = GetOrigin(context.Request);
        var metadataUrl = origin + ModuleConstants.Endpoints.McpProtectedResourceMetadata;
        var challenge = $"Bearer realm=\"{Escape(origin)}\", resource_metadata=\"{Escape(metadataUrl)}\", scope=\"{_requiredScopes}\"";
        if (!string.IsNullOrWhiteSpace(error))
        {
            challenge += $", error=\"{Escape(error)}\"";
        }

        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = challenge;
        context.Response.Headers[HeaderNames.CacheControl] = "no-store";
        await context.Response.WriteAsJsonAsync(new
        {
            messages = new[]
            {
                new
                {
                    type = "error",
                    code = ModuleConstants.ErrorCodes.IdentityRequired,
                    content = message,
                    severity = "requires_buyer_review",
                },
            },
        }, context.RequestAborted);
    }

    private string GetOrigin(HttpRequest request)
    {
        return UcpPublicEndpoints.GetOrigin(_options, request);
    }

    private static string Escape(string value)
    {
        return value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }
}
