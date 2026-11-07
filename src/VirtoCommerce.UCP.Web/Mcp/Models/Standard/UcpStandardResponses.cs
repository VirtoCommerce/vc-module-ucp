using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace VirtoCommerce.UCP.Web.Mcp.Models.Standard;

// The payloads of the standard UCP MCP binding (2026-08-25), as UcpStandardMapper fills them: source/schemas/ucp.json,
// shopping/{catalog_search,catalog_lookup,cart,checkout,order}.json and their types. Amounts are integer minor units.

public sealed class UcpStandardVersionReference
{
    [JsonPropertyName("version")]
    public string Version { get; set; }
}

public sealed class UcpStandardMetadata
{
    [JsonPropertyName("version")]
    public string Version { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; }

    [JsonPropertyName("capabilities")]
    public IDictionary<string, IList<UcpStandardVersionReference>> Capabilities { get; set; }

    // Checkout responses only; this server advertises no payment handler.
    [JsonPropertyName("payment_handlers")]
    public IDictionary<string, IList<object>> PaymentHandlers { get; set; }
}

public sealed class UcpStandardMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; }

    [JsonPropertyName("code")]
    public string Code { get; set; }

    [JsonPropertyName("path")]
    public string Path { get; set; }

    [JsonPropertyName("content")]
    public string Content { get; set; }

    // Error messages only: recoverable, requires_buyer_input, requires_buyer_review or unrecoverable.
    [JsonPropertyName("severity")]
    public string Severity { get; set; }
}

public sealed class UcpStandardErrorResponse
{
    [JsonPropertyName("ucp")]
    public UcpStandardMetadata Ucp { get; set; }

    [JsonPropertyName("messages")]
    public IList<UcpStandardMessage> Messages { get; set; }

    [JsonPropertyName("continue_url")]
    public string ContinueUrl { get; set; }
}

public sealed class UcpStandardPrice
{
    [JsonPropertyName("amount")]
    public long Amount { get; set; }

    [JsonPropertyName("currency")]
    public string Currency { get; set; }
}

public sealed class UcpStandardPriceRange
{
    [JsonPropertyName("min")]
    public UcpStandardPrice Min { get; set; }

    [JsonPropertyName("max")]
    public UcpStandardPrice Max { get; set; }
}

public sealed class UcpStandardDescription
{
    [JsonPropertyName("plain")]
    public string Plain { get; set; }
}

public sealed class UcpStandardMedia
{
    [JsonPropertyName("type")]
    public string Type { get; set; }

    [JsonPropertyName("url")]
    public string Url { get; set; }

    [JsonPropertyName("alt_text")]
    public string AltText { get; set; }
}

public sealed class UcpStandardAvailability
{
    [JsonPropertyName("available")]
    public bool Available { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; }
}

public sealed class UcpStandardInputCorrelation
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("match")]
    public string Match { get; set; }
}

public sealed class UcpStandardVariant
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("sku")]
    public string Sku { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; }

    [JsonPropertyName("description")]
    public UcpStandardDescription Description { get; set; }

    [JsonPropertyName("price")]
    public UcpStandardPrice Price { get; set; }

    [JsonPropertyName("list_price")]
    public UcpStandardPrice ListPrice { get; set; }

    [JsonPropertyName("availability")]
    public UcpStandardAvailability Availability { get; set; }

    [JsonPropertyName("media")]
    public IList<UcpStandardMedia> Media { get; set; }

    // lookup_catalog only: the request identifiers that resolved to this variant.
    [JsonPropertyName("inputs")]
    public IList<UcpStandardInputCorrelation> Inputs { get; set; }
}

public sealed class UcpStandardProduct
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("handle")]
    public string Handle { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; }

    [JsonPropertyName("description")]
    public UcpStandardDescription Description { get; set; }

    [JsonPropertyName("price_range")]
    public UcpStandardPriceRange PriceRange { get; set; }

    [JsonPropertyName("list_price_range")]
    public UcpStandardPriceRange ListPriceRange { get; set; }

    [JsonPropertyName("media")]
    public IList<UcpStandardMedia> Media { get; set; }

    [JsonPropertyName("variants")]
    public IList<UcpStandardVariant> Variants { get; set; }

    [JsonPropertyName("metadata")]
    public IDictionary<string, object> Metadata { get; set; }
}

public sealed class UcpStandardPagination
{
    [JsonPropertyName("cursor")]
    public string Cursor { get; set; }

    [JsonPropertyName("has_next_page")]
    public bool HasNextPage { get; set; }

    [JsonPropertyName("total_count")]
    public int? TotalCount { get; set; }
}

public sealed class UcpStandardCatalogSearchResponse
{
    [JsonPropertyName("ucp")]
    public UcpStandardMetadata Ucp { get; set; }

    [JsonPropertyName("products")]
    public IList<UcpStandardProduct> Products { get; set; }

    [JsonPropertyName("pagination")]
    public UcpStandardPagination Pagination { get; set; }

    [JsonPropertyName("messages")]
    public IList<UcpStandardMessage> Messages { get; set; }
}

public sealed class UcpStandardCatalogLookupResponse
{
    [JsonPropertyName("ucp")]
    public UcpStandardMetadata Ucp { get; set; }

    [JsonPropertyName("products")]
    public IList<UcpStandardProduct> Products { get; set; }

    [JsonPropertyName("messages")]
    public IList<UcpStandardMessage> Messages { get; set; }
}

public sealed class UcpStandardGetProductResponse
{
    [JsonPropertyName("ucp")]
    public UcpStandardMetadata Ucp { get; set; }

