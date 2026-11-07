using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Models;
using VirtoCommerce.UCP.Core.Options;
using VirtoCommerce.UCP.Web;
using VirtoCommerce.UCP.Web.Diagnostics;
using VirtoCommerce.UCP.Web.Models;
using Xunit;

namespace VirtoCommerce.UCP.Tests;

[Trait("Category", "Unit")]
public class UcpOperationInputContractTests
{
    private const string SecretEmail = "secret@example.com";
    private const string SecretAddress = "123 Secret Street";
    private const string SecretNotes = "private checkout note";
    private const string SecretCoupon = "SAVE50-SECRET";
    private const string SecretSession = "SESSION-TOKEN-SECRET";

    private static IEnumerable<McpInputCase> McpCases()
    {
        yield return Mcp(ModuleConstants.Operations.GetStoreCapabilities, new Dictionary<string, JsonElement>(), null);
        yield return Mcp(ModuleConstants.Operations.SearchProducts, Args(("query", "microwave-marker"), ("store_id", "store-marker"), ("price_min", 1000), ("limit", 10)), "microwave-marker");
        yield return Mcp(ModuleConstants.Operations.GetProduct, Args(("product_id", "product-marker"), ("store_id", "store-marker")), "product-marker");
        yield return Mcp(ModuleConstants.Operations.CreateCart, Args(("line_items", new[] { new { id = "line-marker", product_id = "product-marker", quantity = 2 } }), ("coupons", new[] { SecretCoupon })), "product-marker");
        yield return Mcp(ModuleConstants.Operations.ListCarts, Args(("buyer_id", "buyer-marker"), ("cursor", "cursor-secret"), ("limit", 5)), "buyer_id_present");
        yield return Mcp(ModuleConstants.Operations.GetCart, Args(("cart_id", "cart-marker")), "cart-marker");
        yield return Mcp(ModuleConstants.Operations.UpdateCart, Args(("cart_id", "cart-marker"), ("line_items", new[] { new { id = "line-marker", product_id = "product-marker", quantity = 3 } }), ("coupons", new[] { SecretCoupon })), "product-marker");
        yield return Mcp(ModuleConstants.Operations.CreateCheckout, CheckoutArgs(("cart_id", "cart-marker")), "cart-marker");
        yield return Mcp(ModuleConstants.Operations.UpdateCheckout, CheckoutArgs(("checkout_id", "checkout-marker"), ("cart_id", "cart-marker")), "checkout-marker");
        yield return Mcp(ModuleConstants.Operations.GetPaymentHandlers, Args(("checkout_id", "checkout-marker")), "checkout-marker");
        yield return Mcp(ModuleConstants.Operations.CheckoutAndHandoff, CheckoutArgs(("cart_id", "cart-marker")), "cart-marker");
        yield return Mcp(ModuleConstants.Operations.HandoffCheckout, CheckoutArgs(("checkout_id", "checkout-marker")), "checkout-marker");
        yield return Mcp(ModuleConstants.Operations.TrackOrder, Args(("order_number", "order-marker"), ("buyer_id", "buyer-marker")), "order-marker");
        yield return Mcp(ModuleConstants.Operations.ListCountries, Args(("query", "country-marker"), ("limit", 10)), "country-marker");
        yield return Mcp(ModuleConstants.Operations.ResolveCountry, Args(("query", "resolve-marker")), "resolve-marker");
        yield return Mcp(ModuleConstants.Operations.ListRegions, Args(("country_id", "country-marker")), "country-marker");
    }

