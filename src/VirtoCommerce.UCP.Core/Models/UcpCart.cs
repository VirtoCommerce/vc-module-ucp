using System.Collections.Generic;
using Newtonsoft.Json;

namespace VirtoCommerce.UCP.Core.Models;

public class UcpCart
{
    [JsonProperty("id")]
    public string Id { get; set; }

    [JsonProperty("status")]
    public string Status { get; set; }

    [JsonProperty("store_id")]
    public string StoreId { get; set; }

    [JsonProperty("currency")]
    public string Currency { get; set; }

    [JsonProperty("cart_name")]
    public string CartName { get; set; }

    [JsonProperty("cart_type")]
    public string CartType { get; set; }

    [JsonProperty("buyer_id")]
    public string BuyerId { get; set; }

    [JsonProperty("organization_id")]
    public string OrganizationId { get; set; }

    [JsonProperty("line_items")]
    public IList<UcpCartLineItem> LineItems { get; set; } = new List<UcpCartLineItem>();

    [JsonProperty("totals")]
    public UcpCartTotals Totals { get; set; }

    [JsonProperty("coupons")]
    public IList<UcpCartCoupon> Coupons { get; set; } = new List<UcpCartCoupon>();

    [JsonProperty("addresses")]
    public IList<UcpCartAddress> Addresses { get; set; } = new List<UcpCartAddress>();

    [JsonProperty("shipments")]
    public IList<UcpCartShipment> Shipments { get; set; } = new List<UcpCartShipment>();

    [JsonProperty("payments")]
    public IList<UcpCartPayment> Payments { get; set; } = new List<UcpCartPayment>();

    [JsonProperty("continue_url")]
    public string ContinueUrl { get; set; }

    [JsonProperty("messages")]
    public IList<UcpMessage> Messages { get; set; } = new List<UcpMessage>();

    [JsonProperty("inventory_errors")]
    public IList<UcpError> InventoryErrors { get; set; } = new List<UcpError>();
}
