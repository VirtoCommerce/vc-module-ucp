using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Models;
using VirtoCommerce.UCP.Web.Models;

namespace VirtoCommerce.UCP.Web.Diagnostics;

internal sealed class UcpOperationInputCapture
{
    private const int MaxInputJsonLength = 4096;
    private const int MaxCollectionItems = 20;
    private const int MaxSummaryItems = UcpTelemetryInputSanitizer.MaxSummaryItems;
    private const int MaxIdentifierLength = UcpTelemetryInputSanitizer.MaxIdentifierLength;
    private const int MaxTextLength = UcpTelemetryInputSanitizer.MaxTextLength;

    private readonly JsonObject _input = new();
    private readonly UcpTelemetryInputSanitizer _sanitizer = new();
    private readonly bool _captureInputValues;

    public UcpOperationInputCapture(bool captureInputValues = true)
    {
        _captureInputValues = captureInputValues;
    }

    public string ArgumentNames { get; private set; }
    public string RequestedStoreId { get; private set; }
    public string EffectiveStoreId { get; private set; }
    public string StoreSource { get; private set; }
    public string EffectiveCurrency { get; private set; }
    public string EffectiveCulture { get; private set; }
    public string DiagnosticQuery { get; private set; }
    public bool InputTruncated => _sanitizer.InputTruncated;
    public string TruncatedFields => _sanitizer.TruncatedFields;

    public static UcpOperationInputCapture CreateMcp(
        string operation,
        IDictionary<string, JsonElement> arguments,
        bool captureInputValues = true)
    {
        var capture = new UcpOperationInputCapture(captureInputValues);
        capture.CaptureMcp(operation, arguments);
        return capture;
    }

    public static UcpOperationInputCapture CreateRest(
        string operation,
        IDictionary<string, object> arguments,
        bool captureInputValues = true)
    {
        var capture = new UcpOperationInputCapture(captureInputValues);
        capture.CaptureRest(operation, arguments);
        return capture;
    }

    public string GetInputJson()
    {
        return _captureInputValues
            ? _sanitizer.SerializeBounded(_input, MaxInputJsonLength, "input_json")
            : null;
    }

    public void CaptureEffectiveContext(XApiRequestTelemetrySnapshot snapshot)
    {
        if (snapshot == null)
        {
            return;
        }

        EffectiveStoreId = FirstNotEmpty(snapshot.StoreId, EffectiveStoreId);
        EffectiveCurrency = FirstNotEmpty(snapshot.CurrencyCode, EffectiveCurrency);
        EffectiveCulture = FirstNotEmpty(snapshot.CultureName, EffectiveCulture);
        StoreSource = ResolveStoreSource();

        if (_captureInputValues)
        {
            SetEffectiveValue("store", EffectiveStoreId, StoreSource);
            SetEffectiveValue("currency", EffectiveCurrency, null);
            SetEffectiveValue("language", EffectiveCulture, null);
        }
    }

    public void CaptureMcp(string operation, IDictionary<string, JsonElement> arguments)
    {
        arguments ??= new Dictionary<string, JsonElement>();
        ArgumentNames = UcpTelemetryInputSanitizer.JoinNames(arguments.Keys);
        if (!_captureInputValues)
        {
            return;
        }

        if (!CaptureMcpCatalogAndCart(operation, arguments))
        {
            CaptureMcpCheckoutAndLookup(operation, arguments);
        }

        CaptureMcpStandard(arguments);
    }