    private static IEnumerable<RestInputCase> RestCases()
    {
        yield return Rest(ModuleConstants.Operations.GetStoreCapabilities, new Dictionary<string, object>(), null);
        yield return Rest(ModuleConstants.Operations.SearchProducts, new Dictionary<string, object>()
        {
            ["request"] = new UcpCatalogSearchRequest { Query = "microwave-marker", StoreId = "store-marker", Filters = new UcpSearchFilters { Price = new UcpPriceFilter { Min = 1000, Max = 5000 } } },
        }, "microwave-marker");
        yield return Rest(ModuleConstants.Operations.GetProduct, new Dictionary<string, object> { ["id"] = "product-marker", ["query"] = new UcpCatalogProductQuery { StoreId = "store-marker" } }, "product-marker");
        yield return Rest(ModuleConstants.Operations.CreateCart, new Dictionary<string, object> { ["request"] = CartRequest() }, "product-marker");
        yield return Rest(ModuleConstants.Operations.ListCarts, new Dictionary<string, object> { ["query"] = new UcpCartListQuery { BuyerId = "buyer-marker", Cursor = "cursor-secret", Limit = 5 } }, "buyer_id_present");
        yield return Rest(ModuleConstants.Operations.GetCart, new Dictionary<string, object> { ["cartId"] = "cart-marker", ["query"] = new UcpCartQuery { StoreId = "store-marker" } }, "cart-marker");
        yield return Rest(ModuleConstants.Operations.UpdateCart, new Dictionary<string, object> { ["cartId"] = "cart-marker", ["request"] = CartRequest() }, "product-marker");
        yield return Rest(ModuleConstants.Operations.CreateCheckout, new Dictionary<string, object> { ["request"] = CheckoutRequest() }, "cart-marker");
        yield return Rest(ModuleConstants.Operations.UpdateCheckout, new Dictionary<string, object> { ["checkoutId"] = "checkout-marker", ["request"] = CheckoutRequest() }, "checkout-marker");
        yield return Rest(ModuleConstants.Operations.GetPaymentHandlers, new Dictionary<string, object> { ["checkoutId"] = "checkout-marker" }, "checkout-marker");
        yield return Rest(ModuleConstants.Operations.HandoffCheckout, new Dictionary<string, object> { ["checkoutId"] = "checkout-marker", ["request"] = CheckoutRequest() }, "checkout-marker");
        yield return Rest(ModuleConstants.Operations.RestoreHandoff, new Dictionary<string, object> { ["request"] = new UcpHandoffRestoreRequest { UcpSession = SecretSession } }, "session_fingerprint");
        yield return Rest(ModuleConstants.Operations.TrackOrder, new Dictionary<string, object> { ["orderId"] = "order-marker", ["query"] = new UcpOrderTrackingQuery { BuyerId = "buyer-marker" } }, "order-marker");
        yield return Rest(ModuleConstants.Operations.ListCountries, new Dictionary<string, object> { ["query"] = "country-marker", ["limit"] = 10 }, "country-marker");
        yield return Rest(ModuleConstants.Operations.ResolveCountry, new Dictionary<string, object> { ["query"] = "resolve-marker" }, "resolve-marker");
        yield return Rest(ModuleConstants.Operations.ListRegions, new Dictionary<string, object> { ["countryId"] = "country-marker" }, "country-marker");
    }

    [Fact]
    public void McpInputContract_CoversEveryToolWithoutSensitiveValues()
    {
        var cases = McpCases().ToList();

        Assert.NotEmpty(cases);
        foreach (var testCase in cases)
        {
            var inputJson = Execute(testCase.Operation, telemetry => telemetry.CaptureMcpArguments(testCase.Arguments));
            AssertInput(inputJson, testCase.ExpectedMarker);
        }
    }

    [Fact]
    public void RestInputContract_CoversEveryActionWithoutSensitiveValues()
    {
        var cases = RestCases().ToList();

        Assert.NotEmpty(cases);
        foreach (var testCase in cases)
        {
            var inputJson = Execute(testCase.Operation, telemetry => telemetry.CaptureRestArguments(testCase.Arguments));
            AssertInput(inputJson, testCase.ExpectedMarker);
        }
    }

    [Fact]
    public void InputContract_CoversAllCanonicalOperations()
    {
        var expectedMcpOperations = ModuleConstants.McpTools.UcpToolNames.OrderBy(x => x, StringComparer.Ordinal);
        var actualMcpOperations = McpCases().Select(x => x.Operation).OrderBy(x => x, StringComparer.Ordinal);
        Assert.Equal(expectedMcpOperations, actualMcpOperations);

        var expectedRestOperations = ModuleConstants.McpTools.UcpToolNames
            .Where(x => x != ModuleConstants.Operations.CheckoutAndHandoff)
            .Append(ModuleConstants.Operations.RestoreHandoff)
            .OrderBy(x => x, StringComparer.Ordinal);
        var actualRestOperations = RestCases().Select(x => x.Operation).OrderBy(x => x, StringComparer.Ordinal);
        Assert.Equal(expectedRestOperations, actualRestOperations);
    }

