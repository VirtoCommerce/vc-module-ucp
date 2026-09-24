using System.Collections.Generic;

namespace VirtoCommerce.UCP.Core.Models;

public sealed class UcpBuyerContextRequest
{
    public IEnumerable<string> RequestedBuyerIds { get; init; }

    public IEnumerable<string> RequestedOrganizationIds { get; init; }

    public bool CreateAnonymousBuyer { get; init; }

    public bool RequireBuyer { get; init; }

    public bool RequireAuthenticatedBuyer { get; init; }
}
