using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Protocol;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Models;
using VirtoCommerce.UCP.Core.Services;
using VirtoCommerce.UCP.Web.Mcp.Models.Standard;

namespace VirtoCommerce.UCP.Web.Mcp;

/// <summary>
/// Maps between this module's UCP models and the payloads of the standard UCP MCP binding (2026-08-25): the tool
/// arguments to our requests, and our responses to the standard catalog, cart, checkout, order and error payloads.
/// Registered as a singleton; a module that needs another mapping registers a subclass as <see cref="UcpStandardMapper"/>.
/// </summary>
public class UcpStandardMapper
{
    public const string StatusSuccess = "success";
    public const string StatusError = "error";

    public const string CheckoutIncomplete = "incomplete";
    public const string CheckoutRequiresEscalation = "requires_escalation";
    public const string CheckoutReadyForComplete = "ready_for_complete";

    public const string SeverityRecoverable = "recoverable";
    public const string SeverityRequiresBuyerInput = "requires_buyer_input";
    public const string SeverityRequiresBuyerReview = "requires_buyer_review";
    public const string SeverityUnrecoverable = "unrecoverable";

    public const string NotFoundCode = "not_found";
    public const string HostedCheckoutRequiredCode = "hosted_checkout_required";

    public const string HostedCheckoutRequiredMessage =
        "Payment is not taken through this API: the buyer reviews the order, pays and places it in the storefront at continue_url. " +
        "This status does not mean that an approval rule applies.";