    [Fact]
    public void McpInputContract_DropsStructuredValuesFromStringAllowlistFields()
    {
        var productInput = Execute(
            ModuleConstants.Operations.GetProduct,
            telemetry => telemetry.CaptureMcpArguments(Args(("product_id", new { raw = SecretAddress }))));
        var cartInput = Execute(
            ModuleConstants.Operations.CreateCart,
            telemetry => telemetry.CaptureMcpArguments(Args(("line_items", new[]
            {
                new { id = "line-marker", product_id = new { raw = SecretAddress }, quantity = 1 },
            }))));

        Assert.DoesNotContain(SecretAddress, productInput, StringComparison.Ordinal);
        Assert.DoesNotContain("raw", productInput, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretAddress, cartInput, StringComparison.Ordinal);
        Assert.DoesNotContain("raw", cartInput, StringComparison.Ordinal);
    }

    [Fact]
    public void ObservabilityDefaults_CaptureInputsOnlyOnErrors()
    {
        var options = new UcpObservabilityOptions();

        Assert.Equal(UcpInputCaptureMode.ErrorsOnly, options.InputCaptureMode);
    }

    [Fact]
    public void ErrorsOnlyMode_DropsSuccessfulInputsAndKeepsFailedInputs()
    {
        var success = Execute(
            ModuleConstants.Operations.SearchProducts,
            telemetry => telemetry.CaptureMcpArguments(Args(("query", "success-marker"))),
            UcpInputCaptureMode.ErrorsOnly);
        Assert.Null(success);

        var failed = Execute(
            ModuleConstants.Operations.SearchProducts,
            telemetry =>
            {
                telemetry.CaptureMcpArguments(Args(("query", "failure-marker")));
                telemetry.MarkError("TestException");
            },
            UcpInputCaptureMode.ErrorsOnly);
        Assert.Contains("failure-marker", failed, StringComparison.Ordinal);
    }

    [Fact]
    public void CompletionEvent_ContainsReproductionInputWithoutOutputSnapshotFields()
    {
        var logger = new CaptureLogger();
        var options = Options.Create(new UcpOptions
        {
            Observability = new UcpObservabilityOptions { InputCaptureMode = UcpInputCaptureMode.Always },
        });
        var telemetry = new UcpOperationTelemetry(logger, options);
        telemetry.Begin(ModuleConstants.Operations.SearchProducts, "rest");
        telemetry.CaptureRestArguments(new Dictionary<string, object>
        {
            ["request"] = new UcpCatalogSearchRequest { Query = "microwave-marker", StoreId = "store-marker" },
        });

        telemetry.Complete();

        var entry = Assert.Single(logger.Entries);
        Assert.Contains("microwave-marker", Assert.IsType<string>(entry["InputJson"]), StringComparison.Ordinal);
        Assert.DoesNotContain("OutputJson", entry.Keys);
        Assert.DoesNotContain("OutputTruncated", entry.Keys);
    }

    [Fact]
    public void InputContract_RecordsPrivateIdentityPresenceWithoutValues()
    {
        const string privateBuyerId = "buyer-private@example.com";
        const string privateOrganizationId = "organization-private";
        const string privateCartName = "Ada private shopping list";

        var inputJson = Execute(
            ModuleConstants.Operations.CreateCart,
            telemetry => telemetry.CaptureMcpArguments(Args(
                ("buyer_id", privateBuyerId),
                ("organization_id", privateOrganizationId),
                ("cart_name", privateCartName),
                ("line_items", new[] { new { product_id = "product-marker", quantity = 1 } }))));

        using var input = JsonDocument.Parse(inputJson);
        Assert.True(input.RootElement.GetProperty("buyer_id_present").GetBoolean());
        Assert.True(input.RootElement.GetProperty("organization_id_present").GetBoolean());
        Assert.True(input.RootElement.GetProperty("cart_name_present").GetBoolean());
        Assert.DoesNotContain(privateBuyerId, inputJson, StringComparison.Ordinal);
        Assert.DoesNotContain(privateOrganizationId, inputJson, StringComparison.Ordinal);
        Assert.DoesNotContain(privateCartName, inputJson, StringComparison.Ordinal);
    }

