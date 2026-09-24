using System.Security.Claims;

namespace VirtoCommerce.UCP.Core.Models;

public sealed class UcpBuyerContext
{
    public string UserId { get; init; }

    public string PublicBuyerId { get; init; }

    public string SourceAnonymousBuyerId { get; init; }

    public string OrganizationId { get; init; }

    public string AgentId { get; init; }

    public string CorrelationId { get; init; }

    public ClaimsPrincipal Principal { get; init; }

    public bool IsAuthenticated { get; init; }
}
