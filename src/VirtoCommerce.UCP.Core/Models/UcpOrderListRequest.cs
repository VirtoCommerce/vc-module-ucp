using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace VirtoCommerce.UCP.Core.Models;

public class UcpOrderListRequest
{
    [JsonProperty("context")]
    public UcpCartContext Context { get; set; }

    // Order statuses to keep, matched as the platform stores them (for example New, Processing, Completed). Empty keeps all.
    [JsonProperty("statuses")]
    public IList<string> Statuses { get; set; } = new List<string>();

    // Keeps only orders that still have an amount due: not cancelled, and approved payments cover less than the total.
    [JsonProperty("unpaid_only")]
    public bool UnpaidOnly { get; set; }

    [JsonProperty("placed_after")]
    public DateTimeOffset? PlacedAfter { get; set; }

    [JsonProperty("placed_before")]
    public DateTimeOffset? PlacedBefore { get; set; }

    [JsonProperty("pagination")]
    public UcpPaginationRequest Pagination { get; set; }
}