    // Our messages that point the agent to our own handoff tools; a standard checkout says the same with its status.
    private static readonly HashSet<string> _handoffMessageCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "handoff_required",
        "checkout_updated",
        HostedCheckoutRequiredCode,
    };

    // Our checkout warnings about a shipping address the hosted checkout needs; the platform resolves them in band.
    private static readonly HashSet<string> _addressRequirementCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "shipping_address_missing",
        "shipping_postal_code_missing",
    };

    // The XCart validation codes that UcpInventoryErrorNormalizer turns into cart.inventory_errors.
    private static readonly HashSet<string> _inventorySourceCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "PRODUCT_FFC_QTY",
        "PRODUCT_QTY_CHANGED",
        "PRODUCT_QTY_INSUFFICIENT",
        "PRODUCT_MIN_QTY_NOT_AVAILABLE",
    };

    private static readonly HashSet<string> _notFoundCodes = new(StringComparer.Ordinal)
    {
        ModuleConstants.ErrorCodes.ProductNotFound,
        ModuleConstants.ErrorCodes.CartNotFound,
        ModuleConstants.ErrorCodes.OrderNotFound,
    };

    private static readonly HashSet<string> _standardSeverities = new(StringComparer.Ordinal)
    {
        SeverityRecoverable,
        SeverityRequiresBuyerInput,
        SeverityRequiresBuyerReview,
        SeverityUnrecoverable,
    };

    /// <summary>The standard payload in structuredContent, and the same JSON as text for clients without structured content.</summary>
    public virtual CallToolResult CreateResult(object payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var structuredContent = JsonSerializer.SerializeToElement(payload, payload.GetType(), UcpMcpSerialization.Options);

        return new CallToolResult
        {
            StructuredContent = structuredContent,
            Content =
            [
                new TextContentBlock { Text = structuredContent.GetRawText() },
            ],
        };
    }

    public virtual CallToolResult CreateErrorResult(UcpException exception, string capability)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var result = CreateResult(MapError(exception, capability));
        result.IsError = true;
        result.Meta = new JsonObject
        {
            ["source"] = "ucp",
            ["error_code"] = UcpMcpErrorResultFactory.GetSafeErrorCode(exception.Code),
            ["status_code"] = exception.StatusCode,
        };

        return result;
    }

    public virtual UcpStandardMetadata CreateMetadata(string capability, string status = StatusSuccess)
    {
        var metadata = new UcpStandardMetadata
        {
            Version = ModuleConstants.DiscoveryVersion,
            Status = status,
            Capabilities = new Dictionary<string, IList<UcpStandardVersionReference>>(StringComparer.Ordinal),
        };

        if (!string.IsNullOrWhiteSpace(capability))
        {
            metadata.Capabilities[capability] = [new UcpStandardVersionReference { Version = ModuleConstants.DiscoveryVersion }];
        }

        // A checkout response declares the payment handlers; this server takes no payment, so it has none.
        if (status == StatusSuccess && capability == ModuleConstants.Discovery.Checkout)
        {
            metadata.PaymentHandlers = new Dictionary<string, IList<object>>(StringComparer.Ordinal);
        }

        return metadata;
    }

    public virtual UcpStandardErrorResponse MapError(UcpException exception, string capability)
    {
        return new UcpStandardErrorResponse
        {
            Ucp = CreateMetadata(capability, StatusError),
            Messages =
            [
                new UcpStandardMessage
                {
                    Type = "error",
                    Code = MapErrorCode(exception.Code),
                    Content = FirstNotEmpty(exception.Message, "The UCP operation failed."),
                    Severity = MapErrorSeverity(exception),
                },
            ],
        };
    }

    /// <summary>Our error code, except the not-found codes, which become the standard not_found.</summary>
    protected virtual string MapErrorCode(string code)
    {
        return code != null && _notFoundCodes.Contains(code) ? NotFoundCode : UcpMcpErrorResultFactory.GetSafeErrorCode(code);
    }

    protected virtual string MapErrorSeverity(UcpException exception)
    {
        if (exception.Code != null && _notFoundCodes.Contains(exception.Code))
        {
            return SeverityUnrecoverable;
        }

        // 405: the operation is not supported for the resource; 5xx: nothing the platform can change in its request.
        return exception.StatusCode is StatusCodes.Status405MethodNotAllowed or >= StatusCodes.Status500InternalServerError
            ? SeverityUnrecoverable
            : SeverityRecoverable;
    }

    public virtual IList<UcpStandardMessage> MapMessages(IEnumerable<UcpMessage> messages, string path = null)
    {
        return (messages ?? [])
            .Select(message => MapMessage(message, path))
            .Where(message => message != null)
            .ToList();
    }

    public virtual UcpStandardMessage MapMessage(UcpMessage message, string path = null)
    {
        if (message == null || string.IsNullOrWhiteSpace(message.Content))
        {
            return null;
        }

        var type = message.Type?.ToLowerInvariant();
        return type switch
        {
            "error" => new UcpStandardMessage
            {
                Type = "error",
                Code = FirstNotEmpty(message.Code, "error"),
                Path = path,
                Content = message.Content,
                Severity = message.Severity != null && _standardSeverities.Contains(message.Severity) ? message.Severity : SeverityRecoverable,
            },
            "warning" => new UcpStandardMessage
            {
                Type = "warning",
                Code = FirstNotEmpty(message.Code, "warning"),
                Path = path,
                Content = message.Content,
            },
            _ => new UcpStandardMessage
            {
                Type = "info",
                Code = NullIfWhiteSpace(message.Code),
                Path = path,
                Content = message.Content,
            },
        };
    }

    public virtual UcpStandardCatalogSearchResponse MapSearchResponse(UcpCatalogSearchResponse response, string fallbackCurrency)
    {
        return new UcpStandardCatalogSearchResponse
        {
            Ucp = CreateMetadata(ModuleConstants.Discovery.CatalogSearch),
            Products = (response?.Products ?? [])
                .Where(product => product != null)
                .Select(product => MapProduct(product, fallbackCurrency))
                .ToList(),
            Pagination = MapPagination(response?.Pagination),
            Messages = NullIfEmpty(MapMessages(response?.Messages)),
        };
    }

    public virtual UcpStandardGetProductResponse MapGetProductResponse(UcpProductResponse response, string fallbackCurrency)
    {
        return new UcpStandardGetProductResponse
        {
            Ucp = CreateMetadata(ModuleConstants.Discovery.CatalogLookup),
            Product = MapProduct(response?.Product ?? new UcpProduct(), fallbackCurrency),
            Messages = NullIfEmpty(MapMessages(response?.Messages)),
        };
    }

    /// <summary>
    /// The lookup response: one product per resolved product, each with the variants that the requested identifiers
    /// resolved to and the identifiers in their inputs; unresolved identifiers become info messages with code not_found.
    /// </summary>
    public virtual UcpStandardCatalogLookupResponse MapLookupResponse(
        IEnumerable<KeyValuePair<string, UcpProductResponse>> resolved,
        IEnumerable<string> notFoundIds,
        string fallbackCurrency)
    {
        var products = new List<UcpStandardProduct>();
        var productsById = new Dictionary<string, UcpStandardProduct>(StringComparer.Ordinal);
        var messages = new List<UcpStandardMessage>();
        var notFound = (notFoundIds ?? []).ToList();

        foreach (var (requestedId, response) in resolved ?? [])
        {
            var product = response?.Product;
            if (product == null || string.IsNullOrWhiteSpace(product.Id))
            {
                notFound.Add(requestedId);
                continue;
            }

            if (!productsById.TryGetValue(product.Id, out var mappedProduct))
            {
                mappedProduct = MapProduct(product, fallbackCurrency);
                mappedProduct.Variants = [];
                productsById[product.Id] = mappedProduct;
                products.Add(mappedProduct);
            }

            AddLookupVariant(mappedProduct, product, requestedId, fallbackCurrency);
            AddDistinctMessages(messages, MapMessages(response.Messages));
        }

        messages.AddRange(notFound.Distinct(StringComparer.Ordinal).Select(id => new UcpStandardMessage
        {
            Type = "info",
            Code = NotFoundCode,
            Content = id,
        }));

        return new UcpStandardCatalogLookupResponse
        {
            Ucp = CreateMetadata(ModuleConstants.Discovery.CatalogLookup),
            Products = products,
            Messages = NullIfEmpty(messages),
        };
    }

    protected virtual void AddLookupVariant(UcpStandardProduct mappedProduct, UcpProduct product, string requestedId, string fallbackCurrency)
    {
        var variants = MapVariants(product, fallbackCurrency);
        var exact = variants.FirstOrDefault(variant => string.Equals(variant.Id, requestedId, StringComparison.Ordinal));
        var variant = exact ?? variants[0];
        var input = new UcpStandardInputCorrelation { Id = requestedId, Match = exact != null ? "exact" : "featured" };

        var existing = mappedProduct.Variants.FirstOrDefault(x => string.Equals(x.Id, variant.Id, StringComparison.Ordinal));
        if (existing != null)
        {
            existing.Inputs.Add(input);
            return;
        }

        variant.Inputs = [input];
        mappedProduct.Variants.Add(variant);
    }

    public virtual UcpStandardProduct MapProduct(UcpProduct product, string fallbackCurrency)
    {
        ArgumentNullException.ThrowIfNull(product);

        var title = FirstNotEmpty(product.Name, product.Code, product.Id) ?? string.Empty;
        var variants = MapVariants(product, fallbackCurrency);

        return new UcpStandardProduct
        {
            Id = product.Id,
            Handle = NullIfWhiteSpace(product.Slug),
            Title = title,
            // Our catalog response carries no description text: the title stands in, as the schema requires one.
            Description = new UcpStandardDescription { Plain = title },
            PriceRange = MapPriceRange(variants.Select(variant => variant.Price))
                ?? MapPriceRange([new UcpStandardPrice { Amount = 0, Currency = NormalizeCurrency(fallbackCurrency) }]),
            ListPriceRange = MapPriceRange(variants.Select(variant => variant.ListPrice)),
            Media = MapMedia(product.ImageUrl, title),
            Variants = variants,
            Metadata = MapProductMetadata(product),
        };
    }

    /// <summary>
    /// The purchasable variants: the product itself when it has no variations or is buyable itself, then its variations.
    /// The first one is the featured variant.
    /// </summary>
    protected virtual IList<UcpStandardVariant> MapVariants(UcpProduct product, string fallbackCurrency)
    {
        var variations = (product.Variations ?? []).Where(variation => variation != null).ToList();
        var variants = new List<UcpStandardVariant>();
        if (variations.Count == 0 || product.Availability?.IsBuyable == true)
        {
            variants.Add(MapProductVariant(product, fallbackCurrency));
        }

        variants.AddRange(variations.Select(variation => MapVariation(variation, product, fallbackCurrency)));

        return variants;
    }

    protected virtual UcpStandardVariant MapProductVariant(UcpProduct product, string fallbackCurrency)
    {
        var title = FirstNotEmpty(product.Name, product.Code, product.Id) ?? string.Empty;

        return new UcpStandardVariant
        {
            Id = product.Id,
            Sku = NullIfWhiteSpace(product.Code),
            Title = title,
            Description = new UcpStandardDescription { Plain = title },
            Price = MapPrice(product.Price, fallbackCurrency) ?? ZeroPrice(fallbackCurrency),
            ListPrice = MapPrice(product.ListPrice, fallbackCurrency),
            Availability = MapAvailability(product.Availability),
            Media = MapMedia(product.ImageUrl, title),
        };
    }

    protected virtual UcpStandardVariant MapVariation(UcpProductVariation variation, UcpProduct product, string fallbackCurrency)
    {
        var title = FirstNotEmpty(variation.Name, variation.Code, variation.Id) ?? string.Empty;

        return new UcpStandardVariant
        {
            Id = variation.Id,
            Sku = NullIfWhiteSpace(variation.Code),
            Title = title,
            Description = new UcpStandardDescription { Plain = title },
            Price = MapPrice(variation.Price, fallbackCurrency) ?? MapPrice(product.Price, fallbackCurrency) ?? ZeroPrice(fallbackCurrency),
            Availability = MapAvailability(variation.Availability),
        };
    }

    protected virtual UcpStandardAvailability MapAvailability(UcpProductAvailability availability)
    {
        return availability == null
            ? null
            : new UcpStandardAvailability
            {
                Available = availability.IsAvailable,
                Status = availability.IsInStock ? "in_stock" : "out_of_stock",
            };
    }

    protected virtual IDictionary<string, object> MapProductMetadata(UcpProduct product)
    {
        var metadata = new Dictionary<string, object>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(product.Brand))
        {
            metadata["brand"] = product.Brand;
        }

        if (!string.IsNullOrWhiteSpace(product.ProductType))
        {
            metadata["product_type"] = product.ProductType;
        }

        return metadata.Count == 0 ? null : metadata;
    }

    protected virtual UcpStandardPagination MapPagination(UcpPaginationResponse pagination)
    {
        if (pagination == null)
        {
            return null;
        }

        // A next page needs a cursor to fetch it.
        var hasNextPage = pagination.HasNextPage && !string.IsNullOrWhiteSpace(pagination.Cursor);

        return new UcpStandardPagination
        {
            Cursor = hasNextPage ? pagination.Cursor : null,
            HasNextPage = hasNextPage,
            TotalCount = pagination.TotalCount >= 0 ? pagination.TotalCount : null,
        };
    }

    public virtual UcpStandardCart MapCart(UcpCartResponse response, string fallbackCurrency)
    {
        var cart = response?.Cart ?? new UcpCart();

        return new UcpStandardCart
        {
            Ucp = CreateMetadata(ModuleConstants.Discovery.Cart),
            Id = cart.Id,
            LineItems = MapLineItems(cart.LineItems),
            Currency = NormalizeCurrency(FirstNotEmpty(cart.Currency, fallbackCurrency)),
            Totals = MapTotals(cart.Totals),
            Messages = NullIfEmpty(MapCartMessages(cart, response?.Messages)),
            ContinueUrl = AbsoluteUrlOrNull(cart.ContinueUrl),
            BuyerId = NullIfWhiteSpace(cart.BuyerId),
        };
    }

    /// <summary>
    /// The standard checkout of our checkout snapshot (its id is the cart id). The status: requires_escalation after a
    /// handoff; otherwise ready_for_complete when the hosted checkout has what it needs (no inventory error, and a
    /// shipping address with recipient name and postal code unless the store assigns addresses), else incomplete.
    /// </summary>
    public virtual UcpStandardCheckout MapCheckout(UcpCheckoutResponse response, bool storeManagedAddresses, string fallbackCurrency)
    {
        var checkout = response?.Checkout ?? new UcpCheckout();
        var cart = checkout.Cart ?? new UcpCart();
        var status = MapCheckoutStatus(checkout, storeManagedAddresses);

        return new UcpStandardCheckout
        {
            Ucp = CreateMetadata(ModuleConstants.Discovery.Checkout),
            Id = FirstNotEmpty(checkout.Id, checkout.CartId, cart.Id),
            LineItems = MapLineItems(cart.LineItems),
            Buyer = MapBuyer(checkout.Buyer),
            Status = status,
            Currency = NormalizeCurrency(FirstNotEmpty(cart.Currency, fallbackCurrency)),
            Totals = MapTotals(cart.Totals),
            Messages = NullIfEmpty(MapCheckoutMessages(checkout, response?.Messages, status)),
            Links = MapCheckoutLinks(checkout),
            ContinueUrl = AbsoluteUrlOrNull(status == CheckoutRequiresEscalation
                ? checkout.ContinueUrl
                : FirstNotEmpty(checkout.ContinueUrl, cart.ContinueUrl)),
            ExpiresAt = checkout.ExpiresAt?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            BuyerId = FirstNotEmpty(cart.BuyerId, checkout.Buyer?.Id),
        };
    }

    protected virtual string MapCheckoutStatus(UcpCheckout checkout, bool storeManagedAddresses)
    {
        if (string.Equals(checkout.Status, CheckoutRequiresEscalation, StringComparison.OrdinalIgnoreCase))
        {
            return CheckoutRequiresEscalation;
        }

        if (checkout.Cart?.InventoryErrors?.Count > 0 || checkout.Cart?.LineItems?.Count is null or 0)
        {
            return CheckoutIncomplete;
        }

        return storeManagedAddresses || HasRequiredShippingAddress(checkout.ShippingAddress)
            ? CheckoutReadyForComplete
            : CheckoutIncomplete;
    }

    protected virtual bool HasRequiredShippingAddress(UcpCheckoutAddress address)
    {
        return address != null
            && !string.IsNullOrWhiteSpace(address.FirstName)
            && !string.IsNullOrWhiteSpace(address.LastName)
            && !string.IsNullOrWhiteSpace(address.PostalCode);
    }

    protected virtual IList<UcpStandardMessage> MapCheckoutMessages(UcpCheckout checkout, IList<UcpMessage> responseMessages, string status)
    {
        var sourceMessages = (checkout.Messages ?? [])
            .Concat((responseMessages ?? []).Where(message => checkout.Messages?.Contains(message) != true))
            .Where(message => message?.Code == null || !_handoffMessageCodes.Contains(message.Code));
        var messages = MapCartMessages(checkout.Cart ?? new UcpCart(), null);
        AddDistinctMessages(messages, MapMessages(sourceMessages));

        foreach (var message in messages.Where(message => message.Code != null && _addressRequirementCodes.Contains(message.Code)))
        {
            // The hosted checkout cannot start without it: the platform supplies it with update_checkout.
            message.Type = "error";
            message.Severity = SeverityRecoverable;
        }

        if (status == CheckoutRequiresEscalation)
        {
            messages.Add(new UcpStandardMessage
            {
                Type = "error",
                Code = HostedCheckoutRequiredCode,
                Content = HostedCheckoutRequiredMessage,
                Severity = SeverityRequiresBuyerInput,
            });
        }

        return messages;
    }

    /// <summary>The legal links of a checkout. The store profile has no policy URLs, so there are none.</summary>
    protected virtual IList<UcpStandardLink> MapCheckoutLinks(UcpCheckout checkout)
    {
        return [];
    }

    protected virtual UcpStandardBuyer MapBuyer(UcpCheckoutBuyer buyer)
    {
        if (buyer == null)
        {
            return null;
        }

        var name = buyer.Name?.Trim();
        var separator = string.IsNullOrEmpty(name) ? -1 : name.IndexOf(' ');
        var result = new UcpStandardBuyer
        {
            FirstName = NullIfWhiteSpace(separator < 0 ? name : name[..separator]),
            LastName = separator < 0 ? null : NullIfWhiteSpace(name[(separator + 1)..].Trim()),
            Email = NullIfWhiteSpace(buyer.Email),
            PhoneNumber = NullIfWhiteSpace(buyer.Phone),
        };

        return result.FirstName == null && result.LastName == null && result.Email == null && result.PhoneNumber == null
            ? null
            : result;
    }

    protected virtual IList<UcpStandardMessage> MapCartMessages(UcpCart cart, IList<UcpMessage> responseMessages)
    {
        var lineItems = (cart.LineItems ?? []).Where(IsStandardLineItem).ToList();
        var inventoryErrors = (cart.InventoryErrors ?? []).Where(error => error != null).ToList();
        var cartMessages = (cart.Messages ?? [])
            .Concat((responseMessages ?? []).Where(message => cart.Messages?.Contains(message) != true))
            // The inventory errors below replace the XCart validation errors they come from.
            .Where(message => inventoryErrors.Count == 0 || message?.Code == null || !_inventorySourceCodes.Contains(message.Code));

        var messages = MapMessages(cartMessages);
        for (var index = 0; index < lineItems.Count; index++)
        {
            AddDistinctMessages(messages, MapMessages(
                lineItems[index].Messages?.Where(message => inventoryErrors.Count == 0 || message?.Code == null || !_inventorySourceCodes.Contains(message.Code)),
                $"$.line_items[{index}]"));
        }

        foreach (var error in inventoryErrors)
        {
            var lineItemId = error.Details != null && error.Details.TryGetValue("line_item_id", out var value) ? value as string : null;
            var index = lineItemId == null ? -1 : lineItems.FindIndex(lineItem => lineItem.Id == lineItemId);
            messages.Add(new UcpStandardMessage
            {
                Type = "error",
                Code = UcpMcpErrorResultFactory.GetSafeErrorCode(error.Code),
                Path = index < 0 ? null : $"$.line_items[{index}]",
                Content = FirstNotEmpty(error.Message, error.Code),
                Severity = SeverityRecoverable,
            });
        }

        return messages;
    }

    /// <summary>The lines with a quantity; the standard counts at least one of each.</summary>
    protected virtual IList<UcpStandardLineItem> MapLineItems(IEnumerable<UcpCartLineItem> lineItems)
    {
        return (lineItems ?? [])
            .Where(IsStandardLineItem)
            .Select(MapLineItem)
            .ToList();
    }

    protected virtual bool IsStandardLineItem(UcpCartLineItem lineItem)
    {
        return lineItem != null && lineItem.Quantity > 0;
    }

    protected virtual UcpStandardLineItem MapLineItem(UcpCartLineItem lineItem)
    {
        var price = Math.Max(0, lineItem.UnitPrice?.Amount ?? 0);
        var lineTotal = lineItem.LineTotal?.Amount ?? price * lineItem.Quantity;

        return new UcpStandardLineItem
        {
            Id = FirstNotEmpty(lineItem.Id, lineItem.ProductId),
            Item = new UcpStandardItem
            {
                Id = FirstNotEmpty(lineItem.ProductId, lineItem.Sku),
                Title = FirstNotEmpty(lineItem.Name, lineItem.Sku, lineItem.ProductId) ?? string.Empty,
                Price = price,
                ImageUrl = AbsoluteUrlOrNull(lineItem.ImageUrl),
            },
            Quantity = lineItem.Quantity,
            Totals = MapLineTotals(lineTotal),
        };
    }

    protected virtual IList<UcpStandardTotal> MapTotals(UcpCartTotals totals)
    {
        totals ??= new UcpCartTotals();

        return MapTotals(totals.Subtotal, totals.DiscountTotal, totals.ShippingTotal, totals.TaxTotal, totals.FeeTotal, totals.Total);
    }

    /// <summary>
    /// The totals in the standard order: exactly one subtotal and one total, with discount (negative), fulfillment, tax
    /// and fee between them when they are not zero.
    /// </summary>
    protected virtual IList<UcpStandardTotal> MapTotals(UcpMoney subtotal, UcpMoney discount, UcpMoney fulfillment, UcpMoney tax, UcpMoney fee, UcpMoney total)
    {
        var totals = new List<UcpStandardTotal>
        {
            new() { Type = "subtotal", Amount = Math.Max(0, subtotal?.Amount ?? 0) },
        };

        if (discount?.Amount > 0)
        {
            totals.Add(new UcpStandardTotal { Type = "discount", DisplayText = "Discount", Amount = -discount.Amount });
        }

        AddPositiveTotal(totals, "fulfillment", "Shipping", fulfillment);
        AddPositiveTotal(totals, "tax", "Tax", tax);
        AddPositiveTotal(totals, "fee", "Fees", fee);
        totals.Add(new UcpStandardTotal { Type = "total", Amount = total?.Amount ?? 0 });

        return totals;
    }

    protected virtual IList<UcpStandardTotal> MapLineTotals(long lineTotal)
    {
        return
        [
            new UcpStandardTotal { Type = "subtotal", Amount = Math.Max(0, lineTotal) },
            new UcpStandardTotal { Type = "total", Amount = lineTotal },
        ];
    }

    /// <summary>
    /// The standard order. checkout_id is the cart id (our checkout id). permalink_url is the order page of the
    /// storefront (/account/orders/{id}, the Virto Commerce frontend route) when the storefront origin is known.
    /// </summary>
    public virtual UcpStandardOrder MapOrder(UcpOrder order, string storefrontOrigin)
    {
        ArgumentNullException.ThrowIfNull(order);

        var lineItems = (order.LineItems ?? [])
            .Where(lineItem => lineItem != null)
            .Select(lineItem => MapOrderLineItem(lineItem, order))
            .ToList();
        var totals = order.Totals ?? new UcpOrderTotals();

        return new UcpStandardOrder
        {
            Ucp = CreateMetadata(ModuleConstants.Discovery.Order),
            Id = order.Id,
            Label = NullIfWhiteSpace(order.Number),
            CheckoutId = order.CartId ?? string.Empty,
            PermalinkUrl = BuildOrderPermalink(order, storefrontOrigin),
            LineItems = lineItems,
            Fulfillment = MapOrderFulfillment(order, lineItems),
            Currency = NormalizeCurrency(order.Currency),
            Totals = MapTotals(totals.Subtotal, totals.DiscountTotal, totals.ShippingTotal, totals.TaxTotal, null, totals.Total),
            Messages = NullIfEmpty(MapMessages(order.Messages)),
        };
    }

    protected virtual string BuildOrderPermalink(UcpOrder order, string storefrontOrigin)
    {
        if (string.IsNullOrWhiteSpace(storefrontOrigin) || string.IsNullOrWhiteSpace(order.Id))
        {
            return null;
        }

        return AbsoluteUrlOrNull($"{storefrontOrigin.TrimEnd('/')}/account/orders/{Uri.EscapeDataString(order.Id)}");
    }

    /// <summary>
    /// A line's quantities and status from the order status: a canceled order has no active quantity (removed), a
    /// completed one is fulfilled, any other is processing. The order has no per-line fulfillment record.
    /// </summary>
    protected virtual UcpStandardOrderLineItem MapOrderLineItem(UcpOrderLineItem lineItem, UcpOrder order)
    {
        var quantity = Math.Max(0, lineItem.Quantity);
        var total = IsOrderStatus(order, "Cancelled", "Canceled") ? 0 : quantity;
        var fulfilled = IsOrderStatus(order, "Completed") ? total : 0;
        var price = Math.Max(0, (lineItem.PlacedPrice ?? lineItem.UnitPrice)?.Amount ?? 0);
        var lineTotal = lineItem.LineTotal?.Amount ?? price * quantity;

        return new UcpStandardOrderLineItem
        {
            Id = FirstNotEmpty(lineItem.Id, lineItem.ProductId),
            Item = new UcpStandardItem
            {
                Id = FirstNotEmpty(lineItem.ProductId, lineItem.Sku),
                Title = FirstNotEmpty(lineItem.Name, lineItem.Sku, lineItem.ProductId) ?? string.Empty,
                Price = price,
                ImageUrl = AbsoluteUrlOrNull(lineItem.ImageUrl),
            },
            Quantity = new UcpStandardOrderQuantity { Original = quantity, Total = total, Fulfilled = fulfilled },
            Totals = MapLineTotals(lineTotal),
            Status = GetOrderLineStatus(total, fulfilled),
        };
    }

    protected virtual string GetOrderLineStatus(int total, int fulfilled)
    {
        if (total == 0)
        {
            return "removed";
        }

        if (fulfilled == total)
        {
            return "fulfilled";
        }

        return fulfilled > 0 ? "partial" : "processing";
    }

    /// <summary>
    /// One expectation for an order with a single delivered-to shipment (it holds every active line); none when the
    /// order has several, since the order does not record which lines each holds. No events: shipments carry no event time.
    /// </summary>
    protected virtual UcpStandardOrderFulfillment MapOrderFulfillment(UcpOrder order, IList<UcpStandardOrderLineItem> lineItems)
    {
        var fulfillment = new UcpStandardOrderFulfillment { Expectations = [], Events = [] };
        var shipments = (order.Shipments ?? []).Where(shipment => shipment?.DeliveryAddress != null).ToList();
        var activeLines = lineItems
            .Where(lineItem => lineItem.Quantity.Total > 0)
            .Select(lineItem => new UcpStandardLineItemQuantity { Id = lineItem.Id, Quantity = lineItem.Quantity.Total })
            .ToList();
        if (shipments.Count != 1 || activeLines.Count == 0)
        {
            return fulfillment;
        }

        var shipment = shipments[0];
        fulfillment.Expectations.Add(new UcpStandardExpectation
        {
            Id = FirstNotEmpty(shipment.Id, shipment.Number, "shipment"),
            LineItems = activeLines,
            MethodType = "shipping",
            Destination = MapPostalAddress(shipment.DeliveryAddress),
            Description = NullIfWhiteSpace(FirstNotEmpty(shipment.ShipmentMethodOption, shipment.ShipmentMethodCode)),
        });

        return fulfillment;
    }

    protected virtual UcpStandardPostalAddress MapPostalAddress(UcpOrderAddress address)
    {
        return new UcpStandardPostalAddress
        {
            FirstName = NullIfWhiteSpace(address.FirstName),
            LastName = NullIfWhiteSpace(address.LastName),
            StreetAddress = NullIfWhiteSpace(address.Line1),
            ExtendedAddress = NullIfWhiteSpace(address.Line2),
            AddressLocality = NullIfWhiteSpace(address.City),
            AddressRegion = NullIfWhiteSpace(FirstNotEmpty(address.Region, address.RegionId)),
            AddressCountry = NullIfWhiteSpace(FirstNotEmpty(address.CountryCode, address.CountryName)),
            PostalCode = NullIfWhiteSpace(address.PostalCode),
            PhoneNumber = NullIfWhiteSpace(address.Phone),
        };
    }

    /// <summary>Standard line items as ours: item.id is the product id. A line id is kept only to update a cart.</summary>
    public virtual IList<UcpCartLineItemRequest> MapLineItemRequests(IEnumerable<UcpStandardLineItemRequest> lineItems, bool keepLineItemIds)
    {
        return lineItems?
            .Select(lineItem => lineItem == null
                ? null
                : new UcpCartLineItemRequest
                {
                    Id = keepLineItemIds ? NullIfWhiteSpace(lineItem.Id) : null,
                    ProductId = NullIfWhiteSpace(lineItem.Item?.Id),
                    Quantity = lineItem.Quantity,
                })
            .ToList();
    }

    public virtual UcpCheckoutBuyer MapBuyerRequest(UcpStandardBuyer buyer)
    {
        if (buyer == null)
        {
            return null;
        }

        var name = string.Join(' ', new[] { buyer.FirstName, buyer.LastName }.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()));
        var result = new UcpCheckoutBuyer
        {
            Email = NullIfWhiteSpace(buyer.Email),
            Name = NullIfWhiteSpace(name),
            Phone = NullIfWhiteSpace(buyer.PhoneNumber),
        };

        return result.Email == null && result.Name == null && result.Phone == null ? null : result;
    }

    /// <summary>The first shipping destination of the fulfillment methods, as our shipping address.</summary>
    public virtual UcpCheckoutAddress MapShippingAddressRequest(UcpStandardFulfillmentRequest fulfillment)
    {
        var destination = (fulfillment?.Methods ?? [])
            .Where(method => method != null && (string.IsNullOrWhiteSpace(method.Type) || string.Equals(method.Type, "shipping", StringComparison.OrdinalIgnoreCase)))
            .SelectMany(method => method.Destinations ?? [])
            .FirstOrDefault(address => address != null);

        return destination == null ? null : MapAddressRequest(destination);
    }

    public virtual UcpCheckoutAddress MapAddressRequest(UcpStandardPostalAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        var country = address.AddressCountry?.Trim();
        var isCountryCode = country?.Length is 2 or 3 && country.All(char.IsLetter);

        return new UcpCheckoutAddress
        {
            FirstName = NullIfWhiteSpace(address.FirstName),
            LastName = NullIfWhiteSpace(address.LastName),
            Line1 = NullIfWhiteSpace(address.StreetAddress),
            Line2 = NullIfWhiteSpace(address.ExtendedAddress),
            City = NullIfWhiteSpace(address.AddressLocality),
            Region = NullIfWhiteSpace(address.AddressRegion),
            PostalCode = NullIfWhiteSpace(address.PostalCode),
            CountryCode = isCountryCode ? country.ToUpperInvariant() : null,
            CountryName = isCountryCode ? null : NullIfWhiteSpace(country),
            Phone = NullIfWhiteSpace(address.PhoneNumber),
        };
    }

    protected virtual UcpStandardPrice MapPrice(UcpMoney money, string fallbackCurrency)
    {
        return money == null
            ? null
            : new UcpStandardPrice
            {
                Amount = Math.Max(0, money.Amount),
                Currency = NormalizeCurrency(FirstNotEmpty(money.Currency, fallbackCurrency)),
            };
    }

    protected virtual UcpStandardPriceRange MapPriceRange(IEnumerable<UcpStandardPrice> prices)
    {
        var list = prices.Where(price => price != null).ToList();
        if (list.Count == 0)
        {
            return null;
        }

        var min = list.MinBy(price => price.Amount);
        var max = list.MaxBy(price => price.Amount);

        return new UcpStandardPriceRange
        {
            Min = new UcpStandardPrice { Amount = min.Amount, Currency = min.Currency },
            Max = new UcpStandardPrice { Amount = max.Amount, Currency = max.Currency },
        };
    }

    protected virtual IList<UcpStandardMedia> MapMedia(string imageUrl, string altText)
    {
        var url = AbsoluteUrlOrNull(imageUrl);

        return url == null ? null : [new UcpStandardMedia { Type = "image", Url = url, AltText = NullIfWhiteSpace(altText) }];
    }

    private static UcpStandardPrice ZeroPrice(string currency)
    {
        return new UcpStandardPrice { Amount = 0, Currency = NormalizeCurrency(currency) };
    }

    private static void AddPositiveTotal(List<UcpStandardTotal> totals, string type, string displayText, UcpMoney money)
    {
        if (money?.Amount > 0)
        {
            totals.Add(new UcpStandardTotal { Type = type, DisplayText = displayText, Amount = money.Amount });
        }
    }

    private static void AddDistinctMessages(IList<UcpStandardMessage> target, IEnumerable<UcpStandardMessage> messages)
    {
        foreach (var message in messages)
        {
            if (!target.Any(x => x.Type == message.Type && x.Code == message.Code && x.Path == message.Path && x.Content == message.Content))
            {
                target.Add(message);
            }
        }
    }

    private static bool IsOrderStatus(UcpOrder order, params string[] statuses)
    {
        return statuses.Any(status => string.Equals(order.Status, status, StringComparison.OrdinalIgnoreCase));
    }

    protected static string NormalizeCurrency(string currency)
    {
        return string.IsNullOrWhiteSpace(currency) ? string.Empty : currency.Trim().ToUpperInvariant();
    }

    protected static string AbsoluteUrlOrNull(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? url.Trim()
            : null;
    }

    protected static IList<T> NullIfEmpty<T>(IList<T> items)
    {
        return items?.Count > 0 ? items : null;
    }

    protected static string NullIfWhiteSpace(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    protected static string FirstNotEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }
}
