using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Models;
using VirtoCommerce.UCP.Core.Services;

namespace VirtoCommerce.UCP.Data.Services;

public sealed class UcpBuyerContextAccessor : IUcpBuyerContextAccessor
{
    private const string _anonymousBuyerPrefix = "ucp-anonymous-";
    private const string _organizationIdClaimType = "organization_id";

    private static readonly string[] _userIdClaimTypes = ["sub", ClaimTypes.NameIdentifier];
    private static readonly string[] _agentIdClaimTypes = ["client_id", "azp", "oi_prst"];

    private readonly IHttpContextAccessor _httpContextAccessor;

    public UcpBuyerContextAccessor(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public UcpBuyerContext Resolve(UcpBuyerContextRequest request = null)
    {
        request ??= new UcpBuyerContextRequest();
        RejectLegacyBuyerHeaders();

        var requestedBuyerId = ResolveRequestedValue(request.RequestedBuyerIds, "buyer_id", StringComparer.Ordinal);
        var requestedOrganizationId = ResolveRequestedValue(request.RequestedOrganizationIds, "organization_id", StringComparer.OrdinalIgnoreCase);
        var principal = _httpContextAccessor.HttpContext?.User;
        var authenticatedIdentities = principal?.Identities.Where(identity => identity.IsAuthenticated).ToArray() ?? [];

        if (HasAuthenticatedBuyer(authenticatedIdentities))
        {
            return ResolveAuthenticated(authenticatedIdentities, requestedBuyerId, requestedOrganizationId);
        }

        if (request.RequireAuthenticatedBuyer)
        {
            throw CreateException(
                ModuleConstants.ErrorCodes.IdentityRequired,
                "Platform OAuth user identity is required for this operation.",
                StatusCodes.Status401Unauthorized);
        }

        return ResolveAnonymous(request, requestedBuyerId, requestedOrganizationId, authenticatedIdentities);
    }

    private UcpBuyerContext ResolveAuthenticated(
        ClaimsIdentity[] authenticatedIdentities,
        string requestedBuyerId,
        string requestedOrganizationId)
    {
        if (authenticatedIdentities.Length == 0)
        {
            throw CreateException(
                ModuleConstants.ErrorCodes.IdentityRequired,
                "Platform OAuth user identity is required for this operation.",
                StatusCodes.Status401Unauthorized);
        }

        var userId = ResolveClaim(authenticatedIdentities, _userIdClaimTypes, "user identifier", required: true);
        var organizationId = ResolveClaim(authenticatedIdentities, [_organizationIdClaimType], "organization identifier", required: false);
        var agentId = ResolveClaim(authenticatedIdentities, _agentIdClaimTypes, "agent identifier", required: false);

        if (string.Equals(userId, agentId, StringComparison.OrdinalIgnoreCase))
        {
            throw CreateException(
                ModuleConstants.ErrorCodes.IdentityRequired,
                "The Platform access token identifies the MCP client, but not a buyer.",
                StatusCodes.Status401Unauthorized);
        }

        var sourceAnonymousBuyerId = IsAnonymousBuyerId(requestedBuyerId)
            ? requestedBuyerId
            : null;
        if ((sourceAnonymousBuyerId == null && !Matches(requestedBuyerId, userId)) ||
            !Matches(requestedOrganizationId, organizationId))
        {
            throw CreateException(
                ModuleConstants.ErrorCodes.BuyerContextMismatch,
                "Buyer context does not match the authenticated Platform identity.",
                StatusCodes.Status403Forbidden);
        }

        return new UcpBuyerContext
        {
            UserId = userId,
            PublicBuyerId = userId,
            SourceAnonymousBuyerId = sourceAnonymousBuyerId,
            OrganizationId = organizationId,
            AgentId = agentId,
            CorrelationId = _httpContextAccessor.HttpContext?.TraceIdentifier,
            Principal = new ClaimsPrincipal(authenticatedIdentities),
            IsAuthenticated = true,
        };
    }

    private UcpBuyerContext ResolveAnonymous(
        UcpBuyerContextRequest request,
        string requestedBuyerId,
        string requestedOrganizationId,
        ClaimsIdentity[] authenticatedIdentities)
    {
        if (!string.IsNullOrWhiteSpace(requestedOrganizationId))
        {
            throw CreateException(
                ModuleConstants.ErrorCodes.BuyerContextMismatch,
                "Organization context requires a Platform-authenticated buyer.",
                StatusCodes.Status403Forbidden);
        }

        var buyerId = requestedBuyerId;
        if (!string.IsNullOrWhiteSpace(buyerId) && !IsAnonymousBuyerId(buyerId))
        {
            throw CreateException(
                ModuleConstants.ErrorCodes.BuyerContextMismatch,
                "Anonymous buyer context is invalid.",
                StatusCodes.Status403Forbidden);
        }

        if (string.IsNullOrWhiteSpace(buyerId) && request.CreateAnonymousBuyer)
        {
            buyerId = _anonymousBuyerPrefix + Guid.NewGuid().ToString("N");
        }
        else if (string.IsNullOrWhiteSpace(buyerId) && request.RequireBuyer)
        {
            throw CreateException(ModuleConstants.ErrorCodes.InvalidRequest, "buyer_id is required for anonymous continuation.");
        }

        return new UcpBuyerContext
        {
            UserId = buyerId,
            PublicBuyerId = buyerId,
            AgentId = ResolveClaim(authenticatedIdentities, _agentIdClaimTypes, "agent identifier", required: false),
            CorrelationId = _httpContextAccessor.HttpContext?.TraceIdentifier,
            Principal = CreateAnonymousPrincipal(buyerId),
        };
    }

    private bool HasAuthenticatedBuyer(ClaimsIdentity[] identities)
    {
        var subject = ResolveClaim(identities, _userIdClaimTypes, "user identifier", required: false);
        var agentId = ResolveClaim(identities, _agentIdClaimTypes, "agent identifier", required: false);
        return !string.IsNullOrWhiteSpace(subject) &&
            !string.Equals(subject, agentId, StringComparison.OrdinalIgnoreCase);
    }

    private static ClaimsPrincipal CreateAnonymousPrincipal(string buyerId)
    {
        var identity = new ClaimsIdentity();
        if (!string.IsNullOrWhiteSpace(buyerId))
        {
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, buyerId));
            identity.AddClaim(new Claim("sub", buyerId));
            identity.AddClaim(new Claim("user_id", buyerId));
        }

