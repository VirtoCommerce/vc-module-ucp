using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Models;
using VirtoCommerce.UCP.Core.Services;
using VirtoCommerce.UCP.Web.Mcp;
using VirtoCommerce.UCP.Web.Mcp.Models;
using Xunit;
using UcpModule = VirtoCommerce.UCP.Web.Module;

namespace VirtoCommerce.UCP.Tests;

[Trait("Category", "Unit")]
public class UcpMcpCommerceToolsTests
{
    [Fact]
    public void UcpMcpTools_ExposeCommerceToolsOnly()
    {
        var toolNames = GetCommerceToolNames();

        Assert.Contains(ModuleConstants.McpTools.GetStoreCapabilities, toolNames);
        Assert.Contains(ModuleConstants.McpTools.SearchProducts, toolNames);
        Assert.Contains(ModuleConstants.McpTools.GetProduct, toolNames);
        Assert.Contains(ModuleConstants.McpTools.CreateCart, toolNames);
        Assert.Contains(ModuleConstants.McpTools.ListCarts, toolNames);
        Assert.Contains(ModuleConstants.McpTools.GetCart, toolNames);
        Assert.Contains(ModuleConstants.McpTools.UpdateCart, toolNames);
        Assert.Contains(ModuleConstants.McpTools.CreateCheckout, toolNames);
        Assert.Contains(ModuleConstants.McpTools.UpdateCheckout, toolNames);
        Assert.Contains(ModuleConstants.McpTools.CheckoutAndHandoff, toolNames);
        Assert.Contains(ModuleConstants.McpTools.GetPaymentHandlers, toolNames);
        Assert.Contains(ModuleConstants.McpTools.HandoffCheckout, toolNames);
        Assert.Contains(ModuleConstants.McpTools.ListCountries, toolNames);
        Assert.Contains(ModuleConstants.McpTools.ResolveCountry, toolNames);
        Assert.Contains(ModuleConstants.McpTools.ListRegions, toolNames);
        Assert.Contains(ModuleConstants.McpTools.TrackOrder, toolNames);
        Assert.Equal(16, toolNames.Length);
        Assert.All(toolNames, toolName => Assert.True(ModuleConstants.McpTools.IsUcpTool(toolName)));
        Assert.DoesNotContain("get_ucp_autodiscovery", toolNames);
    }

    [Fact]
    public void IdentityLinkingTool_IsSeparateFromCommerceSchemasAndHasNoBuyerSelector()
    {
        var method = typeof(UcpMcpIdentityTools).GetMethod(nameof(UcpMcpIdentityTools.LinkBuyerIdentity));
        var attribute = method?.GetCustomAttribute<McpServerToolAttribute>();
        var description = method?.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description;
        var parameters = method?.GetParameters().Select(parameter => parameter.Name).ToArray();

        Assert.Equal(ModuleConstants.McpTools.LinkBuyerIdentity, attribute?.Name);
        Assert.DoesNotContain("buyer_mode", parameters);
        Assert.DoesNotContain("buyer_id", parameters);
        Assert.DoesNotContain(ModuleConstants.McpTools.LinkBuyerIdentity, ModuleConstants.McpTools.UcpToolNames);
        Assert.StartsWith("REQUIRED first step", description);
        Assert.Contains("Platform OAuth", description);
        Assert.Contains("for my organization", description);
    }

    [Fact]
    public void UcpMcpTools_DoNotAcceptStorefrontUrl()
    {
        var parameterNames = typeof(UcpMcpCommerceTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() != null)
            .SelectMany(method => method.GetParameters())
            .Select(parameter => parameter.Name)
            .ToArray();

        Assert.DoesNotContain("storefront_url", parameterNames);
        Assert.DoesNotContain("storefrontUrl", parameterNames);
        Assert.DoesNotContain("base_url", parameterNames);
        Assert.DoesNotContain("baseUrl", parameterNames);
    }

