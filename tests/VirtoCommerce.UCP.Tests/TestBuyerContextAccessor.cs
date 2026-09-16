using System.Linq;
using System.Security.Claims;
using VirtoCommerce.UCP.Core.Models;
using VirtoCommerce.UCP.Core.Services;

namespace VirtoCommerce.UCP.Tests;

internal sealed class TestBuyerContextAccessor : IUcpBuyerContextAccessor
{
    private readonly string _defaultBuyerId;

    public TestBuyerContextAccessor(string defaultBuyerId = null)
    {
        _defaultBuyerId = defaultBuyerId;
    }

    public UcpBuyerContext Resolve(UcpBuyerContextRequest request = null)
    {
        request ??= new UcpBuyerContextRequest();
        var buyerId = request.RequestedBuyerIds?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        var organizationId = request.RequestedOrganizationIds?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (string.IsNullOrWhiteSpace(buyerId) && !string.IsNullOrWhiteSpace(_defaultBuyerId) &&
            (request.CreateAnonymousBuyer || request.RequireBuyer))
        {
            buyerId = _defaultBuyerId;
        }
        else if (string.IsNullOrWhiteSpace(buyerId) && request.CreateAnonymousBuyer)
        {
            buyerId = "ucp-anonymous-11111111111111111111111111111111";
        }

        var identity = new ClaimsIdentity();
        if (!string.IsNullOrWhiteSpace(buyerId))
        {
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, buyerId));
            identity.AddClaim(new Claim("sub", buyerId));
        }

        return new UcpBuyerContext
        {
            UserId = buyerId,
            PublicBuyerId = buyerId,
            OrganizationId = organizationId,
            Principal = new ClaimsPrincipal(identity),
            IsAuthenticated = false,
        };
    }
}
