using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VirtoCommerce.MCP.Core.Attributes;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Models;
using VirtoCommerce.UCP.Core.Services;
using VirtoCommerce.UCP.Web.Diagnostics;
using VirtoCommerce.UCP.Web.Mcp;
using VirtoCommerce.UCP.Web.Services;
using VirtoCommerce.UCP.Web.Mcp.Models;
using Xunit;
using McpSerialization = VirtoCommerce.MCP.Core.McpSerialization;
using McpSensitiveArguments = VirtoCommerce.MCP.Core.McpSensitiveArguments;

namespace VirtoCommerce.UCP.Tests;

// The standard UCP MCP binding (2026-08-25): the seven standard-only tools, and the standard shape of the six tools whose
// name the standard shares with ours. The calls go through the MCP SDK (JSON arguments bound with the module's serializer
// options) and UcpMcpCallToolFilter, as on /ucp/mcp.
[Trait("Category", "Unit")]
public class UcpMcpStandardToolsTests
{
    private const string Version = "2026-08-25";
    private const string Meta = """{"ucp-agent": {"profile": "https://agent.example/profile.json"}}""";
    private const string AnonymousBuyerId = "ucp-anonymous-22222222222222222222222222222222";

    // error_response.json: ucp and messages, and nothing else but continue_url.
    private static readonly string[] ErrorResponseMembers = ["ucp", "messages", "continue_url"];
    private static readonly string[] CheckoutStatuses = ["incomplete", "requires_escalation", "ready_for_complete", "complete_in_progress", "completed", "canceled"];

    [Fact]
    public void StandardTools_AreTheSevenStandardOnlyTools_WithTheirAnnotations()
    {
        var tools = typeof(UcpMcpStandardTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>())
            .Where(attribute => attribute != null)
            .ToDictionary(attribute => attribute.Name);

        Assert.Equal(ModuleConstants.McpTools.StandardOnlyToolNames.Order(), tools.Keys.Order());
        Assert.All(tools.Keys, name => Assert.True(ModuleConstants.McpTools.IsUcpTool(name)));
        Assert.Contains(ModuleConstants.McpScopeName, typeof(UcpMcpStandardTools).GetCustomAttribute<McpScopeAttribute>()?.Scopes ?? []);
        Assert.NotNull(typeof(UcpMcpStandardTools).GetCustomAttribute<AllowAnonymousAttribute>());
        Assert.True(tools[ModuleConstants.McpTools.SearchCatalog].ReadOnly);
        Assert.True(tools[ModuleConstants.McpTools.LookupCatalog].ReadOnly);
        Assert.True(tools[ModuleConstants.McpTools.GetCheckout].ReadOnly);
        Assert.True(tools[ModuleConstants.McpTools.GetOrder].ReadOnly);
        Assert.True(tools[ModuleConstants.McpTools.CancelCart].Destructive);
        Assert.False(tools[ModuleConstants.McpTools.CompleteCheckout].ReadOnly);
        Assert.False(tools[ModuleConstants.McpTools.CompleteCheckout].Destructive);
    }

    [Fact]
    public void StandardArguments_MaskBuyerDataInAudit()
    {
        var createCheckoutPaths = McpSensitiveArguments.GetPaths(typeof(UcpMcpCommerceTools).GetMethod(nameof(UcpMcpCommerceTools.CreateCheckout)));
        var completePaths = McpSensitiveArguments.GetPaths(typeof(UcpMcpStandardTools).GetMethod(nameof(UcpMcpStandardTools.CompleteCheckout)));
        var cartPaths = McpSensitiveArguments.GetPaths(typeof(UcpMcpCommerceTools).GetMethod(nameof(UcpMcpCommerceTools.CreateCart)));

        Assert.Contains("checkout.buyer", createCheckoutPaths);
        Assert.Contains("checkout.fulfillment", createCheckoutPaths);
        Assert.Contains("checkout.payment", createCheckoutPaths);
        Assert.Contains("checkout.context.postal_code", createCheckoutPaths);
        Assert.Contains("checkout.payment", completePaths);
        Assert.Contains("cart.buyer", cartPaths);
    }

    [Theory]
    [InlineData(false, false, null, null, false)]
    [InlineData(true, false, null, null, true)]
    [InlineData(false, true, null, null, true)]
    [InlineData(false, false, "id-1", null, true)]
    [InlineData(false, false, "id-1", "cart-1", false)]
    [InlineData(false, false, null, "cart-1", false)]
    public void IsStandardCall_FollowsTheShapeRule(bool hasWrapper, bool hasMeta, string id, string ownId, bool expected)
    {
        var result = UcpMcpStandardTools.IsStandardCall(
            hasWrapper ? new object() : null,
            hasMeta ? new VirtoCommerce.UCP.Web.Mcp.Models.Standard.UcpStandardMeta() : null,
            id,
            ownId);

        Assert.Equal(expected, result);
    }