    [Fact]
    public void UcpMcpTools_AcceptFrontendMcpCommerceParameters()
    {
        var parameterNames = typeof(UcpMcpCommerceTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() != null)
            .SelectMany(method => method.GetParameters())
            .Select(parameter => parameter.Name)
            .ToArray();

        Assert.Contains("price_min", parameterNames);
        Assert.Contains("price_max", parameterNames);
        Assert.Contains("cursor", parameterNames);
        Assert.Contains("sort", parameterNames);
        Assert.Contains("cart_name", parameterNames);
        Assert.Contains("cart_type", parameterNames);
        Assert.Contains("cart_id", parameterNames);
        Assert.Contains("buyer_email", parameterNames);
        Assert.Contains("buyer_name", parameterNames);
        Assert.Contains("buyer_phone", parameterNames);
    }

    [Fact]
    public void GetProduct_AcceptsFrontendMcpIdParameter()
    {
        var parameterNames = typeof(UcpMcpCommerceTools)
            .GetMethod(nameof(UcpMcpCommerceTools.GetProduct))
            ?.GetParameters()
            .Select(parameter => parameter.Name)
            .ToArray();

        Assert.NotNull(parameterNames);
        Assert.Contains("id", parameterNames);
        Assert.Contains("product_id", parameterNames);
    }

    [Fact]
    public void McpToolDescriptions_ExplainMinorUnitsAndCheckoutRequirements()
    {
        var searchParameters = typeof(UcpMcpCommerceTools)
            .GetMethod(nameof(UcpMcpCommerceTools.SearchProducts))
            ?.GetParameters()
            .ToDictionary(parameter => parameter.Name);
        var listCartsMethod = typeof(UcpMcpCommerceTools)
            .GetMethod(nameof(UcpMcpCommerceTools.ListCarts));
        var listCartsBuyerParameter = listCartsMethod
            ?.GetParameters()
            .Single(parameter => parameter.Name == "buyer_id");
        var checkoutDescription = typeof(UcpMcpCommerceTools)
            .GetMethod(nameof(UcpMcpCommerceTools.CheckoutAndHandoff))
            ?.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()
            ?.Description;

        Assert.Contains("minor currency units", searchParameters["price_max"].GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description);
        Assert.Contains("buyer-scoped carts", listCartsMethod?.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description);
        Assert.Contains("Required for anonymous continuation", listCartsBuyerParameter?.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description);
        Assert.Null(listCartsBuyerParameter?.GetCustomAttribute<System.ComponentModel.DataAnnotations.RequiredAttribute>());
        Assert.Contains("shipping_address.postal_code", checkoutDescription);
        Assert.Contains("ask the user", checkoutDescription);
    }

