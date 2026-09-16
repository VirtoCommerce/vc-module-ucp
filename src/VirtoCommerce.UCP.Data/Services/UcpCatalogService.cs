using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Models;
using VirtoCommerce.UCP.Core.Options;
using VirtoCommerce.UCP.Core.Services;
using VirtoCommerce.UCP.Data.Models;

namespace VirtoCommerce.UCP.Data.Services;

public class UcpCatalogService : UcpServiceBase, IUcpCatalogService
{
    private const int DefaultLimit = 10;
    private const int MaxLimit = 50;
    private const decimal MinorUnitsPerMajorUnit = 100m;

    private readonly IXApiInProcessExecutor _xApiExecutor;
    private readonly UcpOptions _options;

    public UcpCatalogService(
        IXApiInProcessExecutor xApiExecutor,
        IHttpContextAccessor httpContextAccessor,
        IOptions<UcpOptions> options,
        IUcpBuyerContextAccessor buyerContextAccessor = null)
        : base(httpContextAccessor, buyerContextAccessor)
    {
        _xApiExecutor = xApiExecutor;
        _options = options.Value;
    }

    public virtual async Task<UcpCatalogSearchResponse> SearchProducts(UcpCatalogSearchRequest request, CancellationToken cancellationToken = default)
    {
        request ??= new UcpCatalogSearchRequest();
        var catalogRequest = BuildCatalogExecutionRequest(request);
        var buyerContext = ResolveBuyerContext();

        var variables = new Dictionary<string, object>
        {
            ["storeId"] = catalogRequest.StoreId,
            ["userId"] = buyerContext.UserId,
            ["currencyCode"] = catalogRequest.Currency,
            ["cultureName"] = catalogRequest.CultureName,
            ["query"] = request.Query,
            ["filter"] = BuildXCatalogFilter(request),
            ["first"] = catalogRequest.Limit,
        };

        var result = await _xApiExecutor.Execute(new XApiExecutionRequest
        {
            Query = SearchProductsQuery,
            OperationName = "UcpSearchProducts",
            Variables = variables,
            User = buyerContext.Principal,
        }, cancellationToken);

        using var document = ParseGraphQlResult(result, "XCatalog", IsRecoverablePropertyValueError);
        var products = document.RootElement
            .GetProperty("data")
            .GetProperty("products");

        var mappedProducts = products
            .GetProperty("items")
            .EnumerateArray()
            .Select(ReadProduct)
            .Where(product => catalogRequest.MinPrice == null || product.Price == null || product.Price.Amount >= catalogRequest.MinPrice.Value)
            .Where(product => catalogRequest.MaxPrice == null || product.Price == null || product.Price.Amount <= catalogRequest.MaxPrice.Value)
            .ToList();

        var response = new UcpCatalogSearchResponse
        {
            Ucp = CreateMetadata("success", "dev.ucp.shopping.catalog.search"),
            Products = mappedProducts,
            Pagination = new UcpPaginationResponse
            {
                TotalCount = ReadInt(products, "totalCount", mappedProducts.Count),
                HasNextPage = ReadInt(products, "totalCount", mappedProducts.Count) > mappedProducts.Count,
            },
        };
        AddIdentityOptionalMessage(response.Messages, buyerContext);

        return response;
    }

