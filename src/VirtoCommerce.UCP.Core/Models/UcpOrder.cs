using System.Collections.Generic;
using Newtonsoft.Json;

namespace VirtoCommerce.UCP.Core.Models;

public class UcpOrder
{
    [JsonProperty("id")]
    public string Id { get; set; }

    [JsonProperty("number")]
    public string Number { get; set; }

    [JsonProperty("status")]
    public string Status { get; set; }

    [JsonProperty("status_display_value")]
    public string StatusDisplayValue { get; set; }

    [JsonProperty("created_at")]
    public string CreatedAt { get; set; }

    [JsonProperty("cart_id")]
    public string CartId { get; set; }

    [JsonProperty("parent_order_id")]
    public string ParentOrderId { get; set; }

    [JsonProperty("store_id")]
    public string StoreId { get; set; }

    [JsonProperty("buyer_id")]
    public string BuyerId { get; set; }

    [JsonProperty("customer_name")]
    public string CustomerName { get; set; }

    [JsonProperty("currency")]
    public string Currency { get; set; }

    [JsonProperty("totals")]
    public UcpOrderTotals Totals { get; set; }

    [JsonProperty("line_items")]
    public IList<UcpOrderLineItem> LineItems { get; set; } = new List<UcpOrderLineItem>();

    [JsonProperty("shipments")]
    public IList<UcpOrderShipment> Shipments { get; set; } = new List<UcpOrderShipment>();

    [JsonProperty("payments")]
    public IList<UcpOrderPayment> Payments { get; set; } = new List<UcpOrderPayment>();

    [JsonProperty("messages")]
    public IList<UcpMessage> Messages { get; set; } = new List<UcpMessage>();
}