    [Fact]
    public void InputContract_RedactsEmailAndPhoneFromDiagnosticQuery()
    {
        var inputJson = Execute(
            ModuleConstants.Operations.SearchProducts,
            telemetry => telemetry.CaptureMcpArguments(Args(
                ("query", "Carriage Bolt secret@example.com +1 555 123 4567"))));

        Assert.Contains("Carriage Bolt [redacted-email] [redacted-phone]", inputJson, StringComparison.Ordinal);
        Assert.DoesNotContain("secret@example.com", inputJson, StringComparison.Ordinal);
        Assert.DoesNotContain("555 123 4567", inputJson, StringComparison.Ordinal);
    }

    [Fact]
    public void InputContract_DoesNotRedactUnformattedNumericIdentifiersAsPhones()
    {
        const string sku = "SKU 1234567890";

        var inputJson = Execute(
            ModuleConstants.Operations.SearchProducts,
            telemetry => telemetry.CaptureMcpArguments(Args(("query", sku))));

        Assert.Contains(sku, inputJson, StringComparison.Ordinal);
        Assert.DoesNotContain("[redacted-phone]", inputJson, StringComparison.Ordinal);
    }

    [Fact]
    public void SnapshotSanitizer_UsesOriginalJsonForFallbackMetadataAfterArrayReduction()
    {
        var source = new JsonObject
        {
            ["items"] = new JsonArray(Enumerable.Range(1, 8).Select(x => JsonValue.Create(new string((char)('a' + x), 32))).ToArray()),
            ["fixed"] = new string('z', 256),
        };
        var originalJson = source.ToJsonString();
        var sanitizer = new UcpTelemetryInputSanitizer();

        var resultJson = sanitizer.SerializeBounded(source, maxLength: 96, "input_json");

        using var result = JsonDocument.Parse(resultJson);
        Assert.True(result.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(originalJson.Length, result.RootElement.GetProperty("original_length").GetInt32());
        Assert.Equal(
            UcpTelemetryInputSanitizer.ComputeFingerprint(originalJson),
            result.RootElement.GetProperty("fingerprint").GetString());
        Assert.True(sanitizer.InputTruncated);
        Assert.Contains("input_json", sanitizer.TruncatedFields, StringComparison.Ordinal);
    }

    [Fact]
    public void SnapshotSanitizer_TrimsArrayInOnePassAndKeepsBoundedPayload()
    {
        var source = new JsonObject
        {
            ["items"] = new JsonArray(Enumerable.Range(1, 20).Select(x => JsonValue.Create($"item-{x:D2}-payload")).ToArray()),
        };
        var sanitizer = new UcpTelemetryInputSanitizer();

        var resultJson = sanitizer.SerializeBounded(source, maxLength: 160, "input_json");

        using var result = JsonDocument.Parse(resultJson);
        var remainingItems = result.RootElement.GetProperty("items").GetArrayLength();
        Assert.InRange(remainingItems, 1, 19);
        Assert.True(resultJson.Length <= 160);
        Assert.True(sanitizer.InputTruncated);
    }

    [Fact]
    public void SnapshotSanitizer_NormalizesControlCharactersAndReportsTheBoundary()
    {
        var sanitizer = new UcpTelemetryInputSanitizer();

        var result = sanitizer.Sanitize("ab\r\ncdef", maxLength: 4, "query");

        Assert.Equal("ab  ", result);
        Assert.True(sanitizer.InputTruncated);
        Assert.Equal("query", sanitizer.TruncatedFields);
    }

    private static string Execute(string operation, Action<UcpOperationTelemetry> capture, UcpInputCaptureMode mode = UcpInputCaptureMode.Always)
    {
        var logger = new CaptureLogger();
        var options = Options.Create(new UcpOptions { Observability = new UcpObservabilityOptions { InputCaptureMode = mode } });
        var telemetry = new UcpOperationTelemetry(logger, options);
        telemetry.Begin(operation, "test");
        capture(telemetry);
        telemetry.Complete();
        return Assert.Single(logger.Entries).TryGetValue("InputJson", out var input) ? input as string : null;
    }

    private static void AssertInput(string inputJson, string expectedMarker)
    {
        Assert.NotNull(inputJson);
        using var _ = JsonDocument.Parse(inputJson);
        if (expectedMarker != null)
        {
            Assert.Contains(expectedMarker, inputJson, StringComparison.Ordinal);
        }
        Assert.DoesNotContain(SecretEmail, inputJson, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretAddress, inputJson, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretNotes, inputJson, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretCoupon, inputJson, StringComparison.Ordinal);
        Assert.DoesNotContain(SecretSession, inputJson, StringComparison.Ordinal);
    }

    private static McpInputCase Mcp(
        string operation,
        IDictionary<string, JsonElement> args,
        string marker)
    {
        return new McpInputCase(operation, args, marker);
    }

    private static RestInputCase Rest(
        string operation,
        IDictionary<string, object> args,
        string marker)
    {
        return new RestInputCase(operation, args, marker);
    }

    private static Dictionary<string, JsonElement> Args(params (string Name, object Value)[] values)
    {
        return values.ToDictionary(x => x.Name, x => JsonSerializer.SerializeToElement(x.Value), StringComparer.Ordinal);
    }

    private static Dictionary<string, JsonElement> CheckoutArgs(params (string Name, object Value)[] values)
    {
        var result = Args(values);
        result["buyer_email"] = JsonSerializer.SerializeToElement(SecretEmail);
        result["notes"] = JsonSerializer.SerializeToElement(SecretNotes);
        result["shipping_address"] = JsonSerializer.SerializeToElement(new
        {
            first_name = "Secret",
            last_name = "Buyer",
            line1 = SecretAddress,
            city = "Secret City",
            postal_code = "12345",
            email = SecretEmail,
            country_code = "USA",
            region_id = "CA",
        });
        return result;
    }

    private static UcpCartRequest CartRequest()
    {
        return new UcpCartRequest
        {
            StoreId = "store-marker",
            BuyerId = "buyer-marker",
            LineItems = [new UcpCartLineItemRequest { Id = "line-marker", ProductId = "product-marker", Quantity = 2 }],
            Coupons = [SecretCoupon],
        };
    }

    private static UcpCheckoutRequest CheckoutRequest()
    {
        return new UcpCheckoutRequest
        {
            CartId = "cart-marker",
            StoreId = "store-marker",
            BuyerId = "buyer-marker",
            Buyer = new UcpCheckoutBuyer { Email = SecretEmail, Name = "Secret Buyer", Phone = "555-0100" },
            ShippingAddress = new UcpCheckoutAddress
            {
                FirstName = "Secret",
                LastName = "Buyer",
                Line1 = SecretAddress,
                City = "Secret City",
                PostalCode = "12345",
                Email = SecretEmail,
                CountryCode = "USA",
                RegionId = "CA",
            },
            Notes = SecretNotes,
            PaymentHandler = "payment-marker",
        };
    }

    private sealed class CaptureLogger : ILogger<UcpOperationTelemetry>
    {
        public List<IReadOnlyDictionary<string, object>> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => Scope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            Entries.Add((state as IEnumerable<KeyValuePair<string, object>>)?.ToDictionary(x => x.Key, x => x.Value)
                ?? new Dictionary<string, object>());
        }
    }

    private sealed record McpInputCase(
        string Operation,
        IDictionary<string, JsonElement> Arguments,
        string ExpectedMarker);

    private sealed record RestInputCase(
        string Operation,
        IDictionary<string, object> Arguments,
        string ExpectedMarker);

    private sealed class Scope : IDisposable
    {
        public static Scope Instance { get; } = new();
        public void Dispose() { }
    }
}
