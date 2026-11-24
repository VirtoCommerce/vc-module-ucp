using System;
using Microsoft.AspNetCore.Mvc;
using VirtoCommerce.UCP.Core.Models;

namespace VirtoCommerce.UCP.Web.Models;

public sealed class UcpOrderListQuery
{
    [FromQuery(Name = "store_id")]
    public string StoreId { get; set; }

    [FromQuery(Name = "culture_name")]
    public string CultureName { get; set; }

    [FromQuery(Name = "status")]
    public string[] Statuses { get; set; }

    [FromQuery(Name = "unpaid_only")]
    public bool UnpaidOnly { get; set; }

    [FromQuery(Name = "paid_only")]
    public bool PaidOnly { get; set; }

    [FromQuery(Name = "placed_after")]
    public DateTimeOffset? PlacedAfter { get; set; }

    [FromQuery(Name = "placed_before")]
    public DateTimeOffset? PlacedBefore { get; set; }

    [FromQuery(Name = "cursor")]
    public string Cursor { get; set; }

    [FromQuery(Name = "limit")]
    public int? Limit { get; set; }

    public UcpOrderListRequest ToRequest()
    {
        return new UcpOrderListRequest
        {
            Context = new UcpCartContext
            {
                StoreId = StoreId,
                Language = CultureName,
            },
            Statuses = Statuses ?? [],
            UnpaidOnly = UnpaidOnly,
            PaidOnly = PaidOnly,
            PlacedAfter = PlacedAfter,
            PlacedBefore = PlacedBefore,
            Pagination = new UcpPaginationRequest
            {
                Cursor = Cursor,
                Limit = Limit,
            },
        };
    }
}