        return new ClaimsPrincipal(identity);
    }

    private void RejectLegacyBuyerHeaders()
    {
        var headers = _httpContextAccessor.HttpContext?.Request.Headers;
        if (headers?.ContainsKey(ModuleConstants.Headers.BuyerUserId) == true ||
            headers?.ContainsKey(ModuleConstants.Headers.BuyerOrganizationId) == true)
        {
            throw CreateException(
                ModuleConstants.ErrorCodes.BuyerContextMismatch,
                "X-Buyer-* headers are not accepted. Use Platform OAuth or an anonymous buyer_id.",
                StatusCodes.Status403Forbidden);
        }
    }

    private string ResolveClaim(
        IEnumerable<ClaimsIdentity> identities,
        IEnumerable<string> claimTypes,
        string description,
        bool required)
    {
        var claimTypeSet = claimTypes.ToHashSet(StringComparer.Ordinal);
        var values = (identities ?? [])
            .SelectMany(identity => identity.Claims)
            .Where(claim => claimTypeSet.Contains(claim.Type))
            .Select(claim => claim.Value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (values.Length > 1 || required && values.Length == 0)
        {
            throw CreateException(
                ModuleConstants.ErrorCodes.BuyerContextMismatch,
                $"Authenticated Platform principal contains an invalid {description}.",
                StatusCodes.Status403Forbidden);
        }

        return values.FirstOrDefault();
    }

    private UcpException CreateException(string code, string message, int statusCode = StatusCodes.Status400BadRequest)
    {
        var exception = new UcpException(code, message, statusCode);
        exception.Error.CorrelationId = _httpContextAccessor.HttpContext?.TraceIdentifier;
        return exception;
    }

    private static string ResolveRequestedValue(IEnumerable<string> values, string fieldName, StringComparer comparer)
    {
        var distinctValues = (values ?? [])
            .Select(value => value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(comparer)
            .ToArray();

        if (distinctValues.Length > 1)
        {
            throw new UcpException(ModuleConstants.ErrorCodes.InvalidRequest, $"Conflicting {fieldName} values were supplied.");
        }

        return distinctValues.FirstOrDefault();
    }

    private static bool Matches(string requestedValue, string authenticatedValue)
    {
        return string.IsNullOrWhiteSpace(requestedValue) ||
            string.Equals(requestedValue, authenticatedValue, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAnonymousBuyerId(string value)
    {
        return value?.StartsWith(_anonymousBuyerPrefix, StringComparison.Ordinal) == true &&
            Guid.TryParseExact(value[_anonymousBuyerPrefix.Length..], "N", out _);
    }
}
