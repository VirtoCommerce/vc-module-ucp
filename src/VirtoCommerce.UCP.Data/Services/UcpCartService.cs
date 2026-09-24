using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Diagnostics;
using VirtoCommerce.UCP.Core.Models;
using VirtoCommerce.UCP.Core.Options;
using VirtoCommerce.UCP.Core.Services;
using VirtoCommerce.UCP.Data.Models;

namespace VirtoCommerce.UCP.Data.Services;

public class UcpCartService : UcpServiceBase, IUcpCartService
{
    private const string DefaultCartName = "default";
    private const string DefaultCartType = "cart";
    private const int DefaultListLimit = 10;
    private const int MaxListLimit = 50;
    private const int BillingAddressType = 1;
    private const int ShippingAddressType = 2;
    private const int BillingAndShippingAddressType = 3;
    private const int PickupAddressType = 4;

    private static readonly Dictionary<string, string> MutationInputNames = new(StringComparer.Ordinal)
    {
        ["addItem"] = "AddItem",
        ["changeCartItemQuantity"] = "ChangeCartItemQuantity",
        ["removeCartItem"] = "RemoveItem",
        ["addCoupon"] = "AddCoupon",
        ["removeCoupon"] = "RemoveCoupon",
        ["addOrUpdateCartAddress"] = "AddOrUpdateCartAddress",
        ["addOrUpdateCartShipment"] = "AddOrUpdateCartShipment",
        ["addOrUpdateCartPayment"] = "AddOrUpdateCartPayment",
        ["mergeCart"] = "MergeCart",
    };

    private readonly IXApiInProcessExecutor _xApiExecutor;
    private readonly ICountriesService _countriesService;
    private readonly UcpOptions _options;

    public UcpCartService(
        IXApiInProcessExecutor xApiExecutor,
        IHttpContextAccessor httpContextAccessor,
        IOptions<UcpOptions> options,
        ICountriesService countriesService = null,
        IUcpBuyerContextAccessor buyerContextAccessor = null)
        : base(httpContextAccessor, buyerContextAccessor)
    {
        _xApiExecutor = xApiExecutor;
        _countriesService = countriesService;
        _options = options.Value;
    }

    public virtual async Task<UcpCartResponse> CreateCart(UcpCartRequest request, CancellationToken cancellationToken = default)
    {
        request ??= new UcpCartRequest();
        var cartRequest = BuildCartExecutionRequest(request, generateAnonymousBuyer: true);

        if (request.LineItems == null || request.LineItems.Count == 0)
        {
            throw CreateException(ModuleConstants.ErrorCodes.InvalidRequest, "line_items must contain at least one item.");
        }

        foreach (var lineItem in request.LineItems)
        {
            ValidateLineItemForAdd(lineItem);
        }

        var firstLineItem = request.LineItems[0];
        var cartElement = await ExecuteCartMutation(
            "addItem",
            "UcpAddCartItem",
            cartRequest,
            BuildAddItemCommand(cartRequest, null, firstLineItem),
            cancellationToken);
        cartRequest.CartId = ReadString(cartElement, "id");

        foreach (var lineItem in request.LineItems.Skip(1))
        {
            cartElement = await ExecuteCartMutation("addItem", "UcpAddCartItem", cartRequest, BuildAddItemCommand(cartRequest, null, lineItem), cancellationToken);
        }

        if (request.Coupons?.Count > 0)
        {
            cartRequest.CartId = ReadString(cartElement, "id");
            foreach (var coupon in NormalizeCoupons(request.Coupons))
            {
                cartElement = await ExecuteCartMutation("addCoupon", "UcpAddCartCoupon", cartRequest, BuildCouponCommand(cartRequest, coupon), cancellationToken);
            }
        }

        return CreateResponse(cartElement);
    }

    public virtual async Task<UcpCartListResponse> ListCarts(UcpCartListRequest request, CancellationToken cancellationToken = default)
    {
        request ??= new UcpCartListRequest();
        var cartRequest = BuildCartExecutionRequest(
            new UcpCartRequest { Context = request.Context },
            requireBuyer: true);

        if (string.IsNullOrWhiteSpace(cartRequest.UserId))
        {
            throw CreateException(ModuleConstants.ErrorCodes.InvalidRequest, "buyer_id is required for anonymous cart listing; authenticated mode uses the Platform token.");
        }

        var variables = new Dictionary<string, object>
        {
            ["storeId"] = cartRequest.StoreId,
            ["userId"] = cartRequest.UserId,
            ["currencyCode"] = cartRequest.Currency,
            ["cultureName"] = cartRequest.CultureName,
            ["cartType"] = cartRequest.CartType,
            ["first"] = Math.Clamp(request.Pagination?.Limit ?? DefaultListLimit, 1, MaxListLimit),
            ["after"] = request.Pagination?.Cursor,
            ["sort"] = request.Sort,
        };

        var result = await _xApiExecutor.ExecuteCart(new XApiExecutionRequest
        {
            Query = ListCartsQuery,
            OperationName = "UcpListCarts",
            Variables = variables,
            User = cartRequest.Principal,
        }, cancellationToken);

        using var document = ParseGraphQlResult(result, "XCart");
        var cartsElement = document.RootElement.GetProperty("data").GetProperty("carts");
        EnsureCartListOwnership(cartsElement, cartRequest);
        var carts = ReadCarts(cartsElement);

        return new UcpCartListResponse
        {
            Ucp = CreateMetadata("success", "dev.ucp.shopping.cart.list"),
            Carts = carts,
            Pagination = new UcpPaginationResponse
            {
                Cursor = cartsElement.TryGetProperty("pageInfo", out var pageInfo) ? ReadString(pageInfo, "endCursor") : null,
                HasNextPage = cartsElement.TryGetProperty("pageInfo", out pageInfo) && ReadBoolean(pageInfo, "hasNextPage"),
                TotalCount = ReadInt(cartsElement, "totalCount"),
            },
        };
    }

    public virtual async Task<UcpCartResponse> GetCart(string cartId, UcpCartRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cartId);

        request ??= new UcpCartRequest();
        var cartRequest = BuildCartExecutionRequest(request, requireBuyer: true);
        cartRequest.CartId = cartId;

        var cartElement = await ExecuteGetCart(cartRequest, cancellationToken);
        if (cartElement.ValueKind == JsonValueKind.Null)
        {
            throw CreateException(ModuleConstants.ErrorCodes.CartNotFound, $"Cart '{cartId}' was not found.", StatusCodes.Status404NotFound);
        }