    public virtual async Task<UcpProductResponse> GetProduct(string productId, UcpCatalogSearchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(productId);

        request ??= new UcpCatalogSearchRequest();
        var catalogRequest = BuildCatalogExecutionRequest(request);
        var buyerContext = ResolveBuyerContext();

        var variables = new Dictionary<string, object>
        {
            ["id"] = productId,
            ["storeId"] = catalogRequest.StoreId,
            ["userId"] = buyerContext.UserId,
            ["currencyCode"] = catalogRequest.Currency,
            ["cultureName"] = catalogRequest.CultureName,
        };

        var result = await _xApiExecutor.Execute(new XApiExecutionRequest
        {
            Query = GetProductQuery,
            OperationName = "UcpGetProduct",
            Variables = variables,
            User = buyerContext.Principal,
        }, cancellationToken);

        using var document = ParseGraphQlResult(result, "XCatalog", IsRecoverablePropertyValueError);
        var productElement = document.RootElement
            .GetProperty("data")
            .GetProperty("product");

        if (productElement.ValueKind == JsonValueKind.Null)
        {
            throw CreateException(ModuleConstants.ErrorCodes.ProductNotFound, $"Product '{productId}' was not found.", StatusCodes.Status404NotFound);
        }

        await EnsureProductBelongsToStore(productId, productElement, catalogRequest, buyerContext, cancellationToken);

        var response = new UcpProductResponse
        {
            Ucp = CreateMetadata("success", "dev.ucp.shopping.catalog.lookup"),
            Product = ReadProduct(productElement),
        };
        AddIdentityOptionalMessage(response.Messages, buyerContext);

        return response;
    }

    private static void AddIdentityOptionalMessage(ICollection<UcpMessage> messages, UcpBuyerContext buyerContext)
    {
        if (!buyerContext.IsAuthenticated)
        {
            messages.Add(new UcpMessage
            {
                Type = "info",
                Code = ModuleConstants.ErrorCodes.IdentityOptional,
                Content = "This response is anonymous. If the user asked to use their account, organization, personalized pricing, saved data, or orders, call link_buyer_identity and repeat this operation before continuing.",
                Severity = "info",
            });
        }
    }

    private async Task EnsureProductBelongsToStore(
        string productId,
        JsonElement product,
        CatalogExecutionRequest catalogRequest,
        UcpBuyerContext buyerContext,
        CancellationToken cancellationToken)
    {
        var lookupText = FirstNotEmpty(ReadString(product, "code"), ReadString(product, "name"));
        var result = await _xApiExecutor.Execute(new XApiExecutionRequest
        {
            Query = ProductMembershipQuery,
            OperationName = "UcpCheckProductMembership",
            Variables = new Dictionary<string, object>
            {
                ["storeId"] = catalogRequest.StoreId,
                ["userId"] = buyerContext.UserId,
                ["currencyCode"] = catalogRequest.Currency,
                ["cultureName"] = catalogRequest.CultureName,
                ["query"] = lookupText,
                ["first"] = MaxLimit,
            },
            User = buyerContext.Principal,
        }, cancellationToken);

        using var document = ParseGraphQlResult(result, "XCatalog");
        var items = document.RootElement
            .GetProperty("data")
            .GetProperty("products")
            .GetProperty("items");
        var belongsToStore = items.EnumerateArray().Any(item =>
            string.Equals(ReadString(item, "id"), productId, StringComparison.OrdinalIgnoreCase) ||
            item.TryGetProperty("variations", out var variations) &&
            variations.ValueKind == JsonValueKind.Array &&
            variations.EnumerateArray().Any(variation => string.Equals(ReadString(variation, "id"), productId, StringComparison.OrdinalIgnoreCase)));

        if (!belongsToStore)
        {
            throw CreateException(ModuleConstants.ErrorCodes.ProductNotFound, $"Product '{productId}' was not found.", StatusCodes.Status404NotFound);
        }
    }

    private CatalogExecutionRequest BuildCatalogExecutionRequest(UcpCatalogSearchRequest request)
    {
        var result = new CatalogExecutionRequest
        {
            StoreId = ResolveStoreId(request),
            Currency = ResolveCurrency(request),
            CultureName = ResolveCultureName(request),
            Limit = ResolveLimit(request),
            MinPrice = ResolveMinPrice(request),
            MaxPrice = ResolveMaxPrice(request),
        };

        ValidateCatalogExecutionRequest(result);

        return result;
    }

    private string ResolveStoreId(UcpCatalogSearchRequest request)
    {
        return FirstNotEmpty(request.StoreId, request.Context?.StoreId, _options.DefaultStoreId);
    }

