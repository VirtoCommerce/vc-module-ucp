using System.Collections.Generic;
using Newtonsoft.Json;

namespace VirtoCommerce.UCP.Core.Models;

public class UcpCartLineItem
{
    [JsonProperty("id")]
    public string Id { get; set; }

    [JsonProperty("product_id")]
    public string ProductId { get; set; }

    [JsonProperty("sku")]
    public string Sku { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; }

    [JsonProperty("image_url")]
    public string ImageUrl { get; set; }

    [JsonProperty("quantity")]
    public int Quantity { get; set; }

    [JsonProperty("requested_quantity")]
    public long? RequestedQuantity { get; set; }

    [JsonProperty("available_quantity")]
    public long? AvailableQuantity { get; set; }

    [JsonProperty("inventory_status")]
    public string InventoryStatus { get; set; }

    [JsonProperty("unit_price")]
    public UcpMoney UnitPrice { get; set; }

    [JsonProperty("list_price")]
    public UcpMoney ListPrice { get; set; }

    [JsonProperty("line_total")]
    public UcpMoney LineTotal { get; set; }

    [JsonProperty("discount_total")]
    public UcpMoney DiscountTotal { get; set; }

    [JsonProperty("tax_total")]
    public UcpMoney TaxTotal { get; set; }

    [JsonProperty("messages")]
    public IList<UcpMessage> Messages { get; set; } = new List<UcpMessage>();
}