        return CreateResponse(cartElement);
    }

    public virtual async Task<UcpCartResponse> UpdateCart(string cartId, UcpCartRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cartId);

        request ??= new UcpCartRequest();
        var cartRequest = BuildCartExecutionRequest(request, requireBuyer: true);
        cartRequest.CartId = cartId;

        var desiredItems = (request.LineItems ?? []).Select(x => x == null ? null : new UcpCartLineItemRequest
        {
            Id = x.Id,
            ProductId = x.ProductId,
            Quantity = x.Quantity,
        }).ToList();
        var currentCart = !string.IsNullOrWhiteSpace(cartRequest.SourceAnonymousBuyerId)
            ? await MergeAnonymousCart(cartRequest, desiredItems, cancellationToken)
            : await ExecuteGetCart(cartRequest, cancellationToken);
        if (currentCart.ValueKind == JsonValueKind.Null)
        {
            throw CreateException(ModuleConstants.ErrorCodes.CartNotFound, $"Cart '{cartId}' was not found.", StatusCodes.Status404NotFound);
        }

        var currentItems = ReadCartLineItems(currentCart);
        cartRequest.CartId = ReadString(currentCart, "id");
        var consolidatedItems = ValidateDesiredItems(cartRequest.CartId, desiredItems, currentItems);
        var cartElement = await RemoveMissingItems(currentCart, cartRequest, currentItems, consolidatedItems, cancellationToken);
        cartElement = await ApplyDesiredItems(cartRequest.CartId, cartElement, cartRequest, currentItems, consolidatedItems, cancellationToken);
        cartElement = await ApplyCoupons(cartElement, cartRequest, currentCart, request.Coupons, cancellationToken);

        return CreateResponse(cartElement);
    }

    private async Task<JsonElement> MergeAnonymousCart(
        CartExecutionRequest targetRequest,
        IList<UcpCartLineItemRequest> desiredItems,
        CancellationToken cancellationToken)
    {
        var sourceRequest = new CartExecutionRequest
        {
            CartId = targetRequest.CartId,
            StoreId = targetRequest.StoreId,
            Currency = targetRequest.Currency,
            CultureName = targetRequest.CultureName,
            CartName = targetRequest.CartName,
            CartType = targetRequest.CartType,
            UserId = targetRequest.SourceAnonymousBuyerId,
            Principal = BuildAnonymousBuyerPrincipal(targetRequest.SourceAnonymousBuyerId),
            IsAuthenticated = false,
        };
        var sourceCart = await ExecuteGetCart(sourceRequest, cancellationToken);
        if (sourceCart.ValueKind == JsonValueKind.Null)
        {
            targetRequest.CartId = null;
            return await ExecuteGetCart(targetRequest, cancellationToken);
        }

        if (!string.Equals(ReadString(sourceCart, "customerId"), targetRequest.SourceAnonymousBuyerId, StringComparison.Ordinal) ||
            !string.IsNullOrWhiteSpace(ReadString(sourceCart, "organizationId")))
        {
            throw CreateException(
                ModuleConstants.ErrorCodes.BuyerContextMismatch,
                "Anonymous cart does not belong to the supplied buyer context.",
                StatusCodes.Status403Forbidden);
        }

        var sourceCartId = targetRequest.CartId;
        var sourceItems = ReadCartLineItems(sourceCart);
        ValidateDesiredItems(sourceCartId, desiredItems, sourceItems);
        foreach (var desiredItem in desiredItems)
        {
            var sourceItem = FindCurrentItem(sourceCartId, sourceItems, desiredItem);
            desiredItem.ProductId = FirstNotEmpty(desiredItem.ProductId, sourceItem?.ProductId);
            // XCart assigns new line identifiers when it merges the source cart.
            desiredItem.Id = null;
        }

        targetRequest.CartId = null;
        var command = BuildBaseCommand(targetRequest);
        command["secondCartId"] = sourceCartId;
        command["deleteAfterMerge"] = true;

        var mergedCart = await ExecuteCartMutation("mergeCart", "UcpMergeCart", targetRequest, command, cancellationToken);
        targetRequest.CartId = ReadString(mergedCart, "id");
        return mergedCart;
    }

    private IList<UcpCartLineItemRequest> ValidateDesiredItems(
        string cartId,
        IList<UcpCartLineItemRequest> desiredItems,
        IList<UcpCartLineItem> currentItems)
    {
        foreach (var desiredItem in desiredItems)
        {
            if (desiredItem == null)
            {
                throw CreateException(ModuleConstants.ErrorCodes.InvalidRequest, "line_items must not contain null items.");
            }

            var currentItem = FindCurrentItem(cartId, currentItems, desiredItem);
            if (currentItem == null)
            {
                ValidateLineItemForAdd(desiredItem);
            }
            else if (!string.IsNullOrWhiteSpace(desiredItem.ProductId) &&
                !string.Equals(desiredItem.ProductId, currentItem.ProductId, StringComparison.OrdinalIgnoreCase))
            {
                throw CreateException(ModuleConstants.ErrorCodes.InvalidRequest, "Line item and product identifiers do not match.");
            }
        }

        return ConsolidateDesiredItems(desiredItems, currentItems);
    }

    public virtual async Task<UcpCartResponse> ApplyCheckoutData(string cartId, UcpCheckoutRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cartId);

        request ??= new UcpCheckoutRequest();
        var cartRequest = BuildCartExecutionRequest(
            new UcpCartRequest { Context = request.Context },
            requireBuyer: true);
        cartRequest.CartId = cartId;

        var cartElement = await ExecuteGetCart(cartRequest, cancellationToken);
        if (cartElement.ValueKind == JsonValueKind.Null)
        {
            throw CreateException(ModuleConstants.ErrorCodes.CartNotFound, $"Cart '{cartId}' was not found.", StatusCodes.Status404NotFound);
        }

        var currentCart = ReadCart(cartElement);
        var shippingAddress = await PrepareAddress(request.ShippingAddress, request.Buyer, cancellationToken);
        var billingAddress = await PrepareAddress(request.BillingAddress, request.Buyer, cancellationToken);

        if (shippingAddress != null)
        {
            ValidateRecipientName(shippingAddress, "shipping_address");

            var cartAddress = WithAddressId(shippingAddress, GetExistingCartAddressId(currentCart, "shipping"));
            cartElement = await ExecuteCartMutation(
                "addOrUpdateCartAddress",
                "UcpAddOrUpdateShippingAddress",
                cartRequest,
                BuildAddressCommand(cartRequest, cartAddress, ShippingAddressType),
                cancellationToken);

            var shipmentAddress = WithAddressId(shippingAddress, GetExistingShipmentAddressId(currentCart) ?? cartAddress.Id);
            cartElement = await ExecuteCartMutation(
                "addOrUpdateCartShipment",
                "UcpAddOrUpdateShipmentAddress",
                cartRequest,
                BuildShipmentCommand(cartRequest, shipmentAddress, ReadFirstArrayObjectString(cartElement, "shipments", "id") ?? GetExistingShipmentId(currentCart)),
                cancellationToken);
        }

        if (billingAddress != null)
        {
            ValidateRecipientName(billingAddress, "billing_address");

            var cartAddress = WithAddressId(billingAddress, GetExistingCartAddressId(currentCart, "billing"));
            cartElement = await ExecuteCartMutation(
                "addOrUpdateCartAddress",
                "UcpAddOrUpdateBillingAddress",
                cartRequest,
                BuildAddressCommand(cartRequest, cartAddress, BillingAddressType),
                cancellationToken);

            var paymentAddress = WithAddressId(billingAddress, GetExistingPaymentAddressId(currentCart) ?? cartAddress.Id);
            cartElement = await ExecuteCartMutation(
                "addOrUpdateCartPayment",
                "UcpAddOrUpdatePaymentAddress",
                cartRequest,
                BuildPaymentCommand(cartRequest, paymentAddress, ReadFirstArrayObjectString(cartElement, "payments", "id") ?? GetExistingPaymentId(currentCart)),
                cancellationToken);
        }

        return CreateResponse(cartElement);
    }

    private CartExecutionRequest BuildCartExecutionRequest(
        UcpCartRequest request,
        bool generateAnonymousBuyer = false,
        bool requireBuyer = false)
    {
        var buyerContext = ResolveBuyerContext(
            requestedBuyerIds: [request.BuyerId, request.Context?.BuyerId],
            requestedOrganizationIds: [request.OrganizationId, request.Context?.OrganizationId],
            createAnonymousBuyer: generateAnonymousBuyer,
            requireBuyer: requireBuyer);
        var result = new CartExecutionRequest
        {
            StoreId = ResolveStoreId(request),
            Currency = ResolveCurrency(request),
            CultureName = ResolveCultureName(request),
            CartName = ResolveCartName(request),
            CartType = ResolveCartType(request),
            UserId = buyerContext.UserId,
            SourceAnonymousBuyerId = buyerContext.SourceAnonymousBuyerId,
            OrganizationId = buyerContext.OrganizationId,
            Principal = buyerContext.Principal,
            IsAuthenticated = buyerContext.IsAuthenticated,
        };

        ValidateCartExecutionRequest(result);

        return result;
    }

    private string ResolveStoreId(UcpCartRequest request)
    {
        return FirstNotEmpty(request.StoreId, request.Context?.StoreId, _options.DefaultStoreId);
    }

    private string ResolveCurrency(UcpCartRequest request)
    {
        return FirstNotEmpty(request.Currency, request.Context?.Currency, _options.DefaultCurrency);
    }

    private string ResolveCultureName(UcpCartRequest request)
    {
        return FirstNotEmpty(request.Language, request.Context?.Language, _options.DefaultCultureName);
    }

    private static string ResolveCartName(UcpCartRequest request)
    {
        return FirstNotEmpty(request.CartName, request.Context?.CartName, DefaultCartName);
    }

    private static string ResolveCartType(UcpCartRequest request)
    {
        return FirstNotEmpty(request.CartType, request.Context?.CartType, DefaultCartType);
    }

    private void ValidateCartExecutionRequest(CartExecutionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.StoreId))
        {
            throw CreateException(ModuleConstants.ErrorCodes.MissingStoreId, "store_id or context.store_id is required when UCP:DefaultStoreId is not configured.");
        }

        if (string.IsNullOrWhiteSpace(request.Currency))
        {
            throw CreateException(ModuleConstants.ErrorCodes.InvalidRequest, "context.currency is required when UCP:DefaultCurrency is not configured.");
        }
    }

    protected virtual void ValidateLineItemForAdd(UcpCartLineItemRequest lineItem)
    {
        if (lineItem == null || string.IsNullOrWhiteSpace(lineItem.ProductId))
        {
            throw CreateException(ModuleConstants.ErrorCodes.InvalidRequest, "line_items[].product_id is required.");
        }

        if (lineItem.Quantity <= 0)
        {
            throw CreateException(ModuleConstants.ErrorCodes.InvalidRequest, "line_items[].quantity must be greater than zero.");
        }
    }

    private async Task<JsonElement> RemoveMissingItems(
        JsonElement cartElement,
        CartExecutionRequest cartRequest,
        IList<UcpCartLineItem> currentItems,
        IList<UcpCartLineItemRequest> desiredItems,
        CancellationToken cancellationToken)
    {
        foreach (var currentItem in currentItems.Where(current => !HasDesiredMatch(current, desiredItems)))
        {
            cartElement = await ExecuteCartMutation(
                "removeCartItem",
                "UcpRemoveCartItem",
                cartRequest,
                BuildLineItemCommand(cartRequest, currentItem.Id),
                cancellationToken);
        }

        return cartElement;
    }

    protected virtual IList<UcpCartLineItemRequest> ConsolidateDesiredItems(
        IEnumerable<UcpCartLineItemRequest> desiredItems,
        IEnumerable<UcpCartLineItem> currentItems)
    {
        var result = new List<UcpCartLineItemRequest>();
        var byProductId = new Dictionary<string, UcpCartLineItemRequest>(StringComparer.OrdinalIgnoreCase);

        foreach (var desiredItem in desiredItems ?? [])
        {
            if (desiredItem == null)
            {
                continue;
            }

            var currentItem = ResolveCurrentItemForConsolidation(desiredItem, currentItems);
            var productId = FirstNotEmpty(desiredItem.ProductId, currentItem?.ProductId);
            if (string.IsNullOrWhiteSpace(productId))
            {
                result.Add(desiredItem);
                continue;
            }

            if (!byProductId.TryGetValue(productId, out var consolidated))
            {
                consolidated = new UcpCartLineItemRequest
                {
                    Id = currentItem?.Id ?? desiredItem.Id,
                    ProductId = productId,
                    Quantity = desiredItem.Quantity,
                };
                byProductId[productId] = consolidated;
                result.Add(consolidated);
                continue;
            }

            consolidated.Quantity = CombineQuantities(consolidated.Quantity, desiredItem.Quantity);
            consolidated.Id = FirstNotEmpty(consolidated.Id, currentItem?.Id, desiredItem.Id);
        }

        return result;
    }

    private int CombineQuantities(int currentQuantity, int additionalQuantity)
    {
        var quantity = (long)currentQuantity + additionalQuantity;
        if (quantity > int.MaxValue || quantity < int.MinValue)
        {
            throw CreateException(ModuleConstants.ErrorCodes.InvalidRequest, "The combined line item quantity is out of range.");
        }

        return (int)quantity;
    }

    protected virtual UcpCartLineItem ResolveCurrentItemForConsolidation(UcpCartLineItemRequest desiredItem, IEnumerable<UcpCartLineItem> currentItems)
    {
        if (desiredItem == null || string.IsNullOrWhiteSpace(desiredItem.Id))
        {
            return null;
        }

        return currentItems.FirstOrDefault(item => string.Equals(item.Id, desiredItem.Id, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<JsonElement> ApplyDesiredItems(
        string cartId,
        JsonElement cartElement,
        CartExecutionRequest cartRequest,
        IList<UcpCartLineItem> currentItems,
        IEnumerable<UcpCartLineItemRequest> desiredItems,
        CancellationToken cancellationToken)
    {
        foreach (var desiredItem in desiredItems)
        {
            cartElement = await ApplyDesiredItem(cartId, cartElement, cartRequest, currentItems, desiredItem, cancellationToken);
        }

        return cartElement;
    }

    private async Task<JsonElement> ApplyDesiredItem(
        string cartId,
        JsonElement cartElement,
        CartExecutionRequest cartRequest,
        IList<UcpCartLineItem> currentItems,
        UcpCartLineItemRequest desiredItem,
        CancellationToken cancellationToken)
    {
        var currentItem = FindCurrentItem(cartId, currentItems, desiredItem);
        if (currentItem == null)
        {
            ValidateLineItemForAdd(desiredItem);

            return await ExecuteCartMutation(
                "addItem",
                "UcpAddCartItem",
                cartRequest,
                BuildAddItemCommand(cartRequest, cartId, desiredItem),
                cancellationToken);
        }

        return await ApplyMatchedLineItem(cartElement, cartRequest, currentItem, desiredItem.Quantity, cancellationToken);
    }

    private UcpCartLineItem FindCurrentItem(string cartId, IEnumerable<UcpCartLineItem> currentItems, UcpCartLineItemRequest desiredItem)
    {
        if (!string.IsNullOrWhiteSpace(desiredItem.Id))
        {
            var currentItem = currentItems.FirstOrDefault(item => string.Equals(item.Id, desiredItem.Id, StringComparison.OrdinalIgnoreCase));
            if (currentItem == null)
            {
                throw CreateException(ModuleConstants.ErrorCodes.InvalidRequest, $"Line item '{desiredItem.Id}' was not found in cart '{cartId}'.");
            }

            return currentItem;
        }

        return currentItems.FirstOrDefault(item => string.Equals(item.ProductId, desiredItem.ProductId, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<JsonElement> ApplyMatchedLineItem(
        JsonElement cartElement,
        CartExecutionRequest cartRequest,
        UcpCartLineItem currentItem,
        int desiredQuantity,
        CancellationToken cancellationToken)
    {
        if (desiredQuantity <= 0)
        {
            return await ExecuteCartMutation(
                "removeCartItem",
                "UcpRemoveCartItem",
                cartRequest,
                BuildLineItemCommand(cartRequest, currentItem.Id),
                cancellationToken);
        }

        if (currentItem.Quantity == desiredQuantity)
        {
            return cartElement;
        }

        return await ExecuteCartMutation(
            "changeCartItemQuantity",
            "UcpChangeCartItemQuantity",
            cartRequest,
            BuildQuantityCommand(cartRequest, currentItem.Id, desiredQuantity),
            cancellationToken);
    }

    private async Task<JsonElement> ApplyCoupons(
        JsonElement cartElement,
        CartExecutionRequest cartRequest,
        JsonElement currentCart,
        IEnumerable<string> coupons,
        CancellationToken cancellationToken)
    {
        var desiredCoupons = NormalizeCoupons(coupons);
        var currentCoupons = ReadCartCoupons(currentCart)
            .Select(coupon => coupon.Code)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var coupon in currentCoupons.Where(coupon => !desiredCoupons.Contains(coupon)))
        {
            cartElement = await ExecuteCartMutation(
                "removeCoupon",
                "UcpRemoveCartCoupon",
                cartRequest,
                BuildCouponCommand(cartRequest, coupon),
                cancellationToken);
        }

        foreach (var coupon in desiredCoupons.Where(coupon => !currentCoupons.Contains(coupon)))
        {
            cartElement = await ExecuteCartMutation(
                "addCoupon",
                "UcpAddCartCoupon",
                cartRequest,
                BuildCouponCommand(cartRequest, coupon),
                cancellationToken);
        }

        return cartElement;
    }

    private async Task<JsonElement> ExecuteGetCart(CartExecutionRequest cartRequest, CancellationToken cancellationToken)
    {
        var variables = new Dictionary<string, object>
        {
            ["cartId"] = cartRequest.CartId,
            ["storeId"] = cartRequest.StoreId,
            ["userId"] = cartRequest.UserId,
            ["currencyCode"] = cartRequest.Currency,
            ["cultureName"] = cartRequest.CultureName,
            ["cartName"] = cartRequest.CartName,
            ["cartType"] = cartRequest.CartType,
        };

        var result = await _xApiExecutor.ExecuteCart(new XApiExecutionRequest
        {
            Query = GetCartQuery,
            OperationName = "UcpGetCart",
            Variables = variables,
            User = cartRequest.Principal,
        }, cancellationToken);

        using var document = ParseGraphQlResult(result, "XCart");
        var cart = document.RootElement.GetProperty("data").GetProperty("cart").Clone();
        EnsureCartOwnership(cart, cartRequest);
        return cart;
    }

    private async Task<JsonElement> ExecuteCartMutation(string mutationName, string operationName, CartExecutionRequest cartRequest, IDictionary<string, object> command, CancellationToken cancellationToken)
    {
        var result = await _xApiExecutor.ExecuteCart(new XApiExecutionRequest
        {
            Query = BuildCartMutation(mutationName, operationName),
            OperationName = operationName,
            Variables = new Dictionary<string, object> { ["command"] = command },
            User = cartRequest.Principal,
        }, cancellationToken);

        using var document = ParseGraphQlResult(result, "XCart");
        var cart = document.RootElement.GetProperty("data").GetProperty(mutationName).Clone();
        EnsureCartOwnership(cart, cartRequest);
        return cart;
    }

    private void EnsureCartOwnership(JsonElement cart, CartExecutionRequest request)
    {
        if (cart.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        var customerId = ReadString(cart, "customerId");
        var organizationId = ReadString(cart, "organizationId");
        var ownerMatches = string.Equals(customerId, request.UserId, StringComparison.Ordinal) &&
            string.Equals(organizationId ?? string.Empty, request.OrganizationId ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        var cartTypeMatches = request.IsAuthenticated
            ? !ReadBoolean(cart, "isAnonymous")
            : ReadBoolean(cart, "isAnonymous") && string.IsNullOrWhiteSpace(organizationId);

        if (!ownerMatches || !cartTypeMatches)
        {
            throw CreateException(
                ModuleConstants.ErrorCodes.BuyerContextMismatch,
                "Cart does not belong to the resolved buyer context.",
                StatusCodes.Status403Forbidden);
        }
    }

    private void EnsureCartListOwnership(JsonElement carts, CartExecutionRequest request)
    {
        if (!carts.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var cart in items.EnumerateArray())
        {
            EnsureCartOwnership(cart, request);
        }
    }

    private static Dictionary<string, object> BuildAddItemCommand(CartExecutionRequest request, string cartId, UcpCartLineItemRequest lineItem)
    {
        var command = BuildBaseCommand(request);
        command["cartId"] = FirstNotEmpty(cartId, request.CartId);
        command["productId"] = lineItem.ProductId;
        command["quantity"] = lineItem.Quantity;
        return command;
    }

    private static Dictionary<string, object> BuildLineItemCommand(CartExecutionRequest request, string lineItemId)
    {
        var command = BuildBaseCommand(request);
        command["cartId"] = request.CartId;
        command["lineItemId"] = lineItemId;
        return command;
    }

    private static Dictionary<string, object> BuildQuantityCommand(CartExecutionRequest request, string lineItemId, int quantity)
    {
        var command = BuildLineItemCommand(request, lineItemId);
        command["quantity"] = quantity;
        return command;
    }

    private static Dictionary<string, object> BuildCouponCommand(CartExecutionRequest request, string coupon)
    {
        var command = BuildBaseCommand(request);
        command["cartId"] = request.CartId;
        command["couponCode"] = coupon;
        return command;
    }

    private Dictionary<string, object> BuildAddressCommand(CartExecutionRequest request, UcpCheckoutAddress address, int addressType)
    {
        var command = BuildBaseCommand(request);
        command["cartId"] = request.CartId;
        command["address"] = BuildAddress(address, addressType);
        return command;
    }

    private Dictionary<string, object> BuildShipmentCommand(CartExecutionRequest request, UcpCheckoutAddress address, string shipmentId)
    {
        var command = BuildBaseCommand(request);
        command["cartId"] = request.CartId;
        var shipment = new Dictionary<string, object>
        {
            ["deliveryAddress"] = BuildAddress(address, ShippingAddressType),
        };

        AddIfNotEmpty(shipment, "id", shipmentId);
        command["shipment"] = shipment;
        return command;
    }

    private Dictionary<string, object> BuildPaymentCommand(CartExecutionRequest request, UcpCheckoutAddress address, string paymentId)
    {
        var command = BuildBaseCommand(request);
        command["cartId"] = request.CartId;
        var payment = new Dictionary<string, object>
        {
            ["billingAddress"] = BuildAddress(address, BillingAddressType),
        };

        AddIfNotEmpty(payment, "id", paymentId);
        command["payment"] = payment;
        return command;
    }

    protected virtual IDictionary<string, object> BuildAddress(UcpCheckoutAddress address, int addressType)
    {
        var postalCode = address.PostalCode ?? string.Empty;
        var result = new Dictionary<string, object>
        {
            ["addressType"] = addressType,
            ["postalCode"] = postalCode,
            ["zip"] = postalCode,
        };

        AddIfNotEmpty(result, "key", address.Id);
        AddIfNotEmpty(result, "id", address.Id);
        AddIfNotEmpty(result, "name", address.Name);
        AddIfNotEmpty(result, "organization", address.Organization);
        AddIfNotEmpty(result, "firstName", address.FirstName);
        AddIfNotEmpty(result, "lastName", address.LastName);
        AddIfNotEmpty(result, "line1", address.Line1);
        AddIfNotEmpty(result, "line2", address.Line2);
        AddIfNotEmpty(result, "city", address.City);
        result["regionName"] = address.Region ?? string.Empty;
        result["regionId"] = address.RegionId ?? string.Empty;
        AddIfNotEmpty(result, "countryCode", address.CountryCode);
        AddIfNotEmpty(result, "countryName", address.CountryName);
        AddIfNotEmpty(result, "phone", address.Phone);
        AddIfNotEmpty(result, "email", address.Email);

        return result;
    }

    protected virtual async Task<UcpCheckoutAddress> PrepareAddress(UcpCheckoutAddress address, UcpCheckoutBuyer buyer, CancellationToken cancellationToken)
    {
        if (address == null)
        {
            return null;
        }

        var result = CloneAddress(address);
        ApplyBuyerContact(result, buyer);
        await NormalizeCountryAndRegion(result, cancellationToken);

        return result;
    }

    protected virtual async Task NormalizeCountryAndRegion(UcpCheckoutAddress address, CancellationToken cancellationToken)
    {
        if (_countriesService == null || address == null)
        {
            return;
        }

        var country = await ResolveCountry(address, cancellationToken);
        if (country == null)
        {
            return;
        }

        address.CountryCode = country.Id;
        address.CountryName = country.Name;

        await NormalizeRegion(address, country.Id, cancellationToken);
    }

    protected virtual async Task<Country> ResolveCountry(UcpCheckoutAddress address, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(address.CountryCode))
        {
            try
            {
                return _countriesService.GetByCode(address.CountryCode.Trim());
            }
            catch (ArgumentException)
            {
                return await ResolveCountryByName(address.CountryName, cancellationToken);
            }
        }

        return await ResolveCountryByName(address.CountryName, cancellationToken);
    }

    protected virtual async Task<Country> ResolveCountryByName(string countryName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(countryName))
        {
            return null;
        }

        var countries = await UcpDiagnostics.ExecuteDependency(
            "countries",
            "GetCountries",
            _countriesService.GetCountriesAsync);
        cancellationToken.ThrowIfCancellationRequested();

        return countries.FirstOrDefault(country => string.Equals(country.Name, countryName, StringComparison.OrdinalIgnoreCase));
    }

    protected virtual async Task NormalizeRegion(UcpCheckoutAddress address, string countryId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(countryId) || string.IsNullOrWhiteSpace(FirstNotEmpty(address.RegionId, address.Region)))
        {
            return;
        }

        var regions = await UcpDiagnostics.ExecuteDependency(
            "countries",
            "GetCountryRegions",
            () => _countriesService.GetCountryRegionsAsync(countryId));
        cancellationToken.ThrowIfCancellationRequested();

        var region = regions.FirstOrDefault(x => string.Equals(x.Id, address.RegionId, StringComparison.OrdinalIgnoreCase))
            ?? regions.FirstOrDefault(x => string.Equals(x.Name, address.Region, StringComparison.OrdinalIgnoreCase))
            ?? regions.FirstOrDefault(x => string.Equals(x.Id, address.Region, StringComparison.OrdinalIgnoreCase));

        if (region == null)
        {
            return;
        }

        address.RegionId = region.Id;
        address.Region = region.Name;
    }

    protected virtual void ApplyBuyerContact(UcpCheckoutAddress address, UcpCheckoutBuyer buyer)
    {
        if (address == null || buyer == null)
        {
            return;
        }

        AddBuyerName(address, buyer.Name);
        address.Email = FirstNotEmpty(address.Email, buyer.Email);
        address.Phone = FirstNotEmpty(address.Phone, buyer.Phone);
    }

    protected virtual void AddBuyerName(UcpCheckoutAddress address, string buyerName)
    {
        if (address == null || string.IsNullOrWhiteSpace(buyerName))
        {
            return;
        }

        var names = buyerName.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Length > 0)
        {
            address.FirstName = FirstNotEmpty(address.FirstName, names[0]);
        }

        if (names.Length > 1)
        {
            address.LastName = FirstNotEmpty(address.LastName, names[1]);
        }
    }

    protected virtual void ValidateRecipientName(UcpCheckoutAddress address, string fieldName)
    {
        if (address == null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(address.FirstName) || string.IsNullOrWhiteSpace(address.LastName))
        {
            throw CreateException(
                ModuleConstants.ErrorCodes.InvalidRequest,
                $"{fieldName}.first_name and {fieldName}.last_name are required.");
        }
    }

    protected virtual UcpCheckoutAddress CloneAddress(UcpCheckoutAddress address)
    {
        return new UcpCheckoutAddress
        {
            Id = address.Id,
            Name = address.Name,
            Organization = address.Organization,
            FirstName = address.FirstName,
            LastName = address.LastName,
            Line1 = address.Line1,
            Line2 = address.Line2,
            City = address.City,
            Region = address.Region,
            RegionId = address.RegionId,
            PostalCode = address.PostalCode,
            CountryCode = address.CountryCode,
            CountryName = address.CountryName,
            Phone = address.Phone,
            Email = address.Email,
        };
    }

    private static Dictionary<string, object> BuildBaseCommand(CartExecutionRequest request)
    {
        return new Dictionary<string, object>
        {
            ["storeId"] = request.StoreId,
            ["cartName"] = request.CartName,
            ["userId"] = request.UserId,
            ["currencyCode"] = request.Currency,
            ["cultureName"] = request.CultureName,
            ["cartType"] = request.CartType,
        };
    }

    protected virtual UcpCartResponse CreateResponse(JsonElement cartElement)
    {
        var cart = ReadCart(cartElement);

        return new UcpCartResponse
        {
            Ucp = CreateMetadata("success", "dev.ucp.shopping.cart"),
            Cart = cart,
            Messages = cart.Messages,
        };
    }

    protected virtual UcpCart ReadCart(JsonElement element)
    {
        var cart = new UcpCart
        {
            Id = ReadString(element, "id"),
            Status = ReadString(element, "status"),
            StoreId = ReadString(element, "storeId"),
            Currency = element.TryGetProperty("currency", out var currency) ? ReadString(currency, "code") : null,
            CartName = ReadString(element, "name"),
            CartType = ReadString(element, "type"),
            BuyerId = ReadString(element, "customerId"),
            OrganizationId = ReadString(element, "organizationId"),
            LineItems = ReadCartLineItems(element),
            Totals = new UcpCartTotals
            {
                Subtotal = ReadMoney(element, "subTotal"),
                Total = ReadMoney(element, "total"),
                TaxTotal = ReadMoney(element, "taxTotal"),
                DiscountTotal = ReadMoney(element, "discountTotal"),
                ShippingTotal = ReadMoney(element, "shippingTotal"),
                PaymentTotal = ReadMoney(element, "paymentTotal"),
                FeeTotal = ReadMoney(element, "feeTotal"),
            },
            Coupons = ReadCartCoupons(element),
            Addresses = ReadCartAddresses(element),
            Shipments = ReadCartShipments(element),
            Payments = ReadCartPayments(element),
        };

        cart.ContinueUrl = BuildContinueUrl(cart.Id);
        cart.Messages = ReadMessages(element);
        AddCouponMessages(cart);

        return cart;
    }

    protected virtual IList<UcpCartLineItem> ReadCartLineItems(JsonElement element)
    {
        if (!element.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return new List<UcpCartLineItem>();
        }

        return items.EnumerateArray()
            .Select(item => new UcpCartLineItem
            {
                Id = ReadString(item, "id"),
                ProductId = ReadString(item, "productId"),
                Sku = ReadString(item, "sku"),
                Name = ReadString(item, "name"),
                ImageUrl = FirstNotEmpty(ReadString(item, "imageUrl"), ReadString(item, "thumbnailImageUrl")),
                Quantity = ReadInt(item, "quantity"),
                UnitPrice = ReadMoney(item, "placedPrice"),
                ListPrice = ReadMoney(item, "listPrice"),
                LineTotal = ReadMoney(item, "extendedPrice"),
                DiscountTotal = ReadMoney(item, "discountTotal"),
                TaxTotal = ReadMoney(item, "taxTotal"),
                Messages = ReadMessages(item),
            })
            .ToList();
    }

    protected virtual IList<UcpCartCoupon> ReadCartCoupons(JsonElement element)
    {
        if (!element.TryGetProperty("coupons", out var coupons) || coupons.ValueKind != JsonValueKind.Array)
        {
            return new List<UcpCartCoupon>();
        }

        return coupons.EnumerateArray()
            .Select(coupon => new UcpCartCoupon
            {
                Code = ReadString(coupon, "code"),
                Applied = ReadBoolean(coupon, "isAppliedSuccessfully"),
            })
            .ToList();
    }

    protected virtual IList<UcpCartAddress> ReadCartAddresses(JsonElement element)
    {
        if (!element.TryGetProperty("addresses", out var addresses) || addresses.ValueKind != JsonValueKind.Array)
        {
            return new List<UcpCartAddress>();
        }

        return addresses.EnumerateArray()
            .Select(ReadCartAddress)
            .ToList();
    }

    protected virtual IList<UcpCartShipment> ReadCartShipments(JsonElement element)
    {
        if (!element.TryGetProperty("shipments", out var shipments) || shipments.ValueKind != JsonValueKind.Array)
        {
            return new List<UcpCartShipment>();
        }

        return shipments.EnumerateArray()
            .Select(shipment => new UcpCartShipment
            {
                Id = ReadString(shipment, "id"),
                ShipmentMethodCode = ReadString(shipment, "shipmentMethodCode"),
                ShipmentMethodOption = ReadString(shipment, "shipmentMethodOption"),
                Price = ReadMoney(shipment, "price"),
                DeliveryAddress = shipment.TryGetProperty("deliveryAddress", out var address) ? ReadCartAddress(address) : null,
            })
            .ToList();
    }

    protected virtual IList<UcpCartPayment> ReadCartPayments(JsonElement element)
    {
        if (!element.TryGetProperty("payments", out var payments) || payments.ValueKind != JsonValueKind.Array)
        {
            return new List<UcpCartPayment>();
        }

        return payments.EnumerateArray()
            .Select(payment => new UcpCartPayment
            {
                Id = ReadString(payment, "id"),
                PaymentGatewayCode = ReadString(payment, "paymentGatewayCode"),
                Amount = ReadMoney(payment, "amount"),
                BillingAddress = payment.TryGetProperty("billingAddress", out var address) ? ReadCartAddress(address) : null,
            })
            .ToList();
    }

    protected virtual UcpCartAddress ReadCartAddress(JsonElement address)
    {
        if (address.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return new UcpCartAddress
        {
            Id = FirstNotEmpty(ReadString(address, "id"), ReadString(address, "key")),
            AddressType = ReadAddressType(address),
            Name = ReadString(address, "name"),
            Organization = ReadString(address, "organization"),
            FirstName = ReadString(address, "firstName"),
            LastName = ReadString(address, "lastName"),
            Line1 = ReadString(address, "line1"),
            Line2 = ReadString(address, "line2"),
            City = ReadString(address, "city"),
            Region = ReadString(address, "regionName"),
            RegionId = ReadString(address, "regionId"),
            PostalCode = FirstNotEmpty(ReadString(address, "postalCode"), ReadString(address, "zip"), ReadString(address, "postal_code")),
            CountryCode = ReadString(address, "countryCode"),
            CountryName = ReadString(address, "countryName"),
            Phone = ReadString(address, "phone"),
            Email = ReadString(address, "email"),
        };
    }

    protected virtual IList<UcpCart> ReadCarts(JsonElement element)
    {
        if (!element.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return new List<UcpCart>();
        }

        return items.EnumerateArray()
            .Select(ReadCart)
            .ToList();
    }

    protected virtual IList<UcpMessage> ReadMessages(JsonElement element)
    {
        var messages = new List<UcpMessage>();
        AddMessages(messages, element, "validationErrors", "error");
        AddMessages(messages, element, "warnings", "warning");
        return messages;
    }

    protected virtual void AddCouponMessages(UcpCart cart)
    {
        var rejectedCouponCodes = cart.Coupons
            .Where(coupon => !coupon.Applied && !string.IsNullOrWhiteSpace(coupon.Code))
            .Select(coupon => coupon.Code);

        foreach (var couponCode in rejectedCouponCodes)
        {
            if (cart.Messages.Any(message => string.Equals(message.Code, "coupon_rejected", StringComparison.OrdinalIgnoreCase)
                && message.Content?.Contains(couponCode, StringComparison.OrdinalIgnoreCase) == true))
            {
                continue;
            }

            cart.Messages.Add(new UcpMessage
            {
                Type = "warning",
                Code = "coupon_rejected",
                Content = $"Coupon '{couponCode}' was not applied.",
                Severity = "warning",
            });
        }
    }

    protected virtual void AddMessages(IList<UcpMessage> messages, JsonElement element, string propertyName, string type)
    {
        if (!element.TryGetProperty(propertyName, out var errors) || errors.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var error in errors.EnumerateArray())
        {
            messages.Add(new UcpMessage
            {
                Type = type,
                Code = FirstNotEmpty(ReadString(error, "errorCode"), propertyName),
                Content = FirstNotEmpty(ReadString(error, "errorMessage"), ReadString(error, "message")),
                Severity = type == "error" ? "recoverable" : "info",
            });
        }
    }

    protected virtual UcpMoney ReadMoney(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var money) || money.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return new UcpMoney
        {
            Amount = ToMinorUnits(ReadDecimal(money, "amount")),
            Currency = money.TryGetProperty("currency", out var currency) ? ReadString(currency, "code") : ReadString(element, "currency"),
            FormattedAmount = ReadString(money, "formattedAmount"),
        };
    }

    protected virtual string BuildContinueUrl(string cartId)
    {
        var origin = _options.StorefrontOrigin?.TrimEnd('/');
        return string.IsNullOrWhiteSpace(origin) || string.IsNullOrWhiteSpace(cartId)
            ? null
            : $"{origin}/cart?cart_id={Uri.EscapeDataString(cartId)}";
    }

    protected static bool HasDesiredMatch(UcpCartLineItem current, IEnumerable<UcpCartLineItemRequest> desiredItems)
    {
        return desiredItems.Any(desired =>
            !string.IsNullOrWhiteSpace(desired.Id) && string.Equals(desired.Id, current.Id, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(desired.Id) && string.Equals(desired.ProductId, current.ProductId, StringComparison.OrdinalIgnoreCase));
    }

    protected static HashSet<string> NormalizeCoupons(IEnumerable<string> coupons)
    {
        return coupons?
            .Where(coupon => !string.IsNullOrWhiteSpace(coupon))
            .Select(coupon => coupon.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    protected static void AddIfNotEmpty(IDictionary<string, object> target, string key, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            target[key] = value;
        }
    }

    protected static string ReadAddressType(JsonElement element)
    {
        return ReadInt(element, "addressType") switch
        {
            BillingAddressType => "billing",
            ShippingAddressType => "shipping",
            BillingAndShippingAddressType => "billing_and_shipping",
            PickupAddressType => "pickup",
            _ => null,
        };
    }

    protected static string BuildCartMutation(string mutationName, string operationName)
    {
        return $$"""
            mutation {{operationName}}($command: Input{{GetMutationInputName(mutationName)}}Type!) {
              {{mutationName}}(command: $command) {
            {{CartFields}}
              }
            }
            """;
    }

    protected static string GetMutationInputName(string mutationName)
    {
        if (MutationInputNames.TryGetValue(mutationName, out var inputName))
        {
            return inputName;
        }

        throw new InvalidOperationException($"Unsupported cart mutation '{mutationName}'.");
    }

    protected static string ReadFirstArrayObjectString(JsonElement element, string arrayPropertyName, string propertyName)
    {
        if (!element.TryGetProperty(arrayPropertyName, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return array.EnumerateArray()
            .Select(item => ReadString(item, propertyName))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    protected static string GetExistingCartAddressId(UcpCart cart, string addressType)
    {
        return cart?.Addresses.LastOrDefault(x => string.Equals(x.AddressType, addressType, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    protected static string GetExistingShipmentId(UcpCart cart)
    {
        return cart?.Shipments.LastOrDefault()?.Id;
    }

    protected static string GetExistingShipmentAddressId(UcpCart cart)
    {
        return cart?.Shipments.LastOrDefault(x => x.DeliveryAddress != null)?.DeliveryAddress?.Id;
    }

    protected static string GetExistingPaymentId(UcpCart cart)
    {
        return cart?.Payments.LastOrDefault()?.Id;
    }

    protected static string GetExistingPaymentAddressId(UcpCart cart)
    {
        return cart?.Payments.LastOrDefault(x => x.BillingAddress != null)?.BillingAddress?.Id;
    }

    protected UcpCheckoutAddress WithAddressId(UcpCheckoutAddress address, string id)
    {
        if (address == null || string.IsNullOrWhiteSpace(id) || !string.IsNullOrWhiteSpace(address.Id))
        {
            return address;
        }

        var result = CloneAddress(address);
        result.Id = id;
        return result;
    }

    protected const string MoneyFields = """
          amount
          formattedAmount
          currency { code }
        """;

    protected const string CartFields = $$"""
        id
        name
        status
        storeId
        type
        isAnonymous
        customerId
        organizationId
        currency { code }
        total {
        {{MoneyFields}}
        }
        subTotal {
        {{MoneyFields}}
        }
        taxTotal {
        {{MoneyFields}}
        }
        discountTotal {
        {{MoneyFields}}
        }
        shippingTotal {
        {{MoneyFields}}
        }
        paymentTotal {
        {{MoneyFields}}
        }
        feeTotal {
        {{MoneyFields}}
        }
        coupons {
          code
          isAppliedSuccessfully
        }
        addresses {
          id
          key
          addressType
          name
          organization
          firstName
          lastName
          line1
          line2
          city
          countryCode
          countryName
          regionId
          regionName
          postalCode
          zip
          phone
          email
        }
        shipments {
          id
          shipmentMethodCode
          shipmentMethodOption
          price {
        {{MoneyFields}}
          }
          deliveryAddress {
            id
            key
            addressType
            name
            organization
            firstName
            lastName
            line1
            line2
            city
            countryCode
            countryName
            regionId
            regionName
            postalCode
            zip
            phone
            email
          }
        }
        payments {
          id
          paymentGatewayCode
          amount {
        {{MoneyFields}}
          }
          billingAddress {
            id
            key
            addressType
            name
            organization
            firstName
            lastName
            line1
            line2
            city
            countryCode
            countryName
            regionId
            regionName
            postalCode
            zip
            phone
            email
          }
        }
        items {
          id
          productId
          sku
          name
          imageUrl
          thumbnailImageUrl
          quantity
          placedPrice {
        {{MoneyFields}}
          }
          listPrice {
        {{MoneyFields}}
          }
          extendedPrice {
        {{MoneyFields}}
          }
          discountTotal {
        {{MoneyFields}}
          }
          taxTotal {
        {{MoneyFields}}
          }
          validationErrors {
            errorCode
            errorMessage
          }
        }
        validationErrors {
          errorCode
          errorMessage
        }
        warnings {
          errorCode
          errorMessage
        }
        """;

    protected static readonly string GetCartQuery = $$"""
        query UcpGetCart(
          $cartId: String,
          $storeId: String!,
          $currencyCode: String!,
          $cartType: String,
          $cartName: String,
          $userId: String,
          $cultureName: String
        ) {
          cart(
            cartId: $cartId,
            storeId: $storeId,
            currencyCode: $currencyCode,
            cartType: $cartType,
            cartName: $cartName,
            userId: $userId,
            cultureName: $cultureName
          ) {
        {{CartFields}}
          }
        }
        """;

    protected static readonly string ListCartsQuery = $$"""
        query UcpListCarts(
          $storeId: String,
          $userId: String,
          $currencyCode: String,
          $cultureName: String,
          $cartType: String,
          $first: Int,
          $after: String,
          $sort: String
        ) {
          carts(
            storeId: $storeId,
            userId: $userId,
            currencyCode: $currencyCode,
            cultureName: $cultureName,
            cartType: $cartType,
            first: $first,
            after: $after,
            sort: $sort
          ) {
            totalCount
            pageInfo {
              hasNextPage
              endCursor
            }
            items {
        {{CartFields}}
            }
          }
        }
        """;
}