    [Fact]
    public async Task SearchCatalog_MapsTheStandardRequest_AndReturnsTheStandardSearchResponse()
    {
        var catalog = new FakeCatalogService();
        var services = CreateServices(catalog: catalog);

        var result = await Call(typeof(UcpMcpStandardTools), nameof(UcpMcpStandardTools.SearchCatalog), $$$"""
            {
              "meta": {{{Meta}}},
              "catalog": {
                "query": "printer",
                "context": {"currency": "usd", "language": "en-US", "address_country": "US"},
                "filters": {"price": {"min": 1000, "max": 50000}, "categories": ["printers"]},
                "pagination": {"limit": 5, "cursor": "c1"}
              }
            }
            """, services);

        var payload = AssertStandardResult(result);
        AssertUcp(payload, ModuleConstants.Discovery.CatalogSearch);
        Assert.Equal("printer", catalog.LastRequest.Query);
        Assert.Equal("usd", catalog.LastRequest.Currency);
        Assert.Equal("US", catalog.LastRequest.Context.AddressCountry);
        Assert.Equal(1000, catalog.LastRequest.Filters.Price.Min);
        Assert.Equal(50000, catalog.LastRequest.Filters.Price.Max);
        Assert.Equal(["printers"], catalog.LastRequest.Filters.Categories);
        Assert.Equal(5, catalog.LastRequest.Pagination.Limit);
        Assert.Equal("c1", catalog.LastRequest.Pagination.Cursor);

        var product = Assert.Single(payload.GetProperty("products").EnumerateArray());
        AssertProduct(product);
        Assert.Equal(2, product.GetProperty("variants").GetArrayLength());
        Assert.Equal(19900, product.GetProperty("price_range").GetProperty("min").GetProperty("amount").GetInt64());
        Assert.Equal(24900, product.GetProperty("price_range").GetProperty("max").GetProperty("amount").GetInt64());
        Assert.Equal("USD", product.GetProperty("price_range").GetProperty("min").GetProperty("currency").GetString());
        Assert.Equal("https://cdn.example/printer.png", product.GetProperty("media")[0].GetProperty("url").GetString());
        Assert.True(payload.GetProperty("pagination").GetProperty("has_next_page").GetBoolean());
        Assert.Equal("next", payload.GetProperty("pagination").GetProperty("cursor").GetString());
    }

    [Fact]
    public async Task LookupCatalog_ReturnsResolvedVariantsWithInputs_AndNotFoundMessages()
    {
        var services = CreateServices();

        var result = await Call(typeof(UcpMcpStandardTools), nameof(UcpMcpStandardTools.LookupCatalog), $$$"""
            {"meta": {{{Meta}}}, "catalog": {"ids": ["printer-1", "printer-1-blue", "missing-1"]}}
            """, services);

        var payload = AssertStandardResult(result);
        AssertUcp(payload, ModuleConstants.Discovery.CatalogLookup);
        var product = Assert.Single(payload.GetProperty("products").EnumerateArray());
        AssertProduct(product);
        var variants = product.GetProperty("variants").EnumerateArray().ToList();
        Assert.Equal(2, variants.Count);
        Assert.Equal("printer-1-black", variants[0].GetProperty("id").GetString());
        Assert.Equal("printer-1", variants[0].GetProperty("inputs")[0].GetProperty("id").GetString());
        Assert.Equal("featured", variants[0].GetProperty("inputs")[0].GetProperty("match").GetString());
        Assert.Equal("printer-1-blue", variants[1].GetProperty("id").GetString());
        Assert.Equal("exact", variants[1].GetProperty("inputs")[0].GetProperty("match").GetString());
        var message = Assert.Single(payload.GetProperty("messages").EnumerateArray());
        Assert.Equal("info", message.GetProperty("type").GetString());
        Assert.Equal("not_found", message.GetProperty("code").GetString());
        Assert.Equal("missing-1", message.GetProperty("content").GetString());
    }

    [Fact]
    public async Task LookupCatalog_WithoutIds_ReturnsTheStandardErrorResponse()
    {
        var result = await Call(typeof(UcpMcpStandardTools), nameof(UcpMcpStandardTools.LookupCatalog), """{"catalog": {"ids": []}}""", CreateServices());

        AssertStandardError(result, ModuleConstants.Discovery.CatalogLookup, ModuleConstants.ErrorCodes.InvalidRequest, "recoverable");
    }

    [Fact]
    public async Task GetProduct_StandardCall_ReturnsTheStandardProduct()
    {
        var catalog = new FakeCatalogService();

        var result = await Call(typeof(UcpMcpCommerceTools), nameof(UcpMcpCommerceTools.GetProduct), $$$"""
            {"meta": {{{Meta}}}, "catalog": {"id": "printer-1", "context": {"currency": "EUR"} } }
            """, CreateServices(catalog: catalog));

        var payload = AssertStandardResult(result);
        AssertUcp(payload, ModuleConstants.Discovery.CatalogLookup);
        AssertProduct(payload.GetProperty("product"));
        Assert.Equal("printer-1", catalog.LastProductId);
        Assert.Equal("EUR", catalog.LastRequest.Currency);
    }

    [Fact]
    public async Task GetProduct_NotFound_ReturnsTheStandardNotFoundError()
    {
        var result = await Call(typeof(UcpMcpCommerceTools), nameof(UcpMcpCommerceTools.GetProduct), """{"catalog": {"id": "missing-1"}}""", CreateServices());

        AssertStandardError(result, ModuleConstants.Discovery.CatalogLookup, "not_found", "unrecoverable");
    }

