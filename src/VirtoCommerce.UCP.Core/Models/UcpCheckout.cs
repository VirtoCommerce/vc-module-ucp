using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace VirtoCommerce.UCP.Core.Models;

public class UcpCheckout
{
    [JsonProperty("id")]
    public string Id { get; set; }

    [JsonProperty("cart_id")]
    public string CartId { get; set; }

    [JsonProperty("status")]
    public string Status { get; set; }

    [JsonProperty("cart")]
    public UcpCart Cart { get; set; }

    [JsonProperty("payment_handlers")]
    public IList<UcpPaymentHandlerProfile> PaymentHandlers { get; set; } = new List<UcpPaymentHandlerProfile>();

    [JsonProperty("continue_url")]
    public string ContinueUrl { get; set; }

    [JsonProperty("buyer")]
    public UcpCheckoutBuyer Buyer { get; set; }

    [JsonProperty("shipping_address")]
    public UcpCheckoutAddress ShippingAddress { get; set; }

    [JsonProperty("billing_address")]
    public UcpCheckoutAddress BillingAddress { get; set; }

    [JsonProperty("shipping_method_id")]
    public string ShippingMethodId { get; set; }

    [JsonProperty("payment_handler")]
    public string PaymentHandler { get; set; }

    [JsonProperty("issued_at")]
    public DateTimeOffset? IssuedAt { get; set; }

    [JsonProperty("expires_at")]
    public DateTimeOffset? ExpiresAt { get; set; }

    [JsonProperty("messages")]
    public IList<UcpMessage> Messages { get; set; } = new List<UcpMessage>();
}
