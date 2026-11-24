using Newtonsoft.Json;

namespace VirtoCommerce.UCP.Core.Models;

public class UcpOrderTotals
{
    [JsonProperty("subtotal")]
    public UcpMoney Subtotal { get; set; }

    [JsonProperty("total")]
    public UcpMoney Total { get; set; }

    [JsonProperty("tax_total")]
    public UcpMoney TaxTotal { get; set; }

    [JsonProperty("discount_total")]
    public UcpMoney DiscountTotal { get; set; }

    [JsonProperty("shipping_subtotal")]
    public UcpMoney ShippingSubtotal { get; set; }

    [JsonProperty("shipping_total")]
    public UcpMoney ShippingTotal { get; set; }

    // The sum of the order's approved, not cancelled payments.
    [JsonProperty("paid_total")]
    public UcpMoney PaidTotal { get; set; }

    // What is left to pay: the total less paid_total, never below zero; zero for a cancelled order.
    [JsonProperty("amount_due")]
    public UcpMoney AmountDue { get; set; }
}
