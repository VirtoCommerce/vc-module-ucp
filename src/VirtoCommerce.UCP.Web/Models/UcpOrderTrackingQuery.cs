using System;
using Microsoft.AspNetCore.Mvc;
using VirtoCommerce.UCP.Core.Models;

namespace VirtoCommerce.UCP.Web.Models;

public sealed class UcpOrderTrackingQuery
{
    [FromQuery(Name = "order_id")]
    public string OrderId { get; set; }

    [FromQuery(Name = "order_number")]
    public string OrderNumber { get; set; }

    [FromQuery(Name = "cart_id")]
    public string CartId { get; set; }

    [FromQuery(Name = "placed_after")]
    public DateTimeOffset? PlacedAfter { get; set; }

    [FromQuery(Name = "buyer_id")]
    public string BuyerId { get; set; }

    [FromQuery(Name = "organization_id")]
    public string OrganizationId { get; set; }

    [FromQuery(Name = "culture_name")]
    public string CultureName { get; set; }

    public UcpOrderTrackingRequest ToRequest()
    {
        return new UcpOrderTrackingRequest
        {
            OrderId = OrderId,
            OrderNumber = OrderNumber,
            CartId = CartId,
            PlacedAfter = PlacedAfter,
            Context = new UcpCartContext
            {
                BuyerId = BuyerId,
                OrganizationId = OrganizationId,
                Language = CultureName,
            },
        };
    }
}
