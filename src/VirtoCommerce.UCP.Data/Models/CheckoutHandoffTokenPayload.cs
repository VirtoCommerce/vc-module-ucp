using System;
using VirtoCommerce.UCP.Core.Models;

namespace VirtoCommerce.UCP.Data.Models;

internal sealed class CheckoutHandoffTokenPayload
{
    public string CheckoutId { get; set; }
    public string CartId { get; set; }
    public string StoreId { get; set; }
    public string Currency { get; set; }
    public string CultureName { get; set; }
    public string BuyerId { get; set; }
    public string OrganizationId { get; set; }
    public bool RequiresAuthentication { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public UcpCheckoutBuyer Buyer { get; set; }
    public UcpCheckoutAddress ShippingAddress { get; set; }
    public UcpCheckoutAddress BillingAddress { get; set; }
    public string ShippingMethodId { get; set; }
    public string PaymentHandler { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
