using System.Collections.Generic;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using VirtoCommerce.MCP.Core.Attributes;

namespace VirtoCommerce.UCP.Web.Mcp.Models.Standard;

// The arguments of the standard UCP MCP binding (source/services/shopping/mcp.openrpc.json, 2026-08-25). Only the members
// this server reads are modeled; the binding allows more. A member that carries buyer data is [McpSensitive], so the audit
// masks it; buyer data the model does not declare is not read and not masked by path, so the models declare it.

[Description("UCP request metadata. ucp-agent.profile is the agent's UCP profile URL; it is accepted but not fetched (no capability negotiation).")]
public sealed class UcpStandardMeta
{
    [JsonPropertyName("ucp-agent")]
    public UcpStandardAgent UcpAgent { get; set; }

    [JsonPropertyName("idempotency-key")]
    public string IdempotencyKey { get; set; }
}

public sealed class UcpStandardAgent
{
    [JsonPropertyName("profile")]
    public string Profile { get; set; }
}

public sealed class UcpStandardContext
{
    [JsonPropertyName("address_country")]
    public string AddressCountry { get; set; }

    [JsonPropertyName("address_region")]
    public string AddressRegion { get; set; }

    [McpSensitive]
    [JsonPropertyName("postal_code")]
    public string PostalCode { get; set; }

    [JsonPropertyName("language")]
    public string Language { get; set; }

    [JsonPropertyName("currency")]
    public string Currency { get; set; }

    [JsonPropertyName("intent")]
    public string Intent { get; set; }
}

public sealed class UcpStandardPriceFilter
{
    [JsonPropertyName("min")]
    public long? Min { get; set; }

    [JsonPropertyName("max")]
    public long? Max { get; set; }
}

public sealed class UcpStandardSearchFilters
{
    [JsonPropertyName("categories")]
    public IList<string> Categories { get; set; }

    [JsonPropertyName("price")]
    public UcpStandardPriceFilter Price { get; set; }
}

public sealed class UcpStandardPaginationRequest
{
    [JsonPropertyName("cursor")]
    public string Cursor { get; set; }

    [JsonPropertyName("limit")]
    public int? Limit { get; set; }
}

[Description("Standard catalog search request: query, context, filters (price in minor units, categories), pagination.")]
public sealed class UcpStandardCatalogSearchRequest
{
    [JsonPropertyName("query")]
    public string Query { get; set; }

    [JsonPropertyName("context")]
    public UcpStandardContext Context { get; set; }

    [JsonPropertyName("filters")]
    public UcpStandardSearchFilters Filters { get; set; }

    [JsonPropertyName("pagination")]
    public UcpStandardPaginationRequest Pagination { get; set; }
}

[Description("Standard catalog lookup request: ids (product or variant ids) and context.")]
public sealed class UcpStandardCatalogLookupRequest
{
    [JsonPropertyName("ids")]
    public IList<string> Ids { get; set; }

    [JsonPropertyName("filters")]
    public UcpStandardSearchFilters Filters { get; set; }

    [JsonPropertyName("context")]
    public UcpStandardContext Context { get; set; }
}

[Description("Standard get_product request: id (product or variant id) and context.")]
public sealed class UcpStandardGetProductRequest
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("filters")]
    public UcpStandardSearchFilters Filters { get; set; }

    [JsonPropertyName("context")]
    public UcpStandardContext Context { get; set; }
}

public sealed class UcpStandardItemRequest
{
    [JsonPropertyName("id")]
    public string Id { get; set; }
}

public sealed class UcpStandardLineItemRequest
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("item")]
    public UcpStandardItemRequest Item { get; set; }

    [JsonPropertyName("quantity")]
    public int Quantity { get; set; }
}

public sealed class UcpStandardBuyer
{
    [JsonPropertyName("first_name")]
    public string FirstName { get; set; }

    [JsonPropertyName("last_name")]
    public string LastName { get; set; }

    [JsonPropertyName("email")]
    public string Email { get; set; }

    [JsonPropertyName("phone_number")]
    public string PhoneNumber { get; set; }
}

public sealed class UcpStandardPostalAddress
{
    [JsonPropertyName("id")]
    public string Id { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; }

    [JsonPropertyName("first_name")]
    public string FirstName { get; set; }

    [JsonPropertyName("last_name")]
    public string LastName { get; set; }

    [JsonPropertyName("street_address")]
    public string StreetAddress { get; set; }

    [JsonPropertyName("extended_address")]
    public string ExtendedAddress { get; set; }

    [JsonPropertyName("address_locality")]
    public string AddressLocality { get; set; }

    [JsonPropertyName("address_region")]
    public string AddressRegion { get; set; }

    [JsonPropertyName("address_country")]
    public string AddressCountry { get; set; }

    [JsonPropertyName("postal_code")]
    public string PostalCode { get; set; }

    [JsonPropertyName("phone_number")]
    public string PhoneNumber { get; set; }
}

public sealed class UcpStandardFulfillmentMethodRequest
{
    [JsonPropertyName("type")]
    public string Type { get; set; }

    [JsonPropertyName("destinations")]
    public IList<UcpStandardPostalAddress> Destinations { get; set; }
}

public sealed class UcpStandardFulfillmentRequest
{
    [JsonPropertyName("methods")]
    public IList<UcpStandardFulfillmentMethodRequest> Methods { get; set; }
}

[Description("Standard cart: line_items ({item: {id}, quantity}; on update the complete desired state), context, buyer.")]
public sealed class UcpStandardCartRequest
{
    [JsonPropertyName("line_items")]
    public IList<UcpStandardLineItemRequest> LineItems { get; set; }

    [JsonPropertyName("context")]
    public UcpStandardContext Context { get; set; }

    [McpSensitive]
    [JsonPropertyName("buyer")]
    public UcpStandardBuyer Buyer { get; set; }
}

[Description(
    "Standard checkout: cart_id (an existing cart) or line_items ({item: {id}, quantity}), buyer, context, and " +
    "fulfillment.methods[].destinations[] (the first shipping destination becomes the shipping address).")]
public sealed class UcpStandardCheckoutRequest
{
    [JsonPropertyName("cart_id")]
    public string CartId { get; set; }

    [JsonPropertyName("line_items")]
    public IList<UcpStandardLineItemRequest> LineItems { get; set; }

    [McpSensitive]
    [JsonPropertyName("buyer")]
    public UcpStandardBuyer Buyer { get; set; }

    [JsonPropertyName("context")]
    public UcpStandardContext Context { get; set; }

    [McpSensitive]
    [JsonPropertyName("fulfillment")]
    public UcpStandardFulfillmentRequest Fulfillment { get; set; }

    // Payment is not taken through this API; the member is declared so the audit masks it.
    [McpSensitive]
    [JsonPropertyName("payment")]
    public JsonElement? Payment { get; set; }
}