    [Fact]
    public void BuyerSensitiveToolDescriptions_RouteAccountIntentThroughIdentityLinking()
    {
        var methods = new[]
        {
            nameof(UcpMcpCommerceTools.SearchProducts),
            nameof(UcpMcpCommerceTools.GetProduct),
            nameof(UcpMcpCommerceTools.CreateCart),
            nameof(UcpMcpCommerceTools.ListCarts),
            nameof(UcpMcpCommerceTools.GetCart),
            nameof(UcpMcpCommerceTools.UpdateCart),
            nameof(UcpMcpCommerceTools.CreateCheckout),
            nameof(UcpMcpCommerceTools.UpdateCheckout),
            nameof(UcpMcpCommerceTools.GetPaymentHandlers),
            nameof(UcpMcpCommerceTools.CheckoutAndHandoff),
            nameof(UcpMcpCommerceTools.HandoffCheckout),
            nameof(UcpMcpCommerceTools.TrackOrder),
        };

        foreach (var methodName in methods)
        {
            var description = typeof(UcpMcpCommerceTools)
                .GetMethod(methodName)
                ?.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()
                ?.Description;

            Assert.Contains(ModuleConstants.McpTools.LinkBuyerIdentity, description);
            Assert.Contains("do not call", description, System.StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void McpToolSerializerOptions_ReadNumbersFromJsonStrings()
    {
        var options = CreateMcpToolSerializerOptions();

        var value = JsonSerializer.Deserialize<long?>("\"15000\"", options);

        Assert.Equal(JsonNumberHandling.AllowReadingFromString, options.NumberHandling);
        Assert.Equal(15000, value);
    }

    [Fact]
    public void McpToolSchemas_DoNotExposeBuyerModeAndKeepPricesNumeric()
    {
        using var services = new ServiceCollection()
            .AddSingleton<IUcpProfileService>(_ => null)
            .AddSingleton<IUcpCatalogService>(_ => null)
            .AddSingleton<IUcpCartService>(_ => null)
            .BuildServiceProvider();
        var options = new McpServerToolCreateOptions
        {
            Services = services,
            SerializerOptions = CreateMcpToolSerializerOptions(),
        };
        var searchTool = McpServerTool.Create(
            typeof(UcpMcpCommerceTools).GetMethod(nameof(UcpMcpCommerceTools.SearchProducts)),
            target: null,
            options);
        var listCartsTool = McpServerTool.Create(
            typeof(UcpMcpCommerceTools).GetMethod(nameof(UcpMcpCommerceTools.ListCarts)),
            target: null,
            options);

        var searchProperties = searchTool.ProtocolTool.InputSchema.GetProperty("properties");
        var listCartsSchema = listCartsTool.ProtocolTool.InputSchema;
        var listCartsRequired = listCartsSchema.TryGetProperty("required", out var required)
            ? required.EnumerateArray().Select(element => element.GetString()).ToArray()
            : [];

        Assert.Contains("integer", GetSchemaTypes(searchProperties.GetProperty("price_min")));
        Assert.Contains("integer", GetSchemaTypes(searchProperties.GetProperty("price_max")));
        Assert.DoesNotContain("string", GetSchemaTypes(searchProperties.GetProperty("price_min")));
        Assert.DoesNotContain("string", GetSchemaTypes(searchProperties.GetProperty("price_max")));
        Assert.DoesNotContain("buyer_mode", listCartsSchema.GetProperty("properties").EnumerateObject().Select(property => property.Name));
        Assert.DoesNotContain("buyer_id", listCartsRequired);
    }

    [Fact]
    public void McpToolSchemas_KeepFlatCommerceArguments()
    {
        using var services = new ServiceCollection()
            .AddSingleton<IUcpProfileService>(_ => null)
            .AddSingleton<IUcpCatalogService>(_ => null)
            .AddSingleton<IUcpCartService>(_ => null)
            .AddSingleton<IUcpCheckoutService>(_ => null)
            .AddSingleton<IUcpOrderService>(_ => null)
            .BuildServiceProvider();
        var options = new McpServerToolCreateOptions
        {
            Services = services,
            SerializerOptions = CreateMcpToolSerializerOptions(),
        };
        var expectedSchemas = new Dictionary<string, string[]>
        {
            [nameof(UcpMcpCommerceTools.SearchProducts)] = ["query", "store_id", "currency", "language", "price_min", "price_max", "limit"],
            [nameof(UcpMcpCommerceTools.GetProduct)] = ["id", "product_id", "store_id", "currency", "language"],
            [nameof(UcpMcpCommerceTools.CreateCart)] = ["line_items", "store_id", "currency", "language", "buyer_id", "cart_name", "cart_type", "coupons"],
            [nameof(UcpMcpCommerceTools.ListCarts)] = ["store_id", "currency", "language", "buyer_id", "cart_name", "cart_type", "cursor", "limit", "sort"],
            [nameof(UcpMcpCommerceTools.GetCart)] = ["cart_id", "store_id", "currency", "language", "buyer_id"],
            [nameof(UcpMcpCommerceTools.UpdateCart)] = ["cart_id", "line_items", "store_id", "currency", "language", "buyer_id", "cart_name", "cart_type", "coupons"],
            [nameof(UcpMcpCommerceTools.CreateCheckout)] = ["cart_id", "store_id", "currency", "language", "buyer_id", "buyer", "buyer_email", "buyer_name", "buyer_phone", "shipping_address", "billing_address", "payment_handler", "notes"],
            [nameof(UcpMcpCommerceTools.UpdateCheckout)] = ["checkout_id", "cart_id", "store_id", "currency", "language", "buyer_id", "buyer", "buyer_email", "buyer_name", "buyer_phone", "shipping_address", "billing_address", "payment_handler", "notes"],
            [nameof(UcpMcpCommerceTools.CheckoutAndHandoff)] = ["cart_id", "store_id", "currency", "language", "buyer_id", "buyer", "buyer_email", "buyer_name", "buyer_phone", "shipping_address", "billing_address", "payment_handler", "notes"],
            [nameof(UcpMcpCommerceTools.HandoffCheckout)] = ["checkout_id", "cart_id", "store_id", "currency", "language", "buyer_id", "buyer", "buyer_email", "buyer_name", "buyer_phone", "shipping_address", "billing_address", "payment_handler", "notes"],
            [nameof(UcpMcpCommerceTools.TrackOrder)] = ["order_id", "order_number", "cart_id", "store_id", "currency", "language", "buyer_id"],
        };

        foreach (var (methodName, expectedProperties) in expectedSchemas)
        {
            var method = typeof(UcpMcpCommerceTools).GetMethod(methodName);
            var tool = McpServerTool.Create(method, target: null, options);
            var actualProperties = tool.ProtocolTool.InputSchema
                .GetProperty("properties")
                .EnumerateObject()
                .Select(property => property.Name)
                .Order()
                .ToArray();

            Assert.Equal(expectedProperties.Order(), actualProperties);
        }
    }

    [Fact]
    public void McpInstructions_DescribeInstalledStorefrontMode()
    {
        Assert.Contains("where this MCP server is installed", ModuleConstants.McpInstructions);
        Assert.Contains("Do not pass storefront URLs", ModuleConstants.McpInstructions);
        Assert.Contains(ModuleConstants.McpTools.CheckoutAndHandoff, ModuleConstants.McpInstructions);
        Assert.Contains("shipping_address.postal_code", ModuleConstants.McpInstructions);
        Assert.Contains("complete desired line_items state", ModuleConstants.McpInstructions);
        Assert.Contains("never call create_cart as a fallback", ModuleConstants.McpInstructions);
        Assert.Contains("saved cart_id and buyer_id", ModuleConstants.McpInstructions);
        Assert.Contains("list_carts requires buyer_id for anonymous continuation", ModuleConstants.McpInstructions);
        Assert.Contains("buyer identity and organization come only from the Platform token", ModuleConstants.McpInstructions);
        Assert.Contains("call link_buyer_identity", ModuleConstants.McpInstructions);
        Assert.Contains("you MUST call link_buyer_identity", ModuleConstants.McpInstructions);
        Assert.Contains(ModuleConstants.ErrorCodes.IdentityOptional, ModuleConstants.McpInstructions);
        Assert.Contains("repeat the exact catalog operation", ModuleConstants.McpInstructions);
        Assert.DoesNotContain("buyer_mode", ModuleConstants.McpInstructions);
        Assert.Contains("MCP tool calls are stateless", ModuleConstants.McpInstructions);
        Assert.Contains("build a fresh argument object", ModuleConstants.McpInstructions);
        Assert.Contains("every new line item requires product_id and quantity greater than zero", ModuleConstants.McpInstructions);
        Assert.Contains("explicitly repeat cart_id, store_id, and buyer_id", ModuleConstants.McpInstructions);
        Assert.Contains("Never send a partial shipping_address", ModuleConstants.McpInstructions);
        Assert.Contains("at least one of order_id, order_number, or the saved cart_id", ModuleConstants.McpInstructions);
        Assert.DoesNotContain("McpDefaultStorefrontUrl", ModuleConstants.McpInstructions);
        Assert.DoesNotContain("get_ucp_autodiscovery", ModuleConstants.McpInstructions);
    }

    [Fact]
    public async Task ListCarts_MissingBuyerId_IsResolvedByUnifiedBuyerContext()
    {
        var cartService = new CaptureCartService();

        await UcpMcpCommerceTools.ListCarts(
            new StubProfileService(new UcpProfile()),
            cartService,
            buyer_id: null,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(cartService.LastListRequest);
        Assert.Null(cartService.LastListRequest.Context.BuyerId);
    }

    [Fact]
    public async Task GetProduct_InvalidRequest_ThrowsUcpExceptionForTransportFilter()
    {
        var exception = await Assert.ThrowsAsync<UcpException>(() => UcpMcpCommerceTools.GetProduct(
            null,
            null,
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(ModuleConstants.ErrorCodes.InvalidRequest, exception.Code);
        Assert.Equal(400, exception.StatusCode);
        Assert.Equal("id is required.", exception.Message);
    }

    [Fact]
    public async Task SearchProducts_UsesDefaultsFromExplicitlySelectedStore()
    {
        var profileService = new StubProfileService(new UcpProfile
        {
            Stores =
            {
                new UcpStoreProfile { Id = "store-acme", DefaultCurrency = "EUR", DefaultLanguage = "de-DE" },
                new UcpStoreProfile { Id = "B2B-store", DefaultCurrency = "USD", DefaultLanguage = "en-US" },
            },
        });
        var catalogService = new CaptureCatalogService();

        await UcpMcpCommerceTools.SearchProducts(
            profileService,
            catalogService,
            "printer",
            store_id: "B2B-store",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("B2B-store", catalogService.LastRequest.StoreId);
        Assert.Equal("USD", catalogService.LastRequest.Currency);
        Assert.Equal("en-US", catalogService.LastRequest.Language);
        Assert.Equal("USD", catalogService.LastRequest.Context.Currency);
        Assert.Equal("en-US", catalogService.LastRequest.Context.Language);
    }

    [Fact]
    public async Task SearchProducts_UsesConfiguredDefaultStoreWhenStoreIsOmitted()
    {
        var profileService = new StubProfileService(new UcpProfile
        {
            DefaultStoreId = "B2B-store",
            Stores =
            {
                new UcpStoreProfile { Id = "store-acme", DefaultCurrency = "EUR", DefaultLanguage = "de-DE" },
                new UcpStoreProfile { Id = "B2B-store", DefaultCurrency = "USD", DefaultLanguage = "en-US" },
            },
        });
        var catalogService = new CaptureCatalogService();

        await UcpMcpCommerceTools.SearchProducts(
            profileService,
            catalogService,
            "printer",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("B2B-store", catalogService.LastRequest.StoreId);
        Assert.Equal("USD", catalogService.LastRequest.Currency);
        Assert.Equal("en-US", catalogService.LastRequest.Language);
    }

    [Fact]
    public async Task CheckoutAndHandoff_PreservesResolvedIdsAndNextStepArguments()
    {
        var profileService = new StubProfileService(new UcpProfile());
        var checkoutService = new CaptureCheckoutService();

        var result = Assert.IsType<UcpMcpCheckoutAndHandoffResult>(await UcpMcpCommerceTools.CheckoutAndHandoff(
            profileService,
            checkoutService,
            cart_id: "cart-request",
            store_id: "B2B-store",
            language: "en-US",
            buyer_id: "buyer-request",
            cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("checkout-1", checkoutService.HandoffCheckoutId);
        Assert.Equal("cart-request", checkoutService.HandoffRequest.CartId);
        Assert.Equal("cart-request", result.CartId);
        Assert.Equal("buyer-service", result.BuyerId);
        Assert.Equal("https://example.test/checkout", result.ContinueUrl);
        Assert.Equal("cart-request", result.NextStepAfterPayment.Arguments["cart_id"]);
        Assert.Equal("buyer-service", result.NextStepAfterPayment.Arguments["buyer_id"]);
        Assert.Equal("en-US", result.NextStepAfterPayment.Arguments["language"]);
    }

    private static string[] GetCommerceToolNames()
    {
        return typeof(UcpMcpCommerceTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>())
            .Where(attribute => attribute != null)
            .Select(attribute => attribute.Name)
            .ToArray();
    }

    private static string[] GetSchemaTypes(JsonElement schema)
    {
        var type = schema.GetProperty("type");

        return type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Select(element => element.GetString()).ToArray()
            : [type.GetString()];
    }

    private static JsonSerializerOptions CreateMcpToolSerializerOptions()
    {
        var factory = typeof(UcpModule).GetMethod(
            "CreateMcpToolSerializerOptions",
            BindingFlags.NonPublic | BindingFlags.Static);

        return Assert.IsType<JsonSerializerOptions>(factory?.Invoke(null, null));
    }

    private sealed class StubProfileService : IUcpProfileService
    {
        private readonly UcpProfile _profile;

        public StubProfileService(UcpProfile profile)
        {
            _profile = profile;
        }

        public Task<UcpProfile> GetProfile(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_profile);
        }
    }

    private sealed class CaptureCatalogService : IUcpCatalogService
    {
        public UcpCatalogSearchRequest LastRequest { get; private set; }

        public Task<UcpCatalogSearchResponse> SearchProducts(UcpCatalogSearchRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(new UcpCatalogSearchResponse());
        }

        public Task<UcpProductResponse> GetProduct(string productId, UcpCatalogSearchRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(new UcpProductResponse());
        }
    }

    private sealed class CaptureCartService : IUcpCartService
    {
        public UcpCartListRequest LastListRequest { get; private set; }

        public Task<UcpCartResponse> CreateCart(UcpCartRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<UcpCartResponse>(null);
        }

        public Task<UcpCartListResponse> ListCarts(UcpCartListRequest request, CancellationToken cancellationToken = default)
        {
            LastListRequest = request;
            return Task.FromResult(new UcpCartListResponse());
        }

        public Task<UcpCartResponse> GetCart(string cartId, UcpCartRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<UcpCartResponse>(null);
        }

        public Task<UcpCartResponse> UpdateCart(string cartId, UcpCartRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<UcpCartResponse>(null);
        }

        public Task<UcpCartResponse> ApplyCheckoutData(string cartId, UcpCheckoutRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<UcpCartResponse>(null);
        }
    }

    private sealed class CaptureCheckoutService : IUcpCheckoutService
    {
        public string HandoffCheckoutId { get; private set; }
        public UcpCheckoutRequest HandoffRequest { get; private set; }

        public Task<UcpCheckoutResponse> CreateCheckout(UcpCheckoutRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new UcpCheckoutResponse
            {
                Checkout = new UcpCheckout
                {
                    Id = "checkout-1",
                    CartId = "cart-service",
                    Buyer = new UcpCheckoutBuyer { Id = "buyer-service" },
                },
            });
        }

        public Task<UcpCheckoutResponse> UpdateCheckout(string checkoutId, UcpCheckoutRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<UcpCheckoutResponse>(null);
        }

        public Task<UcpPaymentHandlersResponse> GetPaymentHandlers(string checkoutId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<UcpPaymentHandlersResponse>(null);
        }

        public Task<UcpCheckoutHandoffResponse> HandoffCheckout(string checkoutId, UcpCheckoutRequest request, CancellationToken cancellationToken = default)
        {
            HandoffCheckoutId = checkoutId;
            HandoffRequest = request;

            return Task.FromResult(new UcpCheckoutHandoffResponse
            {
                Checkout = new UcpCheckout { ContinueUrl = "https://example.test/checkout" },
            });
        }

        public Task<UcpHandoffRestoreResponse> RestoreHandoff(UcpHandoffRestoreRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<UcpHandoffRestoreResponse>(null);
        }
    }
}