    private string ResolveCurrency(UcpCatalogSearchRequest request)
    {
        return FirstNotEmpty(request.Currency, request.Context?.Currency, _options.DefaultCurrency);
    }

    private string ResolveCultureName(UcpCatalogSearchRequest request)
    {
        return FirstNotEmpty(request.Language, request.Context?.Language, _options.DefaultCultureName);
    }

    private static int ResolveLimit(UcpCatalogSearchRequest request)
    {
        return Math.Clamp(request.Limit ?? request.Pagination?.Limit ?? DefaultLimit, 1, MaxLimit);
    }

    private static long? ResolveMinPrice(UcpCatalogSearchRequest request)
    {
        return request.Filters?.Price?.Min;
    }

    private static long? ResolveMaxPrice(UcpCatalogSearchRequest request)
    {
        return request.Filters?.Price?.Max;
    }

    private void ValidateCatalogExecutionRequest(CatalogExecutionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.StoreId))
        {
            throw CreateException(ModuleConstants.ErrorCodes.MissingStoreId, "store_id or context.store_id is required when UCP:DefaultStoreId is not configured.");
        }
    }

    protected virtual string BuildXCatalogFilter(UcpCatalogSearchRequest request)
    {
        var filters = new List<string>();
        AddCategoryFilters(filters, request.Filters?.Categories);
        AddPriceFilter(filters, request.Filters?.Price);

        return filters.Count == 0 ? null : string.Join(" ", filters);
    }

    private static void AddCategoryFilters(List<string> filters, IList<string> categories)
    {
        if (categories?.Count > 0)
        {
            foreach (var category in categories)
            {
                filters.Add($"category.subtree:{category}");
            }
        }
    }

    private static void AddPriceFilter(List<string> filters, UcpPriceFilter price)
    {
        if (price?.Min is null && price?.Max is null)
        {
            return;
        }

        var lower = price.Min.HasValue ? ToMajorUnits(price.Min.Value) : null;
        var upper = price.Max.HasValue ? ToMajorUnits(price.Max.Value) : null;
        var leftBracket = price.Min.HasValue ? "[" : "(";
        var rightBracket = price.Max.HasValue ? "]" : ")";

        filters.Add($"price:{leftBracket}{BuildPriceRange(lower, upper)}{rightBracket}");
    }

    private static string BuildPriceRange(string lower, string upper)
    {
        if (lower == null)
        {
            return $"TO {upper}";
        }

        return upper == null ? $"{lower} TO" : $"{lower} TO {upper}";
    }

    private static string ToMajorUnits(long amount)
    {
        return (amount / MinorUnitsPerMajorUnit).ToString(CultureInfo.InvariantCulture);
    }

    protected virtual UcpProduct ReadProduct(JsonElement element)
    {
        return new UcpProduct
        {
            Id = ReadString(element, "id"),
            Code = ReadString(element, "code"),
            Name = ReadString(element, "name"),
            Slug = ReadString(element, "slug"),
            ImageUrl = ReadString(element, "imgSrc"),
            Brand = ReadString(element, "brandName"),
            ProductType = ReadString(element, "productType"),
            Price = ReadPrice(element, "price", "actual"),
            ListPrice = ReadPrice(element, "price", "list"),
            Availability = ReadAvailability(element),
            Attributes = ReadAttributes(element),
            Variations = ReadVariations(element),
        };
    }

    protected virtual IList<UcpProductVariation> ReadVariations(JsonElement element)
    {
        if (!element.TryGetProperty("variations", out var variations) || variations.ValueKind != JsonValueKind.Array)
        {
            return new List<UcpProductVariation>();
        }

        return variations.EnumerateArray()
            .Select(variation => new UcpProductVariation
            {
                Id = ReadString(variation, "id"),
                Code = ReadString(variation, "code"),
                Name = ReadString(variation, "name"),
                Price = ReadPrice(variation, "price", "actual"),
                Availability = ReadAvailability(variation),
                Attributes = ReadAttributes(variation),
            })
            .ToList();
    }

    protected virtual UcpProductAvailability ReadAvailability(JsonElement element)
    {
        if (!element.TryGetProperty("availabilityData", out var availability) || availability.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return new UcpProductAvailability
        {
            IsBuyable = ReadBoolean(availability, "isBuyable"),
            IsAvailable = ReadBoolean(availability, "isAvailable"),
            IsInStock = ReadBoolean(availability, "isInStock"),
            AvailableQuantity = ReadDecimal(availability, "availableQuantity"),
        };
    }

    protected virtual IList<UcpProductAttribute> ReadAttributes(JsonElement element)
    {
        if (!element.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Array)
        {
            return new List<UcpProductAttribute>();
        }

        return properties.EnumerateArray()
            .Select(property => new UcpProductAttribute
            {
                Name = ReadString(property, "name"),
                Value = ReadString(property, "value"),
            })
            .Where(attribute => !string.IsNullOrWhiteSpace(attribute.Name))
            .ToList();
    }

    protected virtual UcpMoney ReadPrice(JsonElement element, string pricePropertyName, string moneyPropertyName)
    {
        if (!element.TryGetProperty(pricePropertyName, out var price) ||
            price.ValueKind == JsonValueKind.Null ||
            !price.TryGetProperty(moneyPropertyName, out var money) ||
            money.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return new UcpMoney
        {
            Amount = ToMinorUnits(ReadDecimal(money, "amount")),
            Currency = money.TryGetProperty("currency", out var currency) ? ReadString(currency, "code") : null,
            FormattedAmount = ReadString(money, "formattedAmount"),
        };
    }

    private static bool IsRecoverablePropertyValueError(JsonElement error)
    {
        if (error.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.Array)
        {
            var pathSegments = path.EnumerateArray().ToList();
            if (pathSegments.Count > 0 &&
                pathSegments[^1].ValueKind == JsonValueKind.String &&
                string.Equals(pathSegments[^1].GetString(), "value", StringComparison.Ordinal))
            {
                return true;
            }
        }

        var message = ReadString(error, "message");
        return message?.Contains("resolve field 'value'", StringComparison.OrdinalIgnoreCase) == true;
    }

    protected const string ProductFields = """
        id
        code
        name
        slug
        imgSrc
        brandName
        productType
        price {
          actual { amount formattedAmount currency { code } }
          list { amount formattedAmount currency { code } }
        }
        availabilityData {
          availableQuantity
          isBuyable
          isAvailable
          isInStock
        }
        properties {
          name
          value
        }
        variations {
          id
          code
          name
          price {
            actual { amount formattedAmount currency { code } }
          }
          availabilityData {
            availableQuantity
            isBuyable
            isAvailable
            isInStock
          }
          properties {
            name
            value
          }
        }
        """;

    protected static readonly string SearchProductsQuery = $$"""
        query UcpSearchProducts($storeId: String!, $userId: String, $currencyCode: String, $cultureName: String, $query: String, $filter: String, $first: Int) {
          products(storeId: $storeId, userId: $userId, currencyCode: $currencyCode, cultureName: $cultureName, query: $query, filter: $filter, first: $first) {
            totalCount
            items {
        {{ProductFields}}
            }
          }
        }
        """;

    protected static readonly string GetProductQuery = $$"""
        query UcpGetProduct($id: String!, $storeId: String!, $userId: String, $currencyCode: String, $cultureName: String) {
          product(id: $id, storeId: $storeId, userId: $userId, currencyCode: $currencyCode, cultureName: $cultureName) {
        {{ProductFields}}
          }
        }
        """;

    protected static readonly string ProductMembershipQuery = """
        query UcpCheckProductMembership($storeId: String!, $userId: String, $currencyCode: String, $cultureName: String, $query: String, $first: Int) {
          products(storeId: $storeId, userId: $userId, currencyCode: $currencyCode, cultureName: $cultureName, query: $query, first: $first) {
            items {
              id
              variations {
                id
              }
            }
          }
        }
        """;
}