    [Fact]
    public async Task GetProduct_FlatCall_ReturnsTodaysResponse()
    {
        var catalog = new FakeCatalogService();

        var result = await UcpMcpCommerceTools.GetProduct(new FakeProfileService(), catalog, id: "printer-1", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("printer-1", Assert.IsType<UcpProductResponse>(result).Product.Id);

        // Through the SDK the flat call keeps today's payload: our product, not the standard one.
        var sdkResult = await Call(typeof(UcpMcpCommerceTools), nameof(UcpMcpCommerceTools.GetProduct), """{"id": "printer-1"}""", CreateServices(catalog: catalog));
        Assert.Contains("\"product\":{\"id\":\"printer-1\",\"code\":\"PRN-1\"", Assert.IsType<TextContentBlock>(sdkResult.Content[0]).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateCart_StandardCall_CreatesTheCartFromItemIds_AndReturnsTheStandardCart()
    {
        var cart = new FakeCartService();

        var result = await Call(typeof(UcpMcpCommerceTools), nameof(UcpMcpCommerceTools.CreateCart), $$$"""
            {"meta": {{{Meta}}}, "cart": {"line_items": [{"id": "li_1", "item": {"id": "printer-1"}, "quantity": 2}], "context": {"currency": "USD"} } }
            """, CreateServices(cart: cart));

        var payload = AssertStandardResult(result);
        AssertCart(payload, ModuleConstants.Discovery.Cart);
        var line = Assert.Single(cart.LastCreateRequest.LineItems);
        Assert.Equal("printer-1", line.ProductId);
        Assert.Equal(2, line.Quantity);
        Assert.Null(line.Id);
        Assert.Equal(AnonymousBuyerId, payload.GetProperty("buyer_id").GetString());
        Assert.Equal("https://store.example/cart/cart-1", payload.GetProperty("continue_url").GetString());
        var totals = payload.GetProperty("totals").EnumerateArray().Select(x => x.GetProperty("type").GetString()).ToList();
        Assert.Equal(["subtotal", "discount", "fulfillment", "total"], totals);
        Assert.Equal(-1000, payload.GetProperty("totals")[1].GetProperty("amount").GetInt64());
    }

    [Fact]
    public async Task CreateCart_FlatCall_ReturnsTodaysResponse()
    {
        var cart = new FakeCartService();

        var result = await UcpMcpCommerceTools.CreateCart(
            new FakeProfileService(),
            cart,
            [new UcpCartLineItemRequest { ProductId = "printer-1", Quantity = 1 }],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("cart-1", Assert.IsType<UcpCartResponse>(result).Cart.Id);
    }

    [Fact]
    public async Task GetCart_IdWithoutCartId_IsStandard_AndCartIdIsFlat()
    {
        var cart = new FakeCartService();
        var services = CreateServices(cart: cart);

        var standard = await Call(typeof(UcpMcpCommerceTools), nameof(UcpMcpCommerceTools.GetCart), $$$"""{"id": "cart-1", "buyer_id": "{{{AnonymousBuyerId}}}"}""", services);
        var standardBuyerId = cart.LastGetRequest.BuyerId;
        var flat = await Call(typeof(UcpMcpCommerceTools), nameof(UcpMcpCommerceTools.GetCart), """{"cart_id": "cart-1"}""", services);
        var both = await UcpMcpCommerceTools.GetCart(new FakeProfileService(), cart, cart_id: "cart-1", id: "other", cancellationToken: TestContext.Current.CancellationToken);

        AssertCart(AssertStandardResult(standard), ModuleConstants.Discovery.Cart);
        Assert.Equal(AnonymousBuyerId, standardBuyerId);
        Assert.Contains("\"cart\":{", Assert.IsType<TextContentBlock>(flat.Content[0]).Text, StringComparison.Ordinal);
        Assert.IsType<UcpCartResponse>(both);
    }

    [Fact]
    public async Task GetCart_NotFound_ReturnsTheStandardErrorThroughTheFilter()
    {
        var result = await Call(typeof(UcpMcpCommerceTools), nameof(UcpMcpCommerceTools.GetCart), """{"id": "missing-cart"}""", CreateServices());

        AssertStandardError(result, ModuleConstants.Discovery.Cart, "not_found", "unrecoverable");
        Assert.Equal(ModuleConstants.ErrorCodes.CartNotFound, result.Meta?["error_code"]?.GetValue<string>());
    }

    [Fact]
    public async Task UpdateCart_StandardCall_ReplacesLinesKeepingLineIds()
    {
        var cart = new FakeCartService();

        var result = await Call(typeof(UcpMcpCommerceTools), nameof(UcpMcpCommerceTools.UpdateCart), $$$"""
            {"meta": {{{Meta}}}, "id": "cart-1", "cart": {"line_items": [{"id": "line-1", "item": {"id": "printer-1"}, "quantity": 3}]}}
            """, CreateServices(cart: cart));

        AssertCart(AssertStandardResult(result), ModuleConstants.Discovery.Cart);
        Assert.Equal("cart-1", cart.LastUpdateCartId);
        var line = Assert.Single(cart.LastUpdateRequest.LineItems);
        Assert.Equal("line-1", line.Id);
        Assert.Equal(3, line.Quantity);
    }

    [Fact]
    public async Task UpdateCart_FlatCallWithoutLineItems_IsRefusedInsteadOfEmptyingTheCart()
    {
        var cart = new FakeCartService();

        var exception = await Assert.ThrowsAsync<UcpException>(() => UcpMcpCommerceTools.UpdateCart(
            new FakeProfileService(), cart, cart_id: "cart-1", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ModuleConstants.ErrorCodes.InvalidRequest, exception.Code);
        Assert.Null(cart.LastUpdateRequest);
    }

    [Fact]
    public async Task CancelCart_AnonymousCart_ClearsTheLines_AndReturnsTheCartBefore()
    {
        var cart = new FakeCartService();

        var result = await Call(typeof(UcpMcpStandardTools), nameof(UcpMcpStandardTools.CancelCart), $$$"""
            {"meta": {"ucp-agent": {"profile": "https://agent.example/p.json"}, "idempotency-key": "550e8400-e29b-41d4-a716-446655440000"}, "id": "cart-1", "buyer_id": "{{{AnonymousBuyerId}}}"}
            """, CreateServices(cart: cart));

        var payload = AssertStandardResult(result);
        AssertCart(payload, ModuleConstants.Discovery.Cart);
        Assert.Equal(1, payload.GetProperty("line_items").GetArrayLength());
        Assert.Equal("cart-1", cart.LastUpdateCartId);
        Assert.Empty(cart.LastUpdateRequest.LineItems);
    }

    [Fact]
    public async Task CancelCart_SignedInBuyer_IsRefused()
    {
        var cart = new FakeCartService();

        var result = await Call(typeof(UcpMcpStandardTools), nameof(UcpMcpStandardTools.CancelCart), """{"id": "cart-1"}""",
            CreateServices(cart: cart, buyerContextAccessor: new SignedInBuyerContextAccessor()));

        AssertStandardError(result, ModuleConstants.Discovery.Cart, ModuleConstants.ErrorCodes.InvalidRequest, "unrecoverable");
        Assert.Null(cart.LastUpdateRequest);
    }

    [Fact]
    public async Task CreateCheckout_StandardLineItems_CreatesACartThenItsCheckout()
    {
        var cart = new FakeCartService();
        var checkout = new FakeCheckoutService();

        var result = await Call(typeof(UcpMcpCommerceTools), nameof(UcpMcpCommerceTools.CreateCheckout), $$$"""
            {
              "meta": {{{Meta}}},
              "checkout": {
                "line_items": [{"item": {"id": "printer-1"}, "quantity": 1}],
                "buyer": {"first_name": "Jane", "last_name": "Doe", "email": "jane@example.com"},
                "fulfillment": {"methods": [{"type": "shipping", "destinations": [
                  {"first_name": "Jane", "last_name": "Doe", "street_address": "1 Main St", "address_locality": "Springfield",
                   "address_region": "IL", "postal_code": "62701", "address_country": "us"}]}]}
              }
            }
            """, CreateServices(cart: cart, checkout: checkout));

        var payload = AssertStandardResult(result);
        AssertCheckout(payload);
        Assert.Equal("printer-1", Assert.Single(cart.LastCreateRequest.LineItems).ProductId);
        Assert.Equal("cart-1", checkout.LastCreateRequest.CartId);
        Assert.Equal(AnonymousBuyerId, checkout.LastCreateRequest.BuyerId);
        Assert.Equal("Jane Doe", checkout.LastCreateRequest.Buyer.Name);
        Assert.Equal("jane@example.com", checkout.LastCreateRequest.Buyer.Email);
        Assert.Equal("1 Main St", checkout.LastCreateRequest.ShippingAddress.Line1);
        Assert.Equal("US", checkout.LastCreateRequest.ShippingAddress.CountryCode);
        Assert.Equal("ready_for_complete", payload.GetProperty("status").GetString());
        Assert.Equal("Jane", payload.GetProperty("buyer").GetProperty("first_name").GetString());
        Assert.False(payload.TryGetProperty("messages", out _));
    }

    [Fact]
    public async Task CreateCheckout_StandardCartId_UsesTheCart_AndIgnoresTheCheckoutBuyer()
    {
        var cart = new FakeCartService();
        var checkout = new FakeCheckoutService();

        var result = await Call(typeof(UcpMcpCommerceTools), nameof(UcpMcpCommerceTools.CreateCheckout), $$$"""
            {"meta": {{{Meta}}}, "checkout": {"cart_id": "cart-1", "buyer": {"email": "other@example.com"}}, "buyer_id": "{{{AnonymousBuyerId}}}"}
            """, CreateServices(cart: cart, checkout: checkout));

        var payload = AssertStandardResult(result);
        AssertCheckout(payload);
        Assert.Null(cart.LastCreateRequest);
        Assert.Equal("cart-1", checkout.LastCreateRequest.CartId);
        Assert.Null(checkout.LastCreateRequest.Buyer?.Email);
        // No shipping address yet: the hosted checkout cannot start, and the platform is told what to supply.
        Assert.Equal("incomplete", payload.GetProperty("status").GetString());
        var message = Assert.Single(payload.GetProperty("messages").EnumerateArray());
        Assert.Equal("error", message.GetProperty("type").GetString());
        Assert.Equal("shipping_address_missing", message.GetProperty("code").GetString());
        Assert.Equal("recoverable", message.GetProperty("severity").GetString());
    }

    [Fact]
    public async Task CreateCheckout_FlatCall_ReturnsTodaysResponse()
    {
        var checkout = new FakeCheckoutService();

        var result = await UcpMcpCommerceTools.CreateCheckout(new FakeProfileService(), checkout, "cart-1", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("cart-1", Assert.IsType<UcpCheckoutResponse>(result).Checkout.Id);
    }

    [Fact]
    public async Task UpdateCheckout_StandardCall_ReplacesTheLines_ThenUpdatesTheCheckout()
    {
        var cart = new FakeCartService();
        var checkout = new FakeCheckoutService();

        var result = await Call(typeof(UcpMcpCommerceTools), nameof(UcpMcpCommerceTools.UpdateCheckout), $$$"""
            {"id": "cart-1", "checkout": {"line_items": [{"id": "line-1", "item": {"id": "printer-1"}, "quantity": 2}],
              "fulfillment": {"methods": [{"type": "shipping", "destinations": [{"first_name": "Jane", "last_name": "Doe", "postal_code": "62701"}]}]} } }
            """, CreateServices(cart: cart, checkout: checkout));

        var payload = AssertStandardResult(result);
        AssertCheckout(payload);
        Assert.Equal("line-1", Assert.Single(cart.LastUpdateRequest.LineItems).Id);
        Assert.Equal("cart-1", checkout.LastUpdateCheckoutId);
        Assert.Equal("62701", checkout.LastUpdateRequest.ShippingAddress.PostalCode);
        Assert.Equal("ready_for_complete", payload.GetProperty("status").GetString());
    }

    [Fact]
    public async Task UpdateCheckout_FlatCall_ReturnsTodaysResponse()
    {
        var checkout = new FakeCheckoutService();

        var result = await UcpMcpCommerceTools.UpdateCheckout(new FakeProfileService(), checkout, "cart-1", cancellationToken: TestContext.Current.CancellationToken);

        var update = Assert.IsType<UcpMcpUpdateCheckoutResult>(result);
        Assert.Equal(ModuleConstants.McpTools.HandoffCheckout, update.NextStep.Tool);
    }

    [Fact]
    public async Task GetCheckout_ReadsTheCheckoutWithoutChangingIt()
    {
        var checkout = new FakeCheckoutService { ShippingAddress = new UcpCheckoutAddress { FirstName = "Jane", LastName = "Doe", PostalCode = "62701" } };

        var result = await Call(typeof(UcpMcpStandardTools), nameof(UcpMcpStandardTools.GetCheckout), $$$"""
            {"meta": {{{Meta}}}, "id": "cart-1", "buyer_id": "{{{AnonymousBuyerId}}}"}
            """, CreateServices(checkout: checkout));

        var payload = AssertStandardResult(result);
        AssertCheckout(payload);
        Assert.Equal("cart-1", checkout.LastUpdateCheckoutId);
        Assert.Null(checkout.LastUpdateRequest.ShippingAddress);
        Assert.Null(checkout.LastUpdateRequest.BillingAddress);
        Assert.Equal(AnonymousBuyerId, checkout.LastUpdateRequest.BuyerId);
        Assert.Equal("ready_for_complete", payload.GetProperty("status").GetString());
        // Our "create a new handoff URL" message does not reach a standard client.
        Assert.False(payload.TryGetProperty("messages", out _));
    }

    [Fact]
    public async Task CompleteCheckout_HandsOff_AndRequiresEscalationWithContinueUrl()
    {
        var checkout = new FakeCheckoutService { ShippingAddress = new UcpCheckoutAddress { FirstName = "Jane", LastName = "Doe", PostalCode = "62701" } };

        var result = await Call(typeof(UcpMcpStandardTools), nameof(UcpMcpStandardTools.CompleteCheckout), $$$"""
            {"meta": {"ucp-agent": {"profile": "https://agent.example/p.json"}, "idempotency-key": "550e8400-e29b-41d4-a716-446655440000"},
             "id": "cart-1", "checkout": {"payment": {"instruments": []}}, "buyer_id": "{{{AnonymousBuyerId}}}"}
            """, CreateServices(checkout: checkout));

        var payload = AssertStandardResult(result);
        AssertCheckout(payload);
        Assert.Equal("cart-1", checkout.LastHandoffCheckoutId);
        Assert.Equal("requires_escalation", payload.GetProperty("status").GetString());
        Assert.Equal("https://store.example/checkout?ucp_session=token", payload.GetProperty("continue_url").GetString());
        Assert.Equal("2026-06-16T09:45:15.0000000Z", payload.GetProperty("expires_at").GetString());
        var message = Assert.Single(payload.GetProperty("messages").EnumerateArray());
        Assert.Equal("error", message.GetProperty("type").GetString());
        Assert.Equal(UcpStandardMapper.HostedCheckoutRequiredCode, message.GetProperty("code").GetString());
        Assert.Equal("requires_buyer_input", message.GetProperty("severity").GetString());
    }

    [Fact]
    public async Task CancelCheckout_IsNotSupported()
    {
        var result = await Call(typeof(UcpMcpStandardTools), nameof(UcpMcpStandardTools.CancelCheckout), $$$"""{"meta": {{{Meta}}}, "id": "cart-1"}""", CreateServices());

        AssertStandardError(result, ModuleConstants.Discovery.Checkout, ModuleConstants.ErrorCodes.InvalidRequest, "unrecoverable");
    }

    [Fact]
    public async Task GetOrder_ReturnsTheStandardOrder_WithTheStorefrontPermalink()
    {
        var orders = new FakeOrderService();

        var result = await Call(typeof(UcpMcpStandardTools), nameof(UcpMcpStandardTools.GetOrder), $$$"""
            {"meta": {{{Meta}}}, "id": "CO-1001", "buyer_id": "{{{AnonymousBuyerId}}}"}
            """, CreateServices(orders: orders));

        var payload = AssertStandardResult(result);
        AssertUcp(payload, ModuleConstants.Discovery.Order);
        AssertRequired(payload, "ucp", "id", "checkout_id", "permalink_url", "line_items", "fulfillment", "currency", "totals");
        Assert.Equal("CO-1001", orders.LastRequest.OrderId);
        Assert.Equal(AnonymousBuyerId, orders.LastRequest.Context.BuyerId);
        Assert.Equal("order-1", payload.GetProperty("id").GetString());
        Assert.Equal("CO-1001", payload.GetProperty("label").GetString());
        Assert.Equal("cart-1", payload.GetProperty("checkout_id").GetString());
        Assert.Equal("https://store.example/account/orders/order-1", payload.GetProperty("permalink_url").GetString());
        var line = Assert.Single(payload.GetProperty("line_items").EnumerateArray());
        AssertRequired(line, "id", "item", "quantity", "totals", "status");
        Assert.Equal("processing", line.GetProperty("status").GetString());
        Assert.Equal(2, line.GetProperty("quantity").GetProperty("total").GetInt32());
        Assert.Equal(0, line.GetProperty("quantity").GetProperty("fulfilled").GetInt32());
        var expectation = Assert.Single(payload.GetProperty("fulfillment").GetProperty("expectations").EnumerateArray());
        AssertRequired(expectation, "id", "line_items", "method_type", "destination");
        Assert.Equal("62701", expectation.GetProperty("destination").GetProperty("postal_code").GetString());
        AssertTotals(payload.GetProperty("totals"));
    }

    [Fact]
    public async Task GetOrder_NotFound_ReturnsTheStandardNotFoundError()
    {
        var result = await Call(typeof(UcpMcpStandardTools), nameof(UcpMcpStandardTools.GetOrder), """{"id": "missing"}""", CreateServices());

        AssertStandardError(result, ModuleConstants.Discovery.Order, "not_found", "unrecoverable");
    }

    // MCP Inspector refuses a bare `true` where a schema object is expected: clients that map tool schemas onto a
    // stricter dialect (Gemini's OpenAPI subset) reject such a tool.
    [Fact]
    public void UcpTools_DeclareNoBareTrueSchema()
    {
        var methods = new[] { typeof(UcpMcpCommerceTools), typeof(UcpMcpStandardTools), typeof(UcpMcpIdentityTools) }
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() != null);
        var bare = new List<string>();
        // The services the tools take from DI, so the SDK leaves them out of the input schemas, as on the platform.
        var services = new ServiceCollection();
        foreach (var serviceType in new[]
                 {
                     typeof(IUcpProfileService), typeof(IUcpCatalogService), typeof(IUcpCartService), typeof(IUcpCheckoutService),
                     typeof(IUcpOrderService), typeof(IUcpGeographyService), typeof(IUcpBuyerContextAccessor), typeof(UcpBuyerIdentityService),
                     typeof(UcpStandardMapper), typeof(Microsoft.AspNetCore.Http.IHttpContextAccessor), typeof(VirtoCommerce.MCP.Core.Services.IMcpSessionService),
                 })
        {
            services.AddTransient(serviceType, _ => null);
        }

        using var serviceProvider = services.BuildServiceProvider();

        foreach (var method in methods)
        {
            var tool = McpServerTool.Create(method, target: null, new McpServerToolCreateOptions { Services = serviceProvider, SerializerOptions = McpSerialization.Options });
            CollectBareTrueSchemas(tool.ProtocolTool.InputSchema, $"{tool.ProtocolTool.Name}.inputSchema", bare);
        }

        Assert.Empty(bare);
    }

    private static void CollectBareTrueSchemas(JsonElement schema, string path, List<string> bare)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (schema.TryGetProperty("properties", out var properties))
        {
            foreach (var property in properties.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.True)
                {
                    bare.Add($"{path}.properties.{property.Name}");
                }

                CollectBareTrueSchemas(property.Value, $"{path}.properties.{property.Name}", bare);
            }
        }

        if (schema.TryGetProperty("items", out var items))
        {
            if (items.ValueKind == JsonValueKind.True)
            {
                bare.Add($"{path}.items");
            }

            CollectBareTrueSchemas(items, $"{path}.items", bare);
        }
    }

    private static async Task<CallToolResult> Call(Type toolType, string methodName, string argumentsJson, ServiceProvider services)
    {
        var tool = McpServerTool.Create(
            toolType.GetMethod(methodName),
            target: null,
            new McpServerToolCreateOptions { Services = services, SerializerOptions = McpSerialization.Options });
        var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(argumentsJson);
        await using var server = McpServer.Create(new StreamServerTransport(new MemoryStream(), new MemoryStream()), new McpServerOptions(), null, services);
        var context = new RequestContext<CallToolRequestParams>(
            server,
            new JsonRpcRequest { Method = RequestMethods.ToolsCall },
            new CallToolRequestParams { Name = tool.ProtocolTool.Name, Arguments = arguments })
        {
            Services = services,
        };
        var filter = new UcpMcpCallToolFilter(new UcpOperationTelemetry(NullLogger<UcpOperationTelemetry>.Instance), NullLogger<UcpMcpCallToolFilter>.Instance);

        return await filter.InvokeAsync((request, cancellationToken) => tool.InvokeAsync(request, cancellationToken), context, TestContext.Current.CancellationToken);
    }

    private static ServiceProvider CreateServices(
        FakeCatalogService catalog = null,
        FakeCartService cart = null,
        FakeCheckoutService checkout = null,
        FakeOrderService orders = null,
        IUcpBuyerContextAccessor buyerContextAccessor = null)
    {
        return new ServiceCollection()
            .AddSingleton<IUcpProfileService>(new FakeProfileService())
            .AddSingleton<IUcpCatalogService>(catalog ?? new FakeCatalogService())
            .AddSingleton<IUcpCartService>(cart ?? new FakeCartService())
            .AddSingleton<IUcpCheckoutService>(checkout ?? new FakeCheckoutService())
            .AddSingleton<IUcpOrderService>(orders ?? new FakeOrderService())
            .AddSingleton(buyerContextAccessor ?? new TestBuyerContextAccessor())
            .AddSingleton<UcpStandardMapper>()
            .BuildServiceProvider();
    }

    private static JsonElement AssertStandardResult(CallToolResult result)
    {
        Assert.NotEqual(true, result.IsError);
        Assert.NotNull(result.StructuredContent);
        var payload = result.StructuredContent.Value;
        // The same payload as text, for clients without structured content.
        Assert.Equal(payload.GetRawText(), Assert.IsType<TextContentBlock>(result.Content[0]).Text);

        return payload;
    }

    private static void AssertStandardError(CallToolResult result, string capability, string code, string severity)
    {
        Assert.True(result.IsError);
        var payload = result.StructuredContent.Value;
        Assert.Equal(payload.GetRawText(), Assert.IsType<TextContentBlock>(result.Content[0]).Text);
        // error_response.json: ucp (status error) and at least one message; nothing else but continue_url.
        Assert.All(payload.EnumerateObject(), property => Assert.Contains(property.Name, ErrorResponseMembers));
        Assert.Equal(Version, payload.GetProperty("ucp").GetProperty("version").GetString());
        Assert.Equal("error", payload.GetProperty("ucp").GetProperty("status").GetString());
        Assert.True(payload.GetProperty("ucp").GetProperty("capabilities").TryGetProperty(capability, out _));
        var message = Assert.Single(payload.GetProperty("messages").EnumerateArray());
        AssertRequired(message, "type", "code", "content", "severity");
        Assert.Equal("error", message.GetProperty("type").GetString());
        Assert.Equal(code, message.GetProperty("code").GetString());
        Assert.Equal(severity, message.GetProperty("severity").GetString());
    }

    private static void AssertUcp(JsonElement payload, string capability)
    {
        var ucp = payload.GetProperty("ucp");
        Assert.Equal(Version, ucp.GetProperty("version").GetString());
        Assert.Equal("success", ucp.GetProperty("status").GetString());
        var capabilities = ucp.GetProperty("capabilities").EnumerateObject().ToList();
        Assert.Equal(capability, Assert.Single(capabilities).Name);
        Assert.Equal(Version, Assert.Single(capabilities[0].Value.EnumerateArray()).GetProperty("version").GetString());
    }

    private static void AssertProduct(JsonElement product)
    {
        AssertRequired(product, "id", "title", "description", "price_range", "variants");
        Assert.True(product.GetProperty("description").TryGetProperty("plain", out _));
        AssertRequired(product.GetProperty("price_range"), "min", "max");
        Assert.NotEqual(0, product.GetProperty("variants").GetArrayLength());
        foreach (var variant in product.GetProperty("variants").EnumerateArray())
        {
            AssertRequired(variant, "id", "title", "description", "price");
            AssertRequired(variant.GetProperty("price"), "amount", "currency");
            Assert.Equal(JsonValueKind.Number, variant.GetProperty("price").GetProperty("amount").ValueKind);
        }
    }

    private static void AssertCart(JsonElement payload, string capability)
    {
        AssertUcp(payload, capability);
        AssertRequired(payload, "ucp", "id", "line_items", "currency", "totals");
        AssertLineItems(payload.GetProperty("line_items"));
        AssertTotals(payload.GetProperty("totals"));
    }

    private static void AssertCheckout(JsonElement payload)
    {
        AssertUcp(payload, ModuleConstants.Discovery.Checkout);
        AssertRequired(payload, "ucp", "id", "line_items", "status", "currency", "totals", "links");
        Assert.Equal(JsonValueKind.Object, payload.GetProperty("ucp").GetProperty("payment_handlers").ValueKind);
        Assert.Contains(payload.GetProperty("status").GetString(), CheckoutStatuses);
        Assert.Equal(JsonValueKind.Array, payload.GetProperty("links").ValueKind);
        AssertLineItems(payload.GetProperty("line_items"));
        AssertTotals(payload.GetProperty("totals"));
    }

    private static void AssertLineItems(JsonElement lineItems)
    {
        foreach (var line in lineItems.EnumerateArray())
        {
            AssertRequired(line, "id", "item", "quantity", "totals");
            AssertRequired(line.GetProperty("item"), "id", "title", "price");
            Assert.True(line.GetProperty("quantity").GetInt32() >= 1);
        }
    }

    // totals.json: exactly one subtotal and one total; discounts negative.
    private static void AssertTotals(JsonElement totals)
    {
        var entries = totals.EnumerateArray().ToList();
        Assert.Single(entries, x => x.GetProperty("type").GetString() == "subtotal");
        Assert.Single(entries, x => x.GetProperty("type").GetString() == "total");
        Assert.All(entries.Where(x => x.GetProperty("type").GetString() == "discount"), x => Assert.True(x.GetProperty("amount").GetInt64() < 0));
    }

    private static void AssertRequired(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            Assert.True(element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null, $"'{name}' is required.");
        }
    }

    private static UcpCart BuildCart(string id = "cart-1")
    {
        return new UcpCart
        {
            Id = id,
            StoreId = "B2B-store",
            Currency = "USD",
            BuyerId = AnonymousBuyerId,
            ContinueUrl = $"https://store.example/cart/{id}",
            LineItems =
            {
                new UcpCartLineItem
                {
                    Id = "line-1",
                    ProductId = "printer-1",
                    Name = "Printer",
                    Quantity = 2,
                    UnitPrice = Money(19900),
                    LineTotal = Money(39800),
                },
            },
            Totals = new UcpCartTotals
            {
                Subtotal = Money(39800),
                DiscountTotal = Money(1000),
                ShippingTotal = Money(500),
                TaxTotal = Money(0),
                Total = Money(39300),
            },
        };
    }

    private static UcpMoney Money(long amount)
    {
        return new UcpMoney { Amount = amount, Currency = "USD" };
    }

    private sealed class FakeProfileService : IUcpProfileService
    {
        public Task<UcpProfile> GetProfile(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new UcpProfile
            {
                DefaultStoreId = "B2B-store",
                Stores =
                {
                    new UcpStoreProfile { Id = "B2B-store", SecureUrl = "https://store.example", DefaultCurrency = "USD", DefaultLanguage = "en-US" },
                },
            });
        }
    }

    private sealed class FakeCatalogService : IUcpCatalogService
    {
        public UcpCatalogSearchRequest LastRequest { get; private set; }
        public string LastProductId { get; private set; }

        public Task<UcpCatalogSearchResponse> SearchProducts(UcpCatalogSearchRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(new UcpCatalogSearchResponse
            {
                Products = { CreateProduct() },
                Pagination = new UcpPaginationResponse { Cursor = "next", HasNextPage = true, TotalCount = 3 },
            });
        }

        public Task<UcpProductResponse> GetProduct(string productId, UcpCatalogSearchRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            LastProductId = productId;
            if (!productId.StartsWith("printer-1", StringComparison.Ordinal))
            {
                throw new UcpException(ModuleConstants.ErrorCodes.ProductNotFound, $"Product '{productId}' was not found.", 404);
            }

            return Task.FromResult(new UcpProductResponse { Product = CreateProduct() });
        }

        private static UcpProduct CreateProduct()
        {
            return new UcpProduct
            {
                Id = "printer-1",
                Code = "PRN-1",
                Name = "Printer",
                Slug = "printer",
                ImageUrl = "https://cdn.example/printer.png",
                Brand = "Acme",
                Price = Money(19900),
                Availability = new UcpProductAvailability { IsBuyable = false, IsAvailable = true, IsInStock = true },
                Variations =
                {
                    new UcpProductVariation { Id = "printer-1-black", Code = "PRN-1-BK", Name = "Black", Price = Money(19900) },
                    new UcpProductVariation { Id = "printer-1-blue", Code = "PRN-1-BL", Name = "Blue", Price = Money(24900) },
                },
            };
        }
    }

    private sealed class FakeCartService : IUcpCartService
    {
        public UcpCartRequest LastCreateRequest { get; private set; }
        public UcpCartRequest LastGetRequest { get; private set; }
        public string LastUpdateCartId { get; private set; }
        public UcpCartRequest LastUpdateRequest { get; private set; }

        public Task<UcpCartResponse> CreateCart(UcpCartRequest request, CancellationToken cancellationToken = default)
        {
            LastCreateRequest = request;
            return Task.FromResult(new UcpCartResponse { Cart = BuildCart() });
        }

        public Task<UcpCartListResponse> ListCarts(UcpCartListRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new UcpCartListResponse());
        }

        public Task<UcpCartResponse> GetCart(string cartId, UcpCartRequest request, CancellationToken cancellationToken = default)
        {
            LastGetRequest = request;
            if (cartId != "cart-1")
            {
                throw new UcpException(ModuleConstants.ErrorCodes.CartNotFound, $"Cart '{cartId}' was not found.", 404);
            }

            return Task.FromResult(new UcpCartResponse { Cart = BuildCart(cartId) });
        }

        public Task<UcpCartResponse> UpdateCart(string cartId, UcpCartRequest request, CancellationToken cancellationToken = default)
        {
            LastUpdateCartId = cartId;
            LastUpdateRequest = request;
            return Task.FromResult(new UcpCartResponse { Cart = BuildCart(cartId) });
        }

        public Task<UcpCartResponse> ApplyCheckoutData(string cartId, UcpCheckoutRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new UcpCartResponse { Cart = BuildCart(cartId) });
        }
    }

    // Answers as UcpCheckoutService does: the checkout id is the cart id; a handoff requires escalation.
    private sealed class FakeCheckoutService : IUcpCheckoutService
    {
        public UcpCheckoutAddress ShippingAddress { get; init; }
        public UcpCheckoutRequest LastCreateRequest { get; private set; }
        public string LastUpdateCheckoutId { get; private set; }
        public UcpCheckoutRequest LastUpdateRequest { get; private set; }
        public string LastHandoffCheckoutId { get; private set; }

        public Task<UcpCheckoutResponse> CreateCheckout(UcpCheckoutRequest request, CancellationToken cancellationToken = default)
        {
            LastCreateRequest = request;
            return Task.FromResult(CreateResponse(request, "incomplete", "handoff_required"));
        }

        public Task<UcpCheckoutResponse> UpdateCheckout(string checkoutId, UcpCheckoutRequest request, CancellationToken cancellationToken = default)
        {
            LastUpdateCheckoutId = checkoutId;
            LastUpdateRequest = request;
            return Task.FromResult(CreateResponse(request, "incomplete", "checkout_updated"));
        }

        public Task<UcpPaymentHandlersResponse> GetPaymentHandlers(string checkoutId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new UcpPaymentHandlersResponse());
        }

        public Task<UcpCheckoutHandoffResponse> HandoffCheckout(string checkoutId, UcpCheckoutRequest request, CancellationToken cancellationToken = default)
        {
            LastHandoffCheckoutId = checkoutId;
            var response = CreateResponse(request, "requires_escalation", "hosted_checkout_required");
            response.Checkout.ContinueUrl = "https://store.example/checkout?ucp_session=token";
            response.Checkout.IssuedAt = new DateTimeOffset(2026, 6, 16, 9, 30, 15, TimeSpan.Zero);
            response.Checkout.ExpiresAt = new DateTimeOffset(2026, 6, 16, 9, 45, 15, TimeSpan.Zero);

            return Task.FromResult(new UcpCheckoutHandoffResponse { Checkout = response.Checkout, Messages = response.Messages });
        }

        public Task<UcpHandoffRestoreResponse> RestoreHandoff(UcpHandoffRestoreRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<UcpHandoffRestoreResponse>(null);
        }

        private UcpCheckoutResponse CreateResponse(UcpCheckoutRequest request, string status, string infoCode)
        {
            var cart = BuildCart(request.CartId);
            var shippingAddress = request.ShippingAddress ?? ShippingAddress;
            var checkout = new UcpCheckout
            {
                Id = cart.Id,
                CartId = cart.Id,
                Status = status,
                Cart = cart,
                Buyer = new UcpCheckoutBuyer { Id = cart.BuyerId, Name = request.Buyer?.Name, Email = request.Buyer?.Email },
                ShippingAddress = shippingAddress,
            };
            checkout.Messages.Add(new UcpMessage { Type = "info", Code = infoCode, Content = "Our own guidance.", Severity = "info" });
            if (shippingAddress == null)
            {
                checkout.Messages.Add(new UcpMessage { Type = "warning", Code = "shipping_address_missing", Content = "Shipping address is not set.", Severity = "warning" });
            }

            return new UcpCheckoutResponse { Checkout = checkout, Messages = checkout.Messages };
        }
    }

    private sealed class FakeOrderService : IUcpOrderService
    {
        public UcpOrderTrackingRequest LastRequest { get; private set; }

        public Task<UcpOrderResponse> TrackOrder(UcpOrderTrackingRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            if (request.OrderId != "CO-1001")
            {
                throw new UcpException(ModuleConstants.ErrorCodes.OrderNotFound, $"Order '{request.OrderId}' was not found.", 404);
            }

            var order = new UcpOrder
            {
                Id = "order-1",
                Number = "CO-1001",
                Status = "Processing",
                CartId = "cart-1",
                StoreId = "B2B-store",
                Currency = "USD",
                Totals = new UcpOrderTotals { Subtotal = Money(39800), ShippingTotal = Money(500), Total = Money(40300) },
                LineItems =
                {
                    new UcpOrderLineItem { Id = "order-line-1", ProductId = "printer-1", Name = "Printer", Quantity = 2, PlacedPrice = Money(19900), LineTotal = Money(39800) },
                },
                Shipments =
                {
                    new UcpOrderShipment
                    {
                        Id = "shipment-1",
                        ShipmentMethodCode = "FixedRate",
                        DeliveryAddress = new UcpOrderAddress { FirstName = "Jane", LastName = "Doe", Line1 = "1 Main St", City = "Springfield", PostalCode = "62701", CountryCode = "US" },
                    },
                },
            };

            return Task.FromResult(new UcpOrderResponse { Order = order, Orders = { order } });
        }
    }

    private sealed class SignedInBuyerContextAccessor : IUcpBuyerContextAccessor
    {
        public UcpBuyerContext Resolve(UcpBuyerContextRequest request = null)
        {
            return new UcpBuyerContext { UserId = "user-1", PublicBuyerId = "user-1", IsAuthenticated = true };
        }
    }
}
