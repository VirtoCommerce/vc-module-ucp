using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Models;
using VirtoCommerce.UCP.Core.Options;
using VirtoCommerce.UCP.Core.Services;
using VirtoCommerce.UCP.Data.Caching;
using VirtoCommerce.UCP.Data.Services;
using VirtoCommerce.UCP.Web.Mcp;
using Xunit;

namespace VirtoCommerce.UCP.Tests;

public partial class UcpCartServiceTests
{
    [Theory]
    [InlineData(ModuleConstants.McpTools.CreateCart)]
    [InlineData(ModuleConstants.McpTools.UpdateCart)]
    [InlineData(ModuleConstants.McpTools.CreateCheckout)]
    [InlineData(ModuleConstants.McpTools.UpdateCheckout)]
    [InlineData(ModuleConstants.McpTools.CheckoutAndHandoff)]
    public async Task Mcp_RealCommerceServicesRejectNestedInventoryErrors(string tool)
    {
        var operation = tool == ModuleConstants.McpTools.CreateCart ? "addItem" : "cart";
        var executor = new StubXApiExecutor(InventoryCartJson(operation, "PRODUCT_FFC_QTY", "availableQty", "0", false));
        var carts = CreateService(executor);
        var checkout = new UcpCheckoutService(carts, new InMemoryUcpHandoffSessionStore(), null,
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() }, Options.Create(new UcpOptions()));
        var profile = new UcpInventoryErrorContractTests.ProfileService();
        var lines = new[] { new UcpCartLineItemRequest { Id = "line-1", ProductId = "product-1", Quantity = 5 } };
        var result = await UcpInventoryErrorContractTests.InvokeTool(tool, () => tool switch
        {
            ModuleConstants.McpTools.CreateCart => UcpMcpCommerceTools.CreateCart(profile, carts, lines, cancellationToken: TestContext.Current.CancellationToken),
            ModuleConstants.McpTools.UpdateCart => UcpMcpCommerceTools.UpdateCart(profile, carts, "cart-1", lines, cancellationToken: TestContext.Current.CancellationToken),
            ModuleConstants.McpTools.CreateCheckout => UcpMcpCommerceTools.CreateCheckout(profile, checkout, "cart-1", cancellationToken: TestContext.Current.CancellationToken),
            ModuleConstants.McpTools.UpdateCheckout => UcpMcpCommerceTools.UpdateCheckout(profile, checkout, "cart-1", cancellationToken: TestContext.Current.CancellationToken),
            _ => UcpMcpCommerceTools.CheckoutAndHandoff(profile, checkout, "cart-1", cancellationToken: TestContext.Current.CancellationToken),
        });
        Assert.True(result.IsError);
        var content = result.StructuredContent.Value;
        Assert.Equal("out_of_stock", content.GetProperty("code").GetString());
        Assert.Equal(5, content.GetProperty("details").GetProperty("requested_quantity").GetInt64());
        Assert.Equal(0, content.GetProperty("details").GetProperty("available_quantity").GetInt64());
        Assert.True(content.GetProperty("details").GetProperty("operation_rejected").GetBoolean());
        Assert.False(content.TryGetProperty("cart", out _));
        Assert.False(content.TryGetProperty("checkout", out _));
        Assert.Single(executor.Requests);
    }

    [Theory]
    [InlineData("PRODUCT_FFC_QTY", "availableQty", "0", "out_of_stock", true)]
    [InlineData("PRODUCT_FFC_QTY", "availableQty", "2", "insufficient_stock", false)]
    [InlineData("PRODUCT_QTY_INSUFFICIENT", "availQty", "2", "insufficient_stock", true)]
    [InlineData("PRODUCT_QTY_CHANGED", "availQty", "2", "insufficient_stock", false)]
    [InlineData("PRODUCT_MIN_QTY_NOT_AVAILABLE", "minQty", "5", "inventory_unavailable", true)]
    public async Task CreateCart_RejectsInventoryErrorsAndStopsFurtherMutations(
        string sourceCode, string parameterKey, string available, string expectedCode, bool cartLevel)
    {
        var json = InventoryCartJson("addItem", sourceCode, parameterKey, available, cartLevel);
        var executor = new StubXApiExecutor(json);
        var exception = await Assert.ThrowsAsync<UcpException>(() => CreateService(executor).CreateCart(new UcpCartRequest
        {
            LineItems =
            {
                new UcpCartLineItemRequest { ProductId = "product-1", Quantity = 5 },
                new UcpCartLineItemRequest { ProductId = "product-2", Quantity = 1 },
            },
            Coupons = { "SAVE10" },
        }, TestContext.Current.CancellationToken));

        Assert.Equal(expectedCode, exception.Code);
        Assert.Equal(409, exception.StatusCode);
        Assert.Equal("product-1", exception.Error.Details["product_id"]);
        Assert.Equal("cart-1", exception.Error.Details["cart_id"]);
        Assert.Equal(5L, exception.Error.Details["requested_quantity"]);
        Assert.Equal(true, exception.Error.Details["operation_rejected"]);
        Assert.Single(executor.Requests);
        Assert.Contains("errorParameters { key value }", executor.Requests[0].Query);
        if (expectedCode == "inventory_unavailable")
        {
            Assert.False(exception.Error.Details.ContainsKey("available_quantity"));
        }
        else
        {
            Assert.Equal(long.Parse(available, System.Globalization.CultureInfo.InvariantCulture), exception.Error.Details["available_quantity"]);
            Assert.Equal(available != "0", exception.Error.Details["retryable"]);
        }
    }

    [Fact]
    public async Task UpdateCart_ReportsRequestedQuantityEvenWhenXCartKeepsOldQuantity()
    {
        var rejected = JsonNode.Parse(InventoryCartJson("changeCartItemQuantity", "PRODUCT_FFC_QTY", "availableQty", "2", false));
        rejected["data"]["changeCartItemQuantity"]["items"][0]["quantity"] = 1;
        rejected["data"]["changeCartItemQuantity"]["items"][0]["validationErrors"][0]["errorParameters"] = new JsonArray
        {
            new JsonObject { ["key"] = "availableQty", ["value"] = "2" },
        };
        var executor = new StubXApiExecutor(InventoryCartJson("cart"), rejected.ToJsonString());
        var exception = await Assert.ThrowsAsync<UcpException>(() => CreateService(executor).UpdateCart("cart-1", new UcpCartRequest
        {
            LineItems = { new UcpCartLineItemRequest { Id = "line-1", Quantity = 5 } },
        }, TestContext.Current.CancellationToken));

        Assert.Equal("insufficient_stock", exception.Code);
        Assert.Equal(5L, exception.Error.Details["requested_quantity"]);
        Assert.Equal("line-1", exception.Error.Details["line_item_id"]);
        Assert.Equal(2, executor.Requests.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("garbage")]
    [InlineData("-1")]
    public async Task CreateCart_DoesNotInventMissingOrInvalidAvailability(string available)
    {
        var executor = new StubXApiExecutor(InventoryCartJson("addItem", "PRODUCT_FFC_QTY", "availableQty", available, true));
        var exception = await Assert.ThrowsAsync<UcpException>(() => CreateService(executor).CreateCart(new UcpCartRequest
        {
            LineItems = { new UcpCartLineItemRequest { ProductId = "product-1", Quantity = 5 } },
        }, TestContext.Current.CancellationToken));

        Assert.Equal("insufficient_stock", exception.Code);
        Assert.False(exception.Error.Details.ContainsKey("available_quantity"));
        Assert.Equal(false, exception.Error.Details["retryable"]);
    }

    [Fact]
    public async Task CreateCart_ReportsProductNotAddedWithoutInventingLineId()
    {
        var root = JsonNode.Parse(InventoryCartJson("addItem", "PRODUCT_FFC_QTY", "availableQty", "0", true));
        root["data"]["addItem"]["items"] = new JsonArray();
        var executor = new StubXApiExecutor(root.ToJsonString());
        var exception = await Assert.ThrowsAsync<UcpException>(() => CreateService(executor).CreateCart(new UcpCartRequest
        {
            LineItems = { new UcpCartLineItemRequest { ProductId = "product-1", Quantity = 5 } },
        }, TestContext.Current.CancellationToken));

        Assert.Equal("product-1", exception.Error.Details["product_id"]);
        Assert.False(exception.Error.Details.ContainsKey("line_item_id"));
    }

    [Fact]
    public async Task GetCart_ExposesInventoryDetailsSoBuyerCanRepairCart()
    {
        var response = await CreateService(new StubXApiExecutor(
            InventoryCartJson("cart", "PRODUCT_QTY_CHANGED", "availQty", "2", false))).GetCart("cart-1", new UcpCartRequest(), TestContext.Current.CancellationToken);
        var item = Assert.Single(response.Cart.LineItems);
        Assert.Equal("insufficient_stock", item.InventoryStatus);
        Assert.Equal(5L, item.RequestedQuantity);
        Assert.Equal(2L, item.AvailableQuantity);
        Assert.Single(response.Cart.InventoryErrors);
        Assert.Null(response.Cart.ContinueUrl);
    }

    [Fact]
    public async Task UpdateCart_CanRepairMultipleInvalidItemsInOneCall()
    {
        var initial = JsonNode.Parse(InventoryCartJson("cart", "PRODUCT_QTY_CHANGED", "availQty", "2", false));
        var cart = initial["data"]["cart"];
        var other = cart["items"][0].DeepClone();
        other["id"] = "line-2";
        other["productId"] = "product-2";
        other["validationErrors"][0]["objectId"] = "line-2";
        cart["items"].AsArray().Add(other);
        var afterFirst = cart.DeepClone();
        afterFirst["items"][0]["quantity"] = 2;
        afterFirst["items"][0]["validationErrors"] = new JsonArray();
        var afterSecond = afterFirst.DeepClone();
        afterSecond["items"][1]["quantity"] = 2;
        afterSecond["items"][1]["validationErrors"] = new JsonArray();
        var executor = new StubXApiExecutor(initial.ToJsonString(),
            new JsonObject { ["data"] = new JsonObject { ["changeCartItemQuantity"] = afterFirst } }.ToJsonString(),
            new JsonObject { ["data"] = new JsonObject { ["changeCartItemQuantity"] = afterSecond } }.ToJsonString());
        var response = await CreateService(executor).UpdateCart("cart-1", new UcpCartRequest
        {
            LineItems =
            {
                new UcpCartLineItemRequest { Id = "line-1", Quantity = 2 },
                new UcpCartLineItemRequest { Id = "line-2", Quantity = 2 },
            },
        }, TestContext.Current.CancellationToken);
        Assert.Equal(3, executor.Requests.Count);
        Assert.Empty(response.Cart.InventoryErrors);
        Assert.All(response.Cart.LineItems, item => Assert.Equal(2, item.Quantity));
    }

    [Fact]
    public async Task UpdateCart_MapsCartProductErrorWithNewQuantityParameter()
    {
        var root = JsonNode.Parse(InventoryCartJson("changeCartItemQuantity", "PRODUCT_QTY_INSUFFICIENT", "availQty", "2", true));
        var error = root["data"]["changeCartItemQuantity"]["validationErrors"][0];
        error["objectType"] = "CartProduct";
        error["errorParameters"][0]["key"] = "new_qty";
        var executor = new StubXApiExecutor(InventoryCartJson("cart"), root.ToJsonString());
        var exception = await Assert.ThrowsAsync<UcpException>(() => CreateService(executor).UpdateCart("cart-1", new UcpCartRequest
        {
            LineItems = { new UcpCartLineItemRequest { Id = "line-1", Quantity = 5 } },
        }, TestContext.Current.CancellationToken));
        Assert.Equal("product-1", exception.Error.Details["product_id"]);
        Assert.Equal("line-1", exception.Error.Details["line_item_id"]);
        Assert.Equal(5L, exception.Error.Details["requested_quantity"]);
    }

    [Fact]
    public async Task UpdateCart_AllowsRepairingExistingInventoryError()
    {
        var invalid = InventoryCartJson("cart", "PRODUCT_QTY_CHANGED", "availQty", "2", false);
        var valid = JsonNode.Parse(InventoryCartJson("changeCartItemQuantity"));
        valid["data"]["changeCartItemQuantity"]["items"][0]["quantity"] = 2;
        var executor = new StubXApiExecutor(invalid, valid.ToJsonString());
        var response = await CreateService(executor).UpdateCart("cart-1", new UcpCartRequest
        {
            LineItems = { new UcpCartLineItemRequest { Id = "line-1", Quantity = 2 } },
        }, TestContext.Current.CancellationToken);
        Assert.Empty(response.Cart.InventoryErrors);
        Assert.Equal(2, Assert.Single(response.Cart.LineItems).Quantity);
    }

    [Fact]
    public async Task UpdateCart_NoOpCannotReportSuccessForInvalidCart()
    {
        var executor = new StubXApiExecutor(InventoryCartJson("cart", "PRODUCT_QTY_CHANGED", "availQty", "2", false));
        await Assert.ThrowsAsync<UcpException>(() => CreateService(executor).UpdateCart("cart-1", new UcpCartRequest
        {
            LineItems = { new UcpCartLineItemRequest { Id = "line-1", Quantity = 5 } },
        }, TestContext.Current.CancellationToken));
        Assert.Single(executor.Requests);
    }

    [Fact]
    public async Task ApplyCheckoutData_RejectsInventoryBeforeAddressMutations()
    {
        var executor = new StubXApiExecutor(InventoryCartJson("cart", "PRODUCT_FFC_QTY", "availableQty", "0", false));
        await Assert.ThrowsAsync<UcpException>(() => CreateService(executor).ApplyCheckoutData("cart-1", new UcpCheckoutRequest
        {
            ShippingAddress = new UcpCheckoutAddress { FirstName = "Buyer", LastName = "Name" },
        }, TestContext.Current.CancellationToken));
        Assert.Equal("UcpGetCart", Assert.Single(executor.Requests).OperationName);
    }

    [Theory]
    [InlineData("PRODUCT_PRICE_INVALID")]
    [InlineData("PRODUCT_MAX_QTY")]
    [InlineData("PRODUCT_PACK_SIZE_LIMIT")]
    [InlineData("CART_PRODUCT_UNAVAILABLE")]
    public async Task CreateCart_DoesNotMisclassifyNonInventoryValidation(string code)
    {
        var executor = new StubXApiExecutor(InventoryCartJson("addItem", code, "maxQty", "2", true));
        var response = await CreateService(executor).CreateCart(new UcpCartRequest
        {
            LineItems = { new UcpCartLineItemRequest { ProductId = "product-1", Quantity = 5 } },
        }, TestContext.Current.CancellationToken);
        Assert.Empty(response.Cart.InventoryErrors);
        Assert.Equal(code, Assert.Single(response.Messages).Code);
    }

    [Fact]
    public async Task CreateCart_FinalValidationRejectsExistingErrorOnAnotherProduct()
    {
        var executor = new StubXApiExecutor(InventoryCartJson("addItem", "PRODUCT_FFC_QTY", "availableQty", "0", false));
        var exception = await Assert.ThrowsAsync<UcpException>(() => CreateService(executor).CreateCart(new UcpCartRequest
        {
            LineItems = { new UcpCartLineItemRequest { ProductId = "product-2", Quantity = 1 } },
        }, TestContext.Current.CancellationToken));
        Assert.Equal("out_of_stock", exception.Code);
        Assert.Equal("product-1", exception.Error.Details["product_id"]);
    }

    [Fact]
    public async Task UpdateCart_DoesNotDiscardErrorWhenResponseOmitsRelatedLine()
    {
        var root = JsonNode.Parse(InventoryCartJson("changeCartItemQuantity", "PRODUCT_QTY_INSUFFICIENT", "availQty", "2", true));
        root["data"]["changeCartItemQuantity"]["items"] = new JsonArray();
        root["data"]["changeCartItemQuantity"]["validationErrors"][0]["objectType"] = "CartProduct";
        var executor = new StubXApiExecutor(InventoryCartJson("cart"), root.ToJsonString());
        var exception = await Assert.ThrowsAsync<UcpException>(() => CreateService(executor).UpdateCart("cart-1", new UcpCartRequest
        {
            LineItems =
            {
                new UcpCartLineItemRequest { Id = "line-1", Quantity = 5 },
                new UcpCartLineItemRequest { ProductId = "product-2", Quantity = 1 },
            },
        }, TestContext.Current.CancellationToken));
        Assert.Equal("insufficient_stock", exception.Code);
        Assert.Equal(2, executor.Requests.Count);
    }

    [Fact]
    public async Task CreateCart_PartialGraphQlFailureStillReturnsMcpError()
    {
        var root = JsonNode.Parse(InventoryCartJson("addItem", "PRODUCT_FFC_QTY", "availableQty", "0", false));
        root["errors"] = new JsonArray(new JsonObject { ["message"] = "A resolver failed." });
        var executor = new StubXApiExecutor(root.ToJsonString());
        var result = await UcpInventoryErrorContractTests.InvokeTool(ModuleConstants.McpTools.CreateCart, () =>
            UcpMcpCommerceTools.CreateCart(new UcpInventoryErrorContractTests.ProfileService(), CreateService(executor),
                [new UcpCartLineItemRequest { ProductId = "product-1", Quantity = 5 }], cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(result.IsError);
        Assert.True(result.StructuredContent.Value.TryGetProperty("errors", out _));
        Assert.Single(executor.Requests);
    }

    [Fact]
    public async Task GetCart_DoesNotGuessLineForProductErrorWithMultipleMatchingLines()
    {
        var root = JsonNode.Parse(InventoryCartJson("cart", "PRODUCT_FFC_QTY", "availableQty", "0", true));
        var other = root["data"]["cart"]["items"][0].DeepClone();
        other["id"] = "line-2";
        root["data"]["cart"]["items"].AsArray().Add(other);
        var response = await CreateService(new StubXApiExecutor(root.ToJsonString())).GetCart("cart-1", new UcpCartRequest(), TestContext.Current.CancellationToken);
        var error = Assert.Single(response.Cart.InventoryErrors);
        Assert.Equal("product-1", error.Details["product_id"]);
        Assert.False(error.Details.ContainsKey("line_item_id"));
    }

    private static string InventoryCartJson(string operation, string errorCode = null, string parameterKey = null,
        string available = null, bool cartLevel = false)
    {
        var cart = JsonNode.Parse(CartWithOneItemJson)["data"]["addItem"].DeepClone();
        if (errorCode != null)
        {
            var error = new JsonObject
            {
                ["errorCode"] = errorCode,
                ["errorMessage"] = "XCart validation failed.",
                ["objectType"] = cartLevel ? "CatalogProduct" : "LineItem",
                ["objectId"] = cartLevel ? "product-1" : "line-1",
                ["errorParameters"] = new JsonArray
                {
                    new JsonObject { ["key"] = "qty", ["value"] = "5" },
                    new JsonObject { ["key"] = parameterKey, ["value"] = available },
                },
            };
            var owner = cartLevel ? cart : cart["items"][0];
            owner["validationErrors"] = new JsonArray(error);
            cart["items"][0]["quantity"] = 5;
        }
        return new JsonObject { ["data"] = new JsonObject { [operation] = cart } }.ToJsonString();
    }
}
