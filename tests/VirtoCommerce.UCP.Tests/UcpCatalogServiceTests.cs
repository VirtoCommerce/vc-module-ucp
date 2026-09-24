using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Diagnostics;
using VirtoCommerce.UCP.Core.Models;
using VirtoCommerce.UCP.Core.Options;
using VirtoCommerce.UCP.Core.Services;
using VirtoCommerce.UCP.Data.Services;
using Xunit;

namespace VirtoCommerce.UCP.Tests;

[Trait("Category", "Unit")]
public class UcpCatalogServiceTests
{
    [Fact]
    public void UcpServiceBase_PreservesPublishedProtectedVirtualOverloads()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(UcpServiceBase);

        Assert.True(type.GetMethod("CreateException", flags, null, [typeof(string), typeof(string), typeof(int)], null)?.IsVirtual);
        Assert.True(type.GetMethod("CreateException", flags, null, [typeof(string), typeof(string), typeof(int), typeof(Exception)], null)?.IsVirtual);
        Assert.True(type.GetMethod("ParseGraphQlResult", flags, null, [typeof(XApiExecutionResult), typeof(string)], null)?.IsVirtual);
        Assert.True(type.GetMethod("ParseGraphQlResult", flags, null, [typeof(XApiExecutionResult), typeof(string), typeof(Func<JsonElement, bool>)], null)?.IsVirtual);
    }

    [Fact]
    public async Task SearchProducts_MapsXCatalogProductsAndBuyerContext()
    {
        var executor = new StubXApiExecutor(SearchResponseJson);
        var httpContextAccessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext(),
        };
        httpContextAccessor.HttpContext.TraceIdentifier = "trace-1";
        httpContextAccessor.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "buyer-1"),
            new Claim(ClaimTypes.NameIdentifier, "buyer-1"),
            new Claim("organization_id", "org-1"),
        ], "Bearer"));

        var service = new UcpCatalogService(
            executor,
            httpContextAccessor,
            Options.Create(new UcpOptions
            {
                DefaultStoreId = "acme",
                DefaultCurrency = "USD",
                DefaultCultureName = "en-US",
            }));

        var response = await service.SearchProducts(new UcpCatalogSearchRequest
        {
            Query = "waterproof running jacket",
            Context = new UcpCatalogContext
            {
                StoreId = "acme",
                Currency = "USD",
                Language = "en-US",
            },
            Filters = new UcpSearchFilters
            {
                Price = new UcpPriceFilter { Max = 15000 },
            },
            Pagination = new UcpPaginationRequest { Limit = 3 },
        }, TestContext.Current.CancellationToken);

        Assert.Equal("trace-1", response.Ucp.CorrelationId);
        Assert.Contains("dev.ucp.shopping.catalog.search", response.Ucp.Capabilities.Keys);
        Assert.Single(response.Products);
        Assert.Equal("product-1", response.Products[0].Id);
        Assert.Equal("Waterproof Jacket", response.Products[0].Name);
        Assert.Equal(12000, response.Products[0].Price.Amount);
        Assert.Equal("USD", response.Products[0].Price.Currency);
        Assert.True(response.Products[0].Availability.IsBuyable);
        Assert.Contains(response.Products[0].Attributes, x => x.Name == "Size" && x.Value == "M");
        Assert.Equal("acme", executor.LastRequest.Variables["storeId"]);
        Assert.Equal("buyer-1", executor.LastRequest.Variables["userId"]);
        Assert.Equal("USD", executor.LastRequest.Variables["currencyCode"]);
        Assert.Equal("price:(TO 150]", executor.LastRequest.Variables["filter"]);
        Assert.Contains(executor.LastRequest.User.Claims, x => x.Type == ClaimTypes.NameIdentifier && x.Value == "buyer-1");
        Assert.Contains(executor.LastRequest.User.Claims, x => x.Type == "organization_id" && x.Value == "org-1");
        Assert.Empty(response.Messages);
    }

    [Fact]
    public async Task SearchProducts_AnonymousResponseInvitesStandardIdentityLinking()
    {
        var service = new UcpCatalogService(
            new StubXApiExecutor(SearchResponseJson),
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            Options.Create(new UcpOptions { DefaultStoreId = "acme", DefaultCurrency = "USD" }));

        var response = await service.SearchProducts(
            new UcpCatalogSearchRequest { Query = "printer" },
            TestContext.Current.CancellationToken);

        var message = Assert.Single(response.Messages);
        Assert.Equal("info", message.Type);
        Assert.Equal("info", message.Severity);
        Assert.Equal(ModuleConstants.ErrorCodes.IdentityOptional, message.Code);
        Assert.Contains(ModuleConstants.McpTools.LinkBuyerIdentity, message.Content);
        Assert.Contains("repeat this operation", message.Content);
    }

    [Fact]
    public async Task SearchProducts_RequiresStoreId()
    {
        var service = new UcpCatalogService(
            new StubXApiExecutor(SearchResponseJson),
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            Options.Create(new UcpOptions()));

        var exception = await Assert.ThrowsAsync<UcpException>(() => service.SearchProducts(new UcpCatalogSearchRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(ModuleConstants.ErrorCodes.MissingStoreId, exception.Code);
        Assert.Equal(400, exception.StatusCode);
    }

    [Fact]
    public async Task SearchProducts_AcceptsTopLevelStoreIdAndLimit()
    {
        var executor = new StubXApiExecutor(SearchResponseJson);
        var service = new UcpCatalogService(
            executor,
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            Options.Create(new UcpOptions { DefaultCurrency = "USD", DefaultCultureName = "en-US" }));

        var response = await service.SearchProducts(new UcpCatalogSearchRequest
        {
            StoreId = "store-acme",
            Query = "iPhone 17 Pro",
            Limit = 10,
        }, TestContext.Current.CancellationToken);

        Assert.Equal(2, response.Products.Count);
        Assert.Equal("store-acme", executor.LastRequest.Variables["storeId"]);
        Assert.Equal(10, executor.LastRequest.Variables["first"]);
    }

    [Fact]
    public async Task SearchProducts_AppliesMinimumPriceFilter()
    {
        var executor = new StubXApiExecutor(SearchResponseJson);
        var service = new UcpCatalogService(
            executor,
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            Options.Create(new UcpOptions { DefaultStoreId = "acme", DefaultCurrency = "USD" }));

        var response = await service.SearchProducts(new UcpCatalogSearchRequest
        {
            Query = "jacket",
            Filters = new UcpSearchFilters
            {
                Price = new UcpPriceFilter { Min = 15000 },
            },
        }, TestContext.Current.CancellationToken);

        Assert.Single(response.Products);
        Assert.Equal("product-2", response.Products[0].Id);
        Assert.Equal(22000, response.Products[0].Price.Amount);
        Assert.Equal("price:[150 TO)", executor.LastRequest.Variables["filter"]);
    }


    [Fact]
    public async Task SearchProducts_PassesInclusivePriceBandToXCatalogInMajorUnits()
    {
        var executor = new StubXApiExecutor(SearchResponseJson);
        var service = new UcpCatalogService(
            executor,
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            Options.Create(new UcpOptions { DefaultStoreId = "acme", DefaultCurrency = "USD" }));

        await service.SearchProducts(new UcpCatalogSearchRequest
        {
            Filters = new UcpSearchFilters
            {
                Price = new UcpPriceFilter { Min = 20000, Max = 60000 },
            },
        }, TestContext.Current.CancellationToken);

        Assert.Equal("price:[200 TO 600]", executor.LastRequest.Variables["filter"]);
    }

    [Fact]
    public async Task GetProduct_ReturnsNotFoundForNullProduct()
    {
        var service = new UcpCatalogService(
            new StubXApiExecutor("""{"data":{"product":null}}"""),
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            Options.Create(new UcpOptions { DefaultStoreId = "acme" }));

        var exception = await Assert.ThrowsAsync<UcpException>(() => service.GetProduct("missing", new UcpCatalogSearchRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(ModuleConstants.ErrorCodes.ProductNotFound, exception.Code);
        Assert.Equal(404, exception.StatusCode);
    }

    [Fact]
    public async Task GetProduct_ToleratesPropertyValueResolverErrorWithPartialData()
    {
        var executor = new SequenceXApiExecutor(new (string Json, bool Succeeded)[]
        {
            (PartialProductResponseJson, false),
            ("""{"data":{"products":{"items":[{"id":"product-1","variations":[]}]}}}""", true),
        });
        var degradedTelemetry = new CapturingOperationTelemetry();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<IUcpOperationTelemetry>(degradedTelemetry)
                .BuildServiceProvider(),
        };
        var service = new UcpCatalogService(
            executor,
            new HttpContextAccessor { HttpContext = httpContext },
            Options.Create(new UcpOptions { DefaultStoreId = "acme" }));

        var response = await service.GetProduct("product-1", new UcpCatalogSearchRequest(), TestContext.Current.CancellationToken);

        Assert.Equal("product-1", response.Product.Id);
        Assert.Contains(response.Product.Attributes, attribute => attribute.Name == "ReleaseYear" && attribute.Value == null);
        Assert.Equal(nameof(XApiResponseException), degradedTelemetry.ErrorType);
        Assert.Equal("xapi_recoverable_graphql_error", degradedTelemetry.ErrorCode);
    }

    [Fact]
    public async Task GetProduct_AnonymousResponseInvitesStandardIdentityLinking()
    {
        var executor = new SequenceXApiExecutor(new (string Json, bool Succeeded)[]
        {
            (PartialProductResponseJson, true),
            ("""{"data":{"products":{"items":[{"id":"product-1","variations":[]}]}}}""", true),
        });
        var service = new UcpCatalogService(
            executor,
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            Options.Create(new UcpOptions { DefaultStoreId = "acme" }));

        var response = await service.GetProduct(
            "product-1",
            new UcpCatalogSearchRequest(),
            TestContext.Current.CancellationToken);

        var message = Assert.Single(response.Messages);
        Assert.Equal(ModuleConstants.ErrorCodes.IdentityOptional, message.Code);
        Assert.Contains(ModuleConstants.McpTools.LinkBuyerIdentity, message.Content);
    }

    [Fact]
    public async Task GetProduct_PropagatesUnrelatedGraphQlError()
    {
        var executor = new StubXApiExecutor(
            PartialProductResponseJson
                .Replace("'value'", "'product'")
                .Replace("\"value\"]", "\"product\"]"),
            succeeded: false);
        var service = new UcpCatalogService(
            executor,
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            Options.Create(new UcpOptions { DefaultStoreId = "acme" }));

        var exception = await Assert.ThrowsAsync<XApiResponseException>(() => service.GetProduct("product-1", new UcpCatalogSearchRequest(), TestContext.Current.CancellationToken));

        Assert.Equal("XCatalog", exception.Schema);
        Assert.Equal(1, exception.ErrorCount);
        Assert.Equal(executor.LastResultJson, exception.Result.Json);
    }

    [Fact]
    public async Task SearchProducts_FailedResponseWithEmptyErrors_UsesFallbackMessage()
    {
        var service = new UcpCatalogService(
            new StubXApiExecutor("""{"errors":[]}""", succeeded: false),
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            Options.Create(new UcpOptions { DefaultStoreId = "acme" }));

        var exception = await Assert.ThrowsAsync<UcpException>(() =>
            service.SearchProducts(new UcpCatalogSearchRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(ModuleConstants.ErrorCodes.XApiInvalidResponse, exception.Code);
        Assert.Equal(500, exception.StatusCode);
        Assert.Equal("XCatalog failed without a GraphQL error response.", exception.Message);
    }

    [Theory]
    [InlineData("[]", "non-object GraphQL response")]
    [InlineData("null", "non-object GraphQL response")]
    [InlineData("{}", "without object data")]
    [InlineData("{\"data\":null}", "without object data")]
    [InlineData("{\"errors\":null,\"data\":{}}", "invalid GraphQL errors field")]
    public async Task SearchProducts_MalformedGraphQlShape_UsesInvalidResponse(string json, string expectedMessage)
    {
        var service = new UcpCatalogService(
            new StubXApiExecutor(json),
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            Options.Create(new UcpOptions { DefaultStoreId = "acme" }));

        var exception = await Assert.ThrowsAsync<UcpException>(() =>
            service.SearchProducts(new UcpCatalogSearchRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(ModuleConstants.ErrorCodes.XApiInvalidResponse, exception.Code);
        Assert.Equal(500, exception.StatusCode);
        Assert.Contains(expectedMessage, exception.Message);
    }

    [Fact]
    public async Task SearchProducts_InvalidJson_PreservesParserExceptionForTelemetry()
    {
        var service = new UcpCatalogService(
            new StubXApiExecutor("{"),
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            Options.Create(new UcpOptions { DefaultStoreId = "acme" }));

        var exception = await Assert.ThrowsAsync<UcpException>(() =>
            service.SearchProducts(new UcpCatalogSearchRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(ModuleConstants.ErrorCodes.XApiInvalidResponse, exception.Code);
        Assert.Equal(StatusCodes.Status500InternalServerError, exception.StatusCode);
        Assert.IsType<JsonException>(exception.InnerException, exactMatch: false);
    }

    [Fact]
    public async Task GetProduct_ReturnsNotFoundWhenProductIsOutsideStoreCatalog()
    {
        var executor = new SequenceXApiExecutor(
            """{"data":{"product":{"id":"physical-only","code":"PHYSICAL-ONLY","name":"Physical only product","properties":[],"variations":[]}}}""",
            """{"data":{"products":{"items":[]}}}""");
        var service = new UcpCatalogService(
            executor,
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            Options.Create(new UcpOptions { DefaultStoreId = "acme" }));

        var exception = await Assert.ThrowsAsync<UcpException>(() => service.GetProduct("physical-only", new UcpCatalogSearchRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(ModuleConstants.ErrorCodes.ProductNotFound, exception.Code);
        Assert.Equal(404, exception.StatusCode);
    }

    private sealed class StubXApiExecutor : IXApiInProcessExecutor
    {
        private readonly string _json;

        public string LastResultJson => _json;

        public StubXApiExecutor(string json, bool succeeded = true)
        {
            _json = json;
            _succeeded = succeeded;
        }

        private readonly bool _succeeded;

        public XApiExecutionRequest LastRequest { get; private set; }

        public Task<XApiExecutionResult> Execute(XApiExecutionRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;

            return Task.FromResult(new XApiExecutionResult
            {
                Succeeded = _succeeded,
                Json = _json,
            });
        }

        public Task<XApiExecutionResult> ExecuteCart(XApiExecutionRequest request, CancellationToken cancellationToken = default)
        {
            return Execute(request, cancellationToken);
        }

        public Task<XApiExecutionResult> ExecuteOrder(XApiExecutionRequest request, CancellationToken cancellationToken = default)
        {
            return Execute(request, cancellationToken);
        }
    }

    private sealed class SequenceXApiExecutor : IXApiInProcessExecutor
    {
        private readonly Queue<(string Json, bool Succeeded)> _responses;

        public SequenceXApiExecutor(params string[] responses)
        {
            _responses = new Queue<(string Json, bool Succeeded)>(responses.Select(response => (response, true)));
        }

        public SequenceXApiExecutor(IEnumerable<(string Json, bool Succeeded)> responses)
        {
            _responses = new Queue<(string Json, bool Succeeded)>(responses);
        }

        public Task<XApiExecutionResult> Execute(XApiExecutionRequest request, CancellationToken cancellationToken = default)
        {
            var response = _responses.Dequeue();
            return Task.FromResult(new XApiExecutionResult
            {
                Succeeded = response.Succeeded,
                Json = response.Json,
            });
        }

        public Task<XApiExecutionResult> ExecuteCart(XApiExecutionRequest request, CancellationToken cancellationToken = default)
        {
            return Execute(request, cancellationToken);
        }

        public Task<XApiExecutionResult> ExecuteOrder(XApiExecutionRequest request, CancellationToken cancellationToken = default)
        {
            return Execute(request, cancellationToken);
        }
    }

    private sealed class CapturingOperationTelemetry : IUcpOperationTelemetry
    {
        public string ErrorType { get; private set; }
        public string ErrorCode { get; private set; }

        public void MarkDegraded(string errorType, string errorCode = null)
        {
            ErrorType = errorType;
            ErrorCode = errorCode;
        }
    }

    private const string SearchResponseJson = """
        {
          "data": {
            "products": {
              "totalCount": 2,
              "items": [
                {
                  "id": "product-1",
                  "code": "JACKET-M",
                  "name": "Waterproof Jacket",
                  "slug": "waterproof-jacket",
                  "imgSrc": "https://cdn.example/jacket.png",
                  "brandName": "Acme",
                  "productType": "Physical",
                  "price": {
                    "actual": {
                      "amount": 120.0,
                      "formattedAmount": "$120.00",
                      "currency": { "code": "USD" }
                    },
                    "list": {
                      "amount": 140.0,
                      "formattedAmount": "$140.00",
                      "currency": { "code": "USD" }
                    }
                  },
                  "availabilityData": {
                    "availableQuantity": 7,
                    "isBuyable": true,
                    "isAvailable": true,
                    "isInStock": true
                  },
                  "properties": [
                    { "name": "Size", "value": "M" }
                  ],
                  "variations": []
                },
                {
                  "id": "product-2",
                  "code": "JACKET-PREMIUM",
                  "name": "Premium Waterproof Jacket",
                  "price": {
                    "actual": {
                      "amount": 220.0,
                      "formattedAmount": "$220.00",
                      "currency": { "code": "USD" }
                    },
                    "list": {
                      "amount": 240.0,
                      "formattedAmount": "$240.00",
                      "currency": { "code": "USD" }
                    }
                  },
                  "availabilityData": {
                    "availableQuantity": 2,
                    "isBuyable": true,
                    "isAvailable": true,
                    "isInStock": true
                  },
                  "properties": [],
                  "variations": []
                }
              ]
            }
          }
        }
        """;

    private const string PartialProductResponseJson = """
        {
          "data": {
            "product": {
              "id": "product-1",
              "code": "PRODUCT-1",
              "name": "Product 1",
              "properties": [
                { "name": "ReleaseYear", "value": null }
              ],
              "variations": []
            },
            "products": {
              "items": [
                { "id": "product-1", "variations": [] }
              ]
            }
          },
          "errors": [
            {
              "message": "Error trying to resolve field 'value'.",
              "path": ["product", "properties", 0, "value"]
            }
          ]
        }
        """;
}