    [JsonPropertyName("product")]
    public UcpStandardProduct Product { get; set; }

    [JsonPropertyName("messages")]
    public IList<UcpStandardMessage> Messages { get; set; }
}

public sealed class UcpStandardTotal
{
    [JsonPropertyName("type")]
    public string Type { get; set; }

    [JsonPropertyName("display_text")]
    public string DisplayText { get; set; }

    [JsonPropertyName("amount")]
    public long Amount { get; set; }
}

public sealed class UcpStandardItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; }

    [JsonPropertyName("price")]
    public long Price { get; set; }

    [JsonPropertyName("image_url")]
    public string ImageUrl { get; set; }
}

public sealed class UcpStandardLineItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("item")]
    public UcpStandardItem Item { get; set; }

    [JsonPropertyName("quantity")]
    public int Quantity { get; set; }

    [JsonPropertyName("totals")]
    public IList<UcpStandardTotal> Totals { get; set; }
}

public sealed class UcpStandardLink
{
    [JsonPropertyName("type")]
    public string Type { get; set; }

    [JsonPropertyName("url")]
    public string Url { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; }
}

public sealed class UcpStandardCart
{
    [JsonPropertyName("ucp")]
    public UcpStandardMetadata Ucp { get; set; }

    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("line_items")]
    public IList<UcpStandardLineItem> LineItems { get; set; }

    [JsonPropertyName("currency")]
    public string Currency { get; set; }

    [JsonPropertyName("totals")]
    public IList<UcpStandardTotal> Totals { get; set; }

    [JsonPropertyName("messages")]
    public IList<UcpStandardMessage> Messages { get; set; }

    [JsonPropertyName("continue_url")]
    public string ContinueUrl { get; set; }

    // Not in the standard schema (which allows more members): an anonymous buyer passes it back as buyer_id to continue.
    [JsonPropertyName("buyer_id")]
    public string BuyerId { get; set; }
}

public sealed class UcpStandardCheckout
{
    [JsonPropertyName("ucp")]
    public UcpStandardMetadata Ucp { get; set; }

    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("line_items")]
    public IList<UcpStandardLineItem> LineItems { get; set; }

    [JsonPropertyName("buyer")]
    public UcpStandardBuyer Buyer { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; }

    [JsonPropertyName("currency")]
    public string Currency { get; set; }

    [JsonPropertyName("totals")]
    public IList<UcpStandardTotal> Totals { get; set; }

    [JsonPropertyName("messages")]
    public IList<UcpStandardMessage> Messages { get; set; }

    [JsonPropertyName("links")]
    public IList<UcpStandardLink> Links { get; set; }

    [JsonPropertyName("continue_url")]
    public string ContinueUrl { get; set; }

    [JsonPropertyName("expires_at")]
    public string ExpiresAt { get; set; }

    // Not in the standard schema (which allows more members): an anonymous buyer passes it back as buyer_id to continue.
    [JsonPropertyName("buyer_id")]
    public string BuyerId { get; set; }
}

public sealed class UcpStandardOrderQuantity
{
    [JsonPropertyName("original")]
    public int Original { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("fulfilled")]
    public int Fulfilled { get; set; }
}

public sealed class UcpStandardOrderLineItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("item")]
    public UcpStandardItem Item { get; set; }

    [JsonPropertyName("quantity")]
    public UcpStandardOrderQuantity Quantity { get; set; }

    [JsonPropertyName("totals")]
    public IList<UcpStandardTotal> Totals { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; }
}

public sealed class UcpStandardLineItemQuantity
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("quantity")]
    public int Quantity { get; set; }
}

public sealed class UcpStandardExpectation
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("line_items")]
    public IList<UcpStandardLineItemQuantity> LineItems { get; set; }

    [JsonPropertyName("method_type")]
    public string MethodType { get; set; }

    [JsonPropertyName("destination")]
    public UcpStandardPostalAddress Destination { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; }
}

public sealed class UcpStandardFulfillmentEvent
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("occurred_at")]
    public string OccurredAt { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; }

    [JsonPropertyName("line_items")]
    public IList<UcpStandardLineItemQuantity> LineItems { get; set; }

    [JsonPropertyName("tracking_number")]
    public string TrackingNumber { get; set; }

    [JsonPropertyName("tracking_url")]
    public string TrackingUrl { get; set; }
}

public sealed class UcpStandardOrderFulfillment
{
    [JsonPropertyName("expectations")]
    public IList<UcpStandardExpectation> Expectations { get; set; }

    [JsonPropertyName("events")]
    public IList<UcpStandardFulfillmentEvent> Events { get; set; }
}

public sealed class UcpStandardOrder
{
    [JsonPropertyName("ucp")]
    public UcpStandardMetadata Ucp { get; set; }

    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("label")]
    public string Label { get; set; }

    [JsonPropertyName("checkout_id")]
    public string CheckoutId { get; set; }

    [JsonPropertyName("permalink_url")]
    public string PermalinkUrl { get; set; }

    [JsonPropertyName("line_items")]
    public IList<UcpStandardOrderLineItem> LineItems { get; set; }

    [JsonPropertyName("fulfillment")]
    public UcpStandardOrderFulfillment Fulfillment { get; set; }

    [JsonPropertyName("currency")]
    public string Currency { get; set; }

    [JsonPropertyName("totals")]
    public IList<UcpStandardTotal> Totals { get; set; }

    [JsonPropertyName("messages")]
    public IList<UcpStandardMessage> Messages { get; set; }
}