    // The arguments of the standard UCP binding (meta, id, and the catalog, cart or checkout wrapper): identifiers,
    // the search text and counts, never buyer data.
    private void CaptureMcpStandard(IDictionary<string, JsonElement> arguments)
    {
        if (arguments.TryGetValue("meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
        {
            _input["meta_present"] = true;
            _input["idempotency_key_present"] = IsJsonPropertyPresent(meta, "idempotency-key");
        }

        AddMcpIdentifier(arguments, _input, "id", "id");
        if (arguments.TryGetValue("catalog", out var catalog) && catalog.ValueKind == JsonValueKind.Object)
        {
            AddQuery(GetJsonString(catalog, "query"));
            AddIdentifier(_input, "catalog_id", GetJsonString(catalog, "id"), "catalog_id");
            if (catalog.TryGetProperty("ids", out var ids) && ids.ValueKind == JsonValueKind.Array)
            {
                _input["catalog_id_count"] = ids.GetArrayLength();
            }
        }

        foreach (var name in new[] { "cart", "checkout" })
        {
            if (!arguments.TryGetValue(name, out var wrapper) || wrapper.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (wrapper.TryGetProperty("line_items", out var lineItems) && lineItems.ValueKind == JsonValueKind.Array)
            {
                _input[$"{name}_line_item_count"] = lineItems.GetArrayLength();
            }

            AddIdentifier(_input, $"{name}_cart_id", GetJsonString(wrapper, "cart_id"), $"{name}_cart_id");
            _input[$"{name}_buyer_present"] = IsJsonPropertyPresent(wrapper, "buyer");
            _input[$"{name}_fulfillment_present"] = IsJsonPropertyPresent(wrapper, "fulfillment");
        }
    }

    private bool CaptureMcpCatalogAndCart(string operation, IDictionary<string, JsonElement> arguments)
    {
        switch (operation)
        {
            case ModuleConstants.Operations.GetStoreCapabilities:
                return true;
            case ModuleConstants.Operations.SearchProducts:
                AddMcpQuery(arguments);
                AddMcpCommerceContext(arguments);
                AddMcpNumber(arguments, _input, "price_min", "price_min");
                AddMcpNumber(arguments, _input, "price_max", "price_max");
                AddMcpNumber(arguments, _input, "limit", "limit");
                return true;
            case ModuleConstants.Operations.GetProduct:
                AddMcpIdentifier(arguments, _input, "id", "id");
                AddMcpIdentifier(arguments, _input, "product_id", "product_id");
                AddResolvedAlias(_input, "product", GetMcpString(arguments, "product_id"), "product_id", GetMcpString(arguments, "id"), "id");
                AddMcpCommerceContext(arguments);
                return true;
            case ModuleConstants.Operations.CreateCart:
            case ModuleConstants.Operations.UpdateCart:
                if (operation == ModuleConstants.Operations.UpdateCart)
                {
                    AddMcpIdentifier(arguments, _input, "cart_id", "cart_id");
                }
                AddMcpCartContext(arguments);
                AddMcpLineItems(arguments);
                AddMcpCollectionCount(arguments, "coupons", "coupon_count");
                return true;
            case ModuleConstants.Operations.ListCarts:
                AddMcpCartContext(arguments);
                AddMcpFingerprint(arguments, "cursor", "cursor");
                AddMcpNumber(arguments, _input, "limit", "limit");
                AddMcpText(arguments, _input, "sort", "sort");
                return true;
            case ModuleConstants.Operations.GetCart:
                AddMcpIdentifier(arguments, _input, "cart_id", "cart_id");
                AddMcpCartContext(arguments);
                return true;
            default:
                return false;
        }
    }

    private void CaptureMcpCheckoutAndLookup(string operation, IDictionary<string, JsonElement> arguments)
    {
        switch (operation)
        {
            case ModuleConstants.Operations.CreateCheckout:
            case ModuleConstants.Operations.CheckoutAndHandoff:
                AddMcpIdentifier(arguments, _input, "cart_id", "cart_id");
                AddMcpCheckoutContext(arguments);
                break;
            case ModuleConstants.Operations.UpdateCheckout:
            case ModuleConstants.Operations.HandoffCheckout:
                AddMcpIdentifier(arguments, _input, "checkout_id", "checkout_id");
                AddMcpIdentifier(arguments, _input, "cart_id", "cart_id");
                AddResolvedAlias(_input, "cart", GetMcpString(arguments, "cart_id"), "cart_id", GetMcpString(arguments, "checkout_id"), "checkout_id");
                AddMcpCheckoutContext(arguments);
                break;
            case ModuleConstants.Operations.GetPaymentHandlers:
                AddMcpIdentifier(arguments, _input, "checkout_id", "checkout_id");
                break;
            case ModuleConstants.Operations.TrackOrder:
                AddMcpIdentifier(arguments, _input, "order_id", "order_id");
                AddMcpIdentifier(arguments, _input, "order_number", "order_number");
                AddMcpIdentifier(arguments, _input, "cart_id", "cart_id");
                AddLookupKind(GetMcpString(arguments, "order_id"), GetMcpString(arguments, "order_number"), GetMcpString(arguments, "cart_id"));
                AddMcpCartContext(arguments);
                break;
            case ModuleConstants.Operations.ListCountries:
                AddMcpQuery(arguments);
                AddMcpNumber(arguments, _input, "limit", "limit");
                break;
            case ModuleConstants.Operations.ResolveCountry:
                AddMcpQuery(arguments);
                break;
            case ModuleConstants.Operations.ListRegions:
                AddMcpIdentifier(arguments, _input, "country_id", "country_id");
                break;
            case ModuleConstants.Operations.SearchCatalog:
            case ModuleConstants.Operations.LookupCatalog:
            case ModuleConstants.Operations.CancelCart:
            case ModuleConstants.Operations.GetCheckout:
            case ModuleConstants.Operations.CompleteCheckout:
            case ModuleConstants.Operations.CancelCheckout:
            case ModuleConstants.Operations.GetOrder:
                // The standard arguments are captured for every tool; these add store_id and the buyer_id presence.
                AddMcpCartContext(arguments);
                break;
        }
    }

    public void CaptureRest(string operation, IDictionary<string, object> arguments)
    {
        arguments ??= new Dictionary<string, object>();
        ArgumentNames = UcpTelemetryInputSanitizer.JoinNames(arguments.Keys.Where(x => !string.Equals(x, "cancellationToken", StringComparison.OrdinalIgnoreCase)));
        if (!_captureInputValues)
        {
            return;
        }

        if (!CaptureRestCatalogAndCart(operation, arguments))
        {
            CaptureRestCheckoutAndLookup(operation, arguments);
        }
    }

    private bool CaptureRestCatalogAndCart(string operation, IDictionary<string, object> arguments)
    {
        switch (operation)
        {
            case ModuleConstants.Operations.GetStoreCapabilities:
                return true;
            case ModuleConstants.Operations.SearchProducts:
                CaptureCatalogRequest(GetArgument<UcpCatalogSearchRequest>(arguments, "request"));
                return true;
            case ModuleConstants.Operations.GetProduct:
                AddIdentifier(_input, "id", GetArgumentString(arguments, "id"), "id");
                CaptureCatalogProductQuery(GetArgument<UcpCatalogProductQuery>(arguments, "query"));
                return true;
            case ModuleConstants.Operations.CreateCart:
                CaptureCartRequest(GetArgument<UcpCartRequest>(arguments, "request"), null);
                return true;
            case ModuleConstants.Operations.ListCarts:
                CaptureCartListQuery(GetArgument<UcpCartListQuery>(arguments, "query"));
                return true;
            case ModuleConstants.Operations.GetCart:
                AddIdentifier(_input, "cart_id", GetArgumentString(arguments, "cartId"), "cart_id");
                CaptureCartQuery(GetArgument<UcpCartQuery>(arguments, "query"));
                return true;
            case ModuleConstants.Operations.UpdateCart:
                CaptureCartRequest(GetArgument<UcpCartRequest>(arguments, "request"), GetArgumentString(arguments, "cartId"));
                return true;
            default:
                return false;
        }
    }

    private void CaptureRestCheckoutAndLookup(string operation, IDictionary<string, object> arguments)
    {
        switch (operation)
        {
            case ModuleConstants.Operations.CreateCheckout:
                CaptureCheckoutRequest(GetArgument<UcpCheckoutRequest>(arguments, "request"), null);
                break;
            case ModuleConstants.Operations.UpdateCheckout:
            case ModuleConstants.Operations.HandoffCheckout:
                CaptureCheckoutRequest(GetArgument<UcpCheckoutRequest>(arguments, "request"), GetArgumentString(arguments, "checkoutId"));
                break;
            case ModuleConstants.Operations.GetPaymentHandlers:
                AddIdentifier(_input, "checkout_id", GetArgumentString(arguments, "checkoutId"), "checkout_id");
                break;
            case ModuleConstants.Operations.RestoreHandoff:
                CaptureHandoffRestore(GetArgument<UcpHandoffRestoreRequest>(arguments, "request"));
                break;
            case ModuleConstants.Operations.TrackOrder:
                CaptureOrderTrackingQuery(GetArgument<UcpOrderTrackingQuery>(arguments, "query"), GetArgumentString(arguments, "orderId"));
                break;
            case ModuleConstants.Operations.ListCountries:
                AddQuery(GetArgumentString(arguments, "query"));
                AddNumber(_input, "limit", GetArgumentNullableInt(arguments, "limit"));
                break;
            case ModuleConstants.Operations.ResolveCountry:
                AddQuery(GetArgumentString(arguments, "query"));
                break;
            case ModuleConstants.Operations.ListRegions:
                AddIdentifier(_input, "country_id", GetArgumentString(arguments, "countryId"), "country_id");
                break;
        }
    }

    private void CaptureCatalogRequest(UcpCatalogSearchRequest request)
    {
        if (request == null)
        {
            return;
        }

        AddQuery(request.Query);
        CaptureCatalogCommerceContext(request);
        CaptureCatalogPaging(request);
        CaptureCatalogFilters(request);
    }

    private void CaptureCatalogCommerceContext(UcpCatalogSearchRequest request)
    {
        AddCommerceContext(
            request.StoreId,
            request.Context?.StoreId,
            request.Currency,
            request.Context?.Currency,
            request.Language,
            request.Context?.Language);
    }

    private void CaptureCatalogPaging(UcpCatalogSearchRequest request)
    {
        AddNumber(_input, "limit", request.Limit ?? request.Pagination?.Limit);
        AddFingerprint(_input, "cursor", request.Pagination?.Cursor);
    }

    private void CaptureCatalogFilters(UcpCatalogSearchRequest request)
    {
        AddNumber(_input, "price_min", request.Filters?.Price?.Min);
        AddNumber(_input, "price_max", request.Filters?.Price?.Max);
        AddIdentifierArray(_input, "category_ids", request.Filters?.Categories, "category_ids");
    }

    private void CaptureCatalogProductQuery(UcpCatalogProductQuery query)
    {
        if (query != null)
        {
            AddCommerceContext(query.StoreId, null, query.Currency, null, query.CultureName, null);
        }
    }

    private void CaptureCartRequest(UcpCartRequest request, string routeCartId)
    {
        if (!string.IsNullOrWhiteSpace(routeCartId))
        {
            AddIdentifier(_input, "cart_id", routeCartId, "cart_id");
        }

        if (request == null)
        {
            return;
        }

        CaptureCartContexts(request);
        AddLineItems(request.LineItems);
        _input["coupon_count"] = request.Coupons?.Count ?? 0;
    }

    private void CaptureCartContexts(UcpCartRequest request)
    {
        AddCommerceContext(request.StoreId, request.Context?.StoreId, request.Currency, request.Context?.Currency, request.Language, request.Context?.Language);
        AddPrivateIdentifierPair("buyer", request.BuyerId, request.Context?.BuyerId);
        AddPrivateIdentifierPair("organization", request.OrganizationId, request.Context?.OrganizationId);
        AddPrivateIdentifierPair("cart_name", request.CartName, request.Context?.CartName);
        AddTextPair("cart_type", request.CartType, request.Context?.CartType);
    }

    private void CaptureCartListQuery(UcpCartListQuery query)
    {
        if (query == null)
        {
            return;
        }

        AddCommerceContext(query.StoreId, null, query.Currency, null, query.CultureName, null);
        AddPrivateIdentifierPresence("buyer_id", query.BuyerId);
        AddPrivateIdentifierPresence("organization_id", query.OrganizationId);
        AddText(_input, "cart_type", query.CartType, "cart_type");
        AddFingerprint(_input, "cursor", query.Cursor);
        AddNumber(_input, "limit", query.Limit);
        AddText(_input, "sort", query.Sort, "sort");
    }

    private void CaptureCartQuery(UcpCartQuery query)
    {
        if (query != null)
        {
            AddCommerceContext(query.StoreId, null, query.Currency, null, query.CultureName, null);
        }
    }

    private void CaptureCheckoutRequest(UcpCheckoutRequest request, string routeCheckoutId)
    {
        if (!string.IsNullOrWhiteSpace(routeCheckoutId))
        {
            AddIdentifier(_input, "checkout_id", routeCheckoutId, "checkout_id");
        }

        if (request == null)
        {
            return;
        }

        AddIdentifier(_input, "cart_id", request.CartId, "cart_id");
        if (!string.IsNullOrWhiteSpace(routeCheckoutId))
        {
            AddResolvedAlias(_input, "cart", request.CartId, "cart_id", routeCheckoutId, "checkout_id");
        }
        AddCommerceContext(request.StoreId, request.Context?.StoreId, request.Currency, request.Context?.Currency, request.Language, request.Context?.Language);
        AddPrivateIdentifierPair("buyer", request.BuyerId, request.Context?.BuyerId);
        AddPrivateIdentifierPair("organization", request.OrganizationId, request.Context?.OrganizationId);
        AddBuyerPresence(request.Buyer, null, null, null);
        AddAddressPresence("shipping_address", request.ShippingAddress);
        AddAddressPresence("billing_address", request.BillingAddress);
        AddIdentifier(_input, "shipping_method_id", request.ShippingMethodId, "shipping_method_id");
        AddIdentifier(_input, "payment_handler", request.PaymentHandler, "payment_handler");
        AddSensitivePresence("notes", request.Notes);
    }

    private void CaptureHandoffRestore(UcpHandoffRestoreRequest request)
    {
        if (request?.UcpSession == null)
        {
            return;
        }

        _input["session_present"] = true;
        _input["session_length"] = request.UcpSession.Length;
        _input["session_fingerprint"] = UcpTelemetryInputSanitizer.ComputeFingerprint(request.UcpSession);
    }

    private void CaptureOrderTrackingQuery(UcpOrderTrackingQuery query, string routeOrderId)
    {
        AddIdentifier(_input, "route_order_id", routeOrderId, "route_order_id");
        AddIdentifier(_input, "order_id", FirstNotEmpty(routeOrderId, query?.OrderId), "order_id");
        AddIdentifier(_input, "order_number", query?.OrderNumber, "order_number");
        AddIdentifier(_input, "cart_id", query?.CartId, "cart_id");
        AddLookupKind(FirstNotEmpty(routeOrderId, query?.OrderId), query?.OrderNumber, query?.CartId);
        if (query != null)
        {
            AddPrivateIdentifierPresence("buyer_id", query.BuyerId);
            AddPrivateIdentifierPresence("organization_id", query.OrganizationId);
            AddText(_input, "language", query.CultureName, "language");
        }
    }

    private void AddMcpCommerceContext(IDictionary<string, JsonElement> arguments)
    {
        AddCommerceContext(
            GetMcpString(arguments, "store_id"), null,
            GetMcpString(arguments, "currency"), null,
            GetMcpString(arguments, "language"), null);
    }

    private void AddMcpCartContext(IDictionary<string, JsonElement> arguments)
    {
        AddMcpCommerceContext(arguments);
        AddPrivateIdentifierPresence("buyer_id", GetMcpString(arguments, "buyer_id"));
        AddPrivateIdentifierPresence("organization_id", GetMcpString(arguments, "organization_id"));
        AddPrivateIdentifierPresence("cart_name", GetMcpString(arguments, "cart_name"));
        AddMcpText(arguments, _input, "cart_type", "cart_type");
    }

    private void AddMcpCheckoutContext(IDictionary<string, JsonElement> arguments)
    {
        AddMcpCartContext(arguments);
        AddMcpIdentifier(arguments, _input, "payment_handler", "payment_handler");
        AddMcpIdentifier(arguments, _input, "shipping_method_id", "shipping_method_id");
        AddMcpBuyerPresence(arguments);
        AddMcpAddressPresence(arguments, "shipping_address");
        AddMcpAddressPresence(arguments, "billing_address");
        if (arguments.TryGetValue("notes", out var notes) && notes.ValueKind == JsonValueKind.String)
        {
            AddSensitivePresence("notes", notes.GetString());
        }
    }

    private void AddCommerceContext(
        string topLevelStore,
        string contextStore,
        string topLevelCurrency,
        string contextCurrency,
        string topLevelLanguage,
        string contextLanguage)
    {
        RequestedStoreId = FirstNotEmpty(topLevelStore, contextStore, RequestedStoreId);
        AddContextValue("store", topLevelStore, contextStore);
        AddContextValue("currency", topLevelCurrency, contextCurrency);
        AddContextValue("language", topLevelLanguage, contextLanguage);
    }

    private void AddContextValue(string name, string topLevelValue, string contextValue)
    {
        if (string.IsNullOrWhiteSpace(topLevelValue) && string.IsNullOrWhiteSpace(contextValue))
        {
            return;
        }

        var value = new JsonObject();
        AddIdentifier(value, "top_level", topLevelValue, $"{name}.top_level");
        AddIdentifier(value, "context", contextValue, $"{name}.context");
        value["requested"] = _sanitizer.Sanitize(FirstNotEmpty(topLevelValue, contextValue), MaxIdentifierLength, $"{name}.requested");
        value["source"] = !string.IsNullOrWhiteSpace(topLevelValue) ? "top_level" : "context";
        _input[name] = value;
    }

    private void SetEffectiveValue(string name, string effectiveValue, string source)
    {
        if (string.IsNullOrWhiteSpace(effectiveValue))
        {
            return;
        }

        var value = _input[name] as JsonObject ?? new JsonObject();
        value["effective"] = _sanitizer.Sanitize(effectiveValue, MaxIdentifierLength, $"{name}.effective");
        if (!string.IsNullOrWhiteSpace(source))
        {
            value["source"] = source;
        }
        _input[name] = value;
    }

    private void AddPrivateIdentifierPair(string name, string topLevelValue, string contextValue)
    {
        if (string.IsNullOrWhiteSpace(topLevelValue) && string.IsNullOrWhiteSpace(contextValue))
        {
            return;
        }

        _input[name] = new JsonObject
        {
            ["top_level_present"] = !string.IsNullOrWhiteSpace(topLevelValue),
            ["context_present"] = !string.IsNullOrWhiteSpace(contextValue),
            ["effective_present"] = true,
            ["source"] = !string.IsNullOrWhiteSpace(topLevelValue) ? "top_level" : "context",
        };
    }

    private void AddPrivateIdentifierPresence(string name, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            _input[$"{name}_present"] = true;
        }
    }

    private void AddTextPair(string name, string topLevelValue, string contextValue)
    {
        if (string.IsNullOrWhiteSpace(topLevelValue) && string.IsNullOrWhiteSpace(contextValue))
        {
            return;
        }

        var value = new JsonObject();
        AddText(value, "top_level", topLevelValue, $"{name}.top_level");
        AddText(value, "context", contextValue, $"{name}.context");
        value["effective"] = _sanitizer.Sanitize(FirstNotEmpty(topLevelValue, contextValue), MaxTextLength, $"{name}.effective");
        value["source"] = !string.IsNullOrWhiteSpace(topLevelValue) ? "top_level" : "context";
        _input[name] = value;
    }

    private void AddResolvedAlias(JsonObject target, string name, string primaryValue, string primarySource, string fallbackValue, string fallbackSource)
    {
        var resolved = FirstNotEmpty(primaryValue, fallbackValue);
        if (string.IsNullOrWhiteSpace(resolved))
        {
            return;
        }

        target[$"resolved_{name}_id"] = _sanitizer.Sanitize(resolved, MaxIdentifierLength, $"resolved_{name}_id");
        target[$"resolved_{name}_source"] = !string.IsNullOrWhiteSpace(primaryValue) ? primarySource : fallbackSource;
    }

    private void AddLookupKind(string orderId, string orderNumber, string cartId)
    {
        if (!string.IsNullOrWhiteSpace(orderId))
        {
            _input["lookup_kind"] = "order_id";
        }
        else if (!string.IsNullOrWhiteSpace(orderNumber))
        {
            _input["lookup_kind"] = "order_number";
        }
        else if (!string.IsNullOrWhiteSpace(cartId))
        {
            _input["lookup_kind"] = "cart_id";
        }
        else
        {
            _input["lookup_kind"] = "missing";
        }
    }

    private void AddMcpLineItems(IDictionary<string, JsonElement> arguments)
    {
        if (!arguments.TryGetValue("line_items", out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var result = new JsonArray();
        var count = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (count++ >= MaxCollectionItems)
            {
                _sanitizer.MarkTruncated("line_items");
                break;
            }
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var output = new JsonObject();
            AddIdentifier(output, "id", GetJsonString(item, "id"), "line_items.id");
            AddIdentifier(output, "product_id", GetJsonString(item, "product_id"), "line_items.product_id");
            if (item.TryGetProperty("quantity", out var quantity) && quantity.TryGetInt32(out var number))
            {
                output["quantity"] = number;
            }
            result.Add(output);
        }

        _input["line_items"] = result;
        _input["line_item_count"] = value.GetArrayLength();
    }

    private void AddLineItems(IList<UcpCartLineItemRequest> items)
    {
        if (items == null)
        {
            return;
        }

        var result = new JsonArray();
        foreach (var item in items.Take(MaxCollectionItems))
        {
            if (item == null)
            {
                continue;
            }
            var output = new JsonObject();
            AddIdentifier(output, "id", item.Id, "line_items.id");
            AddIdentifier(output, "product_id", item.ProductId, "line_items.product_id");
            output["quantity"] = item.Quantity;
            result.Add(output);
        }
        if (items.Count > MaxCollectionItems)
        {
            _sanitizer.MarkTruncated("line_items");
        }
        _input["line_items"] = result;
        _input["line_item_count"] = items.Count;
    }

    private void AddMcpBuyerPresence(IDictionary<string, JsonElement> arguments)
    {
        JsonElement? buyer = arguments.TryGetValue("buyer", out var buyerValue) ? buyerValue : null;
        var buyerId = buyer is { ValueKind: JsonValueKind.Object } ? GetJsonString(buyer.Value, "id") : null;
        var value = new JsonObject();
        value["id_present"] = !string.IsNullOrWhiteSpace(buyerId);
        value["email_present"] = IsMcpValuePresent(arguments, "buyer_email") || IsJsonPropertyPresent(buyer, "email");
        value["name_present"] = IsMcpValuePresent(arguments, "buyer_name") || IsJsonPropertyPresent(buyer, "name");
        value["phone_present"] = IsMcpValuePresent(arguments, "buyer_phone") || IsJsonPropertyPresent(buyer, "phone");
        _input["buyer_fields"] = value;
    }

    private void AddBuyerPresence(UcpCheckoutBuyer buyer, string emailAlias, string nameAlias, string phoneAlias)
    {
        if (buyer == null && emailAlias == null && nameAlias == null && phoneAlias == null)
        {
            return;
        }

        var value = new JsonObject();
        value["id_present"] = !string.IsNullOrWhiteSpace(buyer?.Id);
        value["email_present"] = !string.IsNullOrWhiteSpace(FirstNotEmpty(emailAlias, buyer?.Email));
        value["name_present"] = !string.IsNullOrWhiteSpace(FirstNotEmpty(nameAlias, buyer?.Name));
        value["phone_present"] = !string.IsNullOrWhiteSpace(FirstNotEmpty(phoneAlias, buyer?.Phone));
        _input["buyer_fields"] = value;
    }

    private void AddMcpAddressPresence(IDictionary<string, JsonElement> arguments, string name)
    {
        if (!arguments.TryGetValue(name, out var address) || address.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var value = new JsonObject
        {
            ["present"] = true,
            ["first_name_present"] = IsJsonPropertyPresent(address, "first_name"),
            ["last_name_present"] = IsJsonPropertyPresent(address, "last_name"),
            ["line1_present"] = IsJsonPropertyPresent(address, "line1"),
            ["city_present"] = IsJsonPropertyPresent(address, "city"),
            ["postal_code_present"] = IsJsonPropertyPresent(address, "postal_code"),
            ["phone_present"] = IsJsonPropertyPresent(address, "phone"),
            ["email_present"] = IsJsonPropertyPresent(address, "email"),
        };
        AddFingerprint(value, "id", GetJsonString(address, "id"));
        AddIdentifier(value, "country_code", GetJsonString(address, "country_code"), $"{name}.country_code");
        AddIdentifier(value, "region_id", GetJsonString(address, "region_id"), $"{name}.region_id");
        _input[name] = value;
    }

    private void AddAddressPresence(string name, UcpCheckoutAddress address)
    {
        if (address == null)
        {
            return;
        }

        var value = new JsonObject
        {
            ["present"] = true,
            ["first_name_present"] = !string.IsNullOrWhiteSpace(address.FirstName),
            ["last_name_present"] = !string.IsNullOrWhiteSpace(address.LastName),
            ["line1_present"] = !string.IsNullOrWhiteSpace(address.Line1),
            ["city_present"] = !string.IsNullOrWhiteSpace(address.City),
            ["postal_code_present"] = !string.IsNullOrWhiteSpace(address.PostalCode),
            ["phone_present"] = !string.IsNullOrWhiteSpace(address.Phone),
            ["email_present"] = !string.IsNullOrWhiteSpace(address.Email),
        };
        AddFingerprint(value, "id", address.Id);
        AddIdentifier(value, "country_code", address.CountryCode, $"{name}.country_code");
        AddIdentifier(value, "region_id", address.RegionId, $"{name}.region_id");
        _input[name] = value;
    }

    private void AddSensitivePresence(string name, string value)
    {
        _input[$"{name}_present"] = !string.IsNullOrWhiteSpace(value);
        _input[$"{name}_length"] = value?.Length ?? 0;
    }

    private void AddMcpCollectionCount(IDictionary<string, JsonElement> arguments, string sourceName, string targetName)
    {
        if (arguments.TryGetValue(sourceName, out var value) && value.ValueKind == JsonValueKind.Array)
        {
            _input[targetName] = value.GetArrayLength();
        }
    }

    private void AddMcpFingerprint(IDictionary<string, JsonElement> arguments, string sourceName, string targetName)
    {
        AddFingerprint(_input, targetName, GetMcpString(arguments, sourceName));
    }

    private static void AddFingerprint(JsonObject target, string name, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }
        target[$"{name}_length"] = value.Length;
        target[$"{name}_fingerprint"] = UcpTelemetryInputSanitizer.ComputeFingerprint(value);
    }

    private void AddIdentifierArray(JsonObject target, string name, IEnumerable<string> values, string field)
    {
        if (values == null)
        {
            return;
        }

        var source = values.ToList();
        var array = new JsonArray();
        foreach (var value in source.Take(MaxSummaryItems))
        {
            var sanitized = _sanitizer.Sanitize(value, MaxIdentifierLength, field);
            if (sanitized != null)
            {
                array.Add(sanitized);
            }
        }
        if (source.Count > MaxSummaryItems)
        {
            _sanitizer.MarkTruncated(field);
        }
        target[name] = array;
    }

    private string ResolveStoreSource()
    {
        if (string.IsNullOrWhiteSpace(EffectiveStoreId))
        {
            return "not_applicable";
        }
        if (string.IsNullOrWhiteSpace(RequestedStoreId))
        {
            return "defaulted";
        }
        return string.Equals(RequestedStoreId, EffectiveStoreId, StringComparison.OrdinalIgnoreCase) ? "explicit" : "resolved";
    }

    private void AddIdentifier(JsonObject target, string name, string value, string field)
    {
        var sanitized = _sanitizer.Sanitize(value, MaxIdentifierLength, field);
        if (sanitized != null)
        {
            target[name] = sanitized;
        }
    }

    private void AddText(JsonObject target, string name, string value, string field)
    {
        var sanitized = _sanitizer.Sanitize(value, MaxTextLength, field);
        if (sanitized != null)
        {
            target[name] = sanitized;
        }
    }

    private void AddQuery(string value)
    {
        var redacted = UcpTelemetryInputSanitizer.RedactPotentialPii(value);
        var sanitized = _sanitizer.Sanitize(redacted, MaxTextLength, "query");
        if (sanitized != null)
        {
            _input["query"] = sanitized;
            DiagnosticQuery = sanitized;
        }
    }

    private static void AddNumber(JsonObject target, string name, long? value)
    {
        if (value.HasValue)
        {
            target[name] = value.Value;
        }
    }

    private void AddMcpIdentifier(IDictionary<string, JsonElement> source, JsonObject target, string sourceName, string targetName)
    {
        AddIdentifier(target, targetName, GetMcpString(source, sourceName), targetName);
    }

    private void AddMcpText(IDictionary<string, JsonElement> source, JsonObject target, string sourceName, string targetName)
    {
        AddText(target, targetName, GetMcpString(source, sourceName), targetName);
    }

    private void AddMcpQuery(IDictionary<string, JsonElement> source)
    {
        AddQuery(GetMcpString(source, "query"));
    }

    private static void AddMcpNumber(IDictionary<string, JsonElement> source, JsonObject target, string sourceName, string targetName)
    {
        if (!source.TryGetValue(sourceName, out var value))
        {
            return;
        }

        if (TryGetMcpNumber(value, out var number))
        {
            target[targetName] = number;
        }
    }

    private static bool TryGetMcpNumber(JsonElement value, out long number)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.TryGetInt64(out number);
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number);
        }

        number = default;
        return false;
    }

