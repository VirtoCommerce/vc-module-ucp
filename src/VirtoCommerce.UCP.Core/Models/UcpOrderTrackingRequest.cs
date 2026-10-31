using System;
using Newtonsoft.Json;

namespace VirtoCommerce.UCP.Core.Models;

public class UcpOrderTrackingRequest
{
    [JsonProperty("context")]
    public UcpCartContext Context { get; set; }

    [JsonProperty("order_id")]
    public string OrderId { get; set; }

    [JsonProperty("order_number")]
    public string OrderNumber { get; set; }

    [JsonProperty("cart_id")]
    public string CartId { get; set; }

    [JsonProperty("placed_after")]
    public DateTimeOffset? PlacedAfter { get; set; }
}
