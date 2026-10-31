using System;

namespace VirtoCommerce.UCP.Data.Models;

internal sealed class OrderExecutionRequest
{
    public string OrderId { get; set; }
    public string OrderNumber { get; set; }
    public string CartId { get; set; }
    public DateTimeOffset? PlacedAfter { get; set; }
    public string CultureName { get; set; }
    public string UserId { get; set; }
    public string OrganizationId { get; set; }
}