    private static string GetMcpString(IDictionary<string, JsonElement> arguments, string name)
    {
        if (!arguments.TryGetValue(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static string GetJsonString(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static bool IsMcpValuePresent(IDictionary<string, JsonElement> arguments, string name)
    {
        return arguments.TryGetValue(name, out var value) && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined &&
            (value.ValueKind != JsonValueKind.String || !string.IsNullOrWhiteSpace(value.GetString()));
    }

    private static bool IsJsonPropertyPresent(JsonElement? value, string name)
    {
        return value is { ValueKind: JsonValueKind.Object } && IsJsonPropertyPresent(value.Value, name);
    }

    private static bool IsJsonPropertyPresent(JsonElement value, string name)
    {
        return value.TryGetProperty(name, out var property) && property.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined &&
            (property.ValueKind != JsonValueKind.String || !string.IsNullOrWhiteSpace(property.GetString()));
    }

    private static T GetArgument<T>(IDictionary<string, object> arguments, string name)
        where T : class
    {
        return arguments.TryGetValue(name, out var value) ? value as T : null;
    }

    private static string GetArgumentString(IDictionary<string, object> arguments, string name)
    {
        return arguments.TryGetValue(name, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
    }

    private static int? GetArgumentNullableInt(IDictionary<string, object> arguments, string name)
    {
        if (!arguments.TryGetValue(name, out var value) || value == null)
        {
            return null;
        }
        if (value is int number)
        {
            return number;
        }

        return int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out number) ? number : null;
    }

    private static string FirstNotEmpty(params string[] values)
    {
        return values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
    }
}
