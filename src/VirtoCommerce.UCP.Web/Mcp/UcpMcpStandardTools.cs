using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using VirtoCommerce.MCP.Core.Attributes;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Models;
using VirtoCommerce.UCP.Core.Services;
using VirtoCommerce.UCP.Web.Mcp.Models;
using VirtoCommerce.UCP.Web.Mcp.Models.Standard;

namespace VirtoCommerce.UCP.Web.Mcp;

/// <summary>
/// The tools of the standard UCP MCP binding (service dev.ucp.shopping, 2026-08-25) that do not share a name with our
/// own tools, and the standard branch of the six that do (get_product, create_cart, get_cart, update_cart,
/// create_checkout, update_checkout, in <see cref="UcpMcpCommerceTools"/>). A standard call returns the standard payload
/// in structuredContent and the same JSON in content; a UCP error returns the standard error_response with isError.
/// Buyer identity is resolved as for our tools: from the Platform token, or the anonymous buyer_id the caller passes.
/// </summary>
[McpServerToolType]
[McpScope(ModuleConstants.McpScopeName)]
[AllowAnonymous]
public static class UcpMcpStandardTools
{
    public const int MaxLookupIds = 20;

    private const string MetaDescription =
        "UCP request metadata: {\"ucp-agent\": {\"profile\": \"<agent profile URL>\"}, \"idempotency-key\": \"<uuid>\"}. " +
        "Accepted and not validated: the agent profile is not fetched.";

    private const string StoreIdDescription = "Not in the UCP standard: the store id; the default store when omitted.";

    private const string BuyerIdDescription =
        "Not in the UCP standard: the buyer_id the standard cart or checkout returned. An anonymous buyer passes it to continue; " +
        "a signed-in buyer comes from the Platform token.";

    private const string IdentityLinkingNote =
        " For the user's account or organization, do not call this tool until link_buyer_identity succeeds.";

    [McpServerTool(Name = ModuleConstants.McpTools.SearchCatalog, ReadOnly = true, Destructive = false)]
    [Description(
        "Standard UCP catalog search (dev.ucp.shopping.catalog.search) in this Virto Commerce storefront: catalog.query, " +
        "catalog.filters.price (minor units) and catalog.filters.categories, catalog.pagination (cursor, limit), catalog.context " +
        "(currency, language, address_country). Without a Platform bearer token the search is anonymous.")]
    public static Task<object> SearchCatalog(
        IUcpProfileService profileService,
        IUcpCatalogService catalogService,
        [Description("Standard UCP search request.")] UcpStandardCatalogSearchRequest catalog = null,
        [Description(MetaDescription)] UcpStandardMeta meta = null,
        [Description(StoreIdDescription)] string store_id = null,
        UcpStandardMapper standardMapper = null,
        CancellationToken cancellationToken = default)
    {
        var call = new UcpMcpStandardCall(profileService, standardMapper, cancellationToken);

        return call.Execute(ModuleConstants.Discovery.CatalogSearch, async () =>
        {
            var request = CreateSearchRequest(catalog, store_id);
            UcpMcpCommerceTools.ApplyCatalogDefaults(request, await call.GetProfile());
            var response = await catalogService.SearchProducts(request, cancellationToken);

            return call.Mapper.MapSearchResponse(response, request.Currency);
        });
    }

    [McpServerTool(Name = ModuleConstants.McpTools.LookupCatalog, ReadOnly = true, Destructive = false)]
    [Description(
        "Standard UCP catalog lookup (dev.ucp.shopping.catalog.lookup) in this Virto Commerce storefront: catalog.ids lists " +
        "up to 20 product or variant ids. Each returned variant names the ids that resolved to it in inputs; an id that does " +
        "not resolve is an info message with code not_found.")]
    public static Task<object> LookupCatalog(
        IUcpProfileService profileService,
        IUcpCatalogService catalogService,
        [Description("Standard UCP lookup request: {\"ids\": [\"<product or variant id>\"], \"context\": {...}}.")] UcpStandardCatalogLookupRequest catalog = null,
        [Description(MetaDescription)] UcpStandardMeta meta = null,
        [Description(StoreIdDescription)] string store_id = null,
        UcpStandardMapper standardMapper = null,
        CancellationToken cancellationToken = default)
    {
        var call = new UcpMcpStandardCall(profileService, standardMapper, cancellationToken);

        return call.Execute(ModuleConstants.Discovery.CatalogLookup, async () =>
        {
            var ids = (catalog?.Ids ?? [])
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (ids.Count == 0)
            {
                throw new UcpException(ModuleConstants.ErrorCodes.InvalidRequest, "catalog.ids must contain at least one id.");
            }

            if (ids.Count > MaxLookupIds)
            {
                throw new UcpException(ModuleConstants.ErrorCodes.InvalidRequest, $"catalog.ids accepts at most {MaxLookupIds} ids.");
            }

            var request = CreateCatalogRequest(catalog.Context, new UcpCatalogSearchRequest { StoreId = store_id });
            UcpMcpCommerceTools.ApplyCatalogDefaults(request, await call.GetProfile());

            var resolved = new List<KeyValuePair<string, UcpProductResponse>>();
            var notFound = new List<string>();
            foreach (var id in ids)
            {
                try
                {
                    resolved.Add(new KeyValuePair<string, UcpProductResponse>(id, await catalogService.GetProduct(id, request, cancellationToken)));
                }
                catch (UcpException exception) when (exception.Code == ModuleConstants.ErrorCodes.ProductNotFound)
                {
                    notFound.Add(id);
                }
            }

            return call.Mapper.MapLookupResponse(resolved, notFound, request.Currency);
        });
    }

    [SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "Each parameter is an argument of the MCP tool's input schema.")]
    [McpServerTool(Name = ModuleConstants.McpTools.CancelCart, ReadOnly = false, Destructive = true)]
    [Description(
        "Standard UCP cancel_cart (dev.ucp.shopping.cart) in this Virto Commerce storefront: removes every line of an anonymous " +
        "buyer's cart and returns the cart as it was before. The cart of a signed-in buyer is their storefront cart, so it is " +
        "not cleared: the call returns an error, and update_cart removes lines instead.")]
    public static Task<object> CancelCart(
        IUcpProfileService profileService,
        IUcpCartService cartService,
        IUcpBuyerContextAccessor buyerContextAccessor,
        [Description("The cart id.")] string id = null,
        [Description(MetaDescription)] UcpStandardMeta meta = null,
        [Description(StoreIdDescription)] string store_id = null,
        [Description(BuyerIdDescription)] string buyer_id = null,
        UcpStandardMapper standardMapper = null,
        CancellationToken cancellationToken = default)
    {
        var call = new UcpMcpStandardCall(profileService, standardMapper, cancellationToken);

        return call.Execute(ModuleConstants.Discovery.Cart, async () =>
        {
            RequireId(id);
            var buyerContext = buyerContextAccessor.Resolve(new UcpBuyerContextRequest { RequestedBuyerIds = [buyer_id] });
            if (buyerContext.IsAuthenticated)
            {
                throw new UcpException(
                    ModuleConstants.ErrorCodes.InvalidRequest,
                    "cancel_cart does not clear a signed-in buyer's cart: it is the buyer's storefront cart. Remove lines with update_cart instead.",
                    StatusCodes.Status405MethodNotAllowed);
            }

            var arguments = new CartToolArguments { StoreId = store_id, BuyerId = buyer_id };
            var profile = await call.GetProfile();
            var request = UcpMcpCommerceTools.CreateCartRequest(arguments);
            UcpMcpCommerceTools.ApplyCartDefaults(request, profile);
            var current = await cartService.GetCart(id, request, cancellationToken);

            var clearRequest = UcpMcpCommerceTools.CreateCartRequest(arguments);
            UcpMcpCommerceTools.ApplyCartDefaults(clearRequest, profile);
            await cartService.UpdateCart(id, clearRequest, cancellationToken);

            return call.Mapper.MapCart(current, request.Currency);
        });
    }

    [SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "Each parameter is an argument of the MCP tool's input schema.")]
    [McpServerTool(Name = ModuleConstants.McpTools.GetCheckout, ReadOnly = true, Destructive = false)]
    [Description(
        "Standard UCP get_checkout (dev.ucp.shopping.checkout) in this Virto Commerce storefront. The checkout id is the cart id. " +
        "status is ready_for_complete when the hosted checkout has what it needs, otherwise incomplete (see messages)." + IdentityLinkingNote)]
    public static Task<object> GetCheckout(
        IUcpProfileService profileService,
        IUcpCheckoutService checkoutService,
        [Description("The checkout id.")] string id = null,
        [Description(MetaDescription)] UcpStandardMeta meta = null,
        [Description(StoreIdDescription)] string store_id = null,
        [Description(BuyerIdDescription)] string buyer_id = null,
        UcpStandardMapper standardMapper = null,
        CancellationToken cancellationToken = default)
    {
        var call = new UcpMcpStandardCall(profileService, standardMapper, cancellationToken);

        return call.Execute(ModuleConstants.Discovery.Checkout, async () =>
        {
            RequireId(id);
            var profile = await call.GetProfile();
            var request = UcpMcpCommerceTools.CreateCheckoutRequest(new CheckoutToolArguments { CartId = id, StoreId = store_id, BuyerId = buyer_id });
            UcpMcpCommerceTools.ApplyCheckoutDefaults(request, profile);

            // Without addresses, the checkout service reads the cart and changes nothing.
            var response = await checkoutService.UpdateCheckout(id, request, cancellationToken);

            return call.Mapper.MapCheckout(response, profile?.StoreManagedAddresses == true, request.Currency);
        });
    }

    [SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "Each parameter is an argument of the MCP tool's input schema.")]
    [McpServerTool(Name = ModuleConstants.McpTools.CompleteCheckout, ReadOnly = false, Destructive = false)]
    [Description(
        "Standard UCP complete_checkout (dev.ucp.shopping.checkout) in this Virto Commerce storefront. It does not take payment " +
        "or place the order: it creates the hosted checkout link and returns status requires_escalation with continue_url, " +
        "where the buyer pays and places the order. Give the buyer continue_url; it is short-lived and works once. " +
        "The checkout needs a shipping address first (update_checkout), unless the store assigns addresses." + IdentityLinkingNote)]
    public static Task<object> CompleteCheckout(
        IUcpProfileService profileService,
        IUcpCheckoutService checkoutService,
        [Description("The checkout id.")] string id = null,
        [Description("Standard UCP checkout of complete_checkout (payment). Accepted and not used: payment is taken in the storefront.")] UcpStandardCheckoutRequest checkout = null,
        [Description(MetaDescription)] UcpStandardMeta meta = null,
        [Description(StoreIdDescription)] string store_id = null,
        [Description(BuyerIdDescription)] string buyer_id = null,
        UcpStandardMapper standardMapper = null,
        CancellationToken cancellationToken = default)
    {
        var call = new UcpMcpStandardCall(profileService, standardMapper, cancellationToken);

        return call.Execute(ModuleConstants.Discovery.Checkout, async () =>
        {
            RequireId(id);
            var profile = await call.GetProfile();
            var request = UcpMcpCommerceTools.CreateCheckoutRequest(new CheckoutToolArguments { CartId = id, StoreId = store_id, BuyerId = buyer_id });
            UcpMcpCommerceTools.ApplyCheckoutDefaults(request, profile);

            var handoff = await checkoutService.HandoffCheckout(id, request, cancellationToken);

            return call.Mapper.MapCheckout(handoff, profile?.StoreManagedAddresses == true, request.Currency);
        });
    }

    [McpServerTool(Name = ModuleConstants.McpTools.CancelCheckout, ReadOnly = true, Destructive = false)]
    [Description(
        "Standard UCP cancel_checkout (dev.ucp.shopping.checkout). Not supported by this Virto Commerce storefront: a checkout " +
        "here is the cart, and it returns an error. A hosted checkout link expires by itself; cancel_cart or update_cart changes the cart.")]
    public static Task<object> CancelCheckout(
        [Description("The checkout id.")] string id = null,
        [Description(MetaDescription)] UcpStandardMeta meta = null,
        UcpStandardMapper standardMapper = null,
        CancellationToken cancellationToken = default)
    {
        var call = new UcpMcpStandardCall(null, standardMapper, cancellationToken);

        return call.Execute(ModuleConstants.Discovery.Checkout, () => throw new UcpException(
            ModuleConstants.ErrorCodes.InvalidRequest,
            "cancel_checkout is not supported: the checkout is the buyer's cart and a hosted checkout link expires by itself. " +
            "Use cancel_cart or update_cart to change the cart.",
            StatusCodes.Status405MethodNotAllowed));
    }

    [SuppressMessage("Major Code Smell", "S107:Methods should not have too many parameters", Justification = "Each parameter is an argument of the MCP tool's input schema.")]
    [McpServerTool(Name = ModuleConstants.McpTools.GetOrder, ReadOnly = true, Destructive = false)]
    [Description(
        "Standard UCP get_order (dev.ucp.shopping.order) in this Virto Commerce storefront: the order by its id or number, " +
        "with the same buyer checks as track_order. checkout_id is the cart id the order was placed from." + IdentityLinkingNote)]
    public static Task<object> GetOrder(
        IUcpProfileService profileService,
        IUcpOrderService orderService,
        [Description("The order id or order number.")] string id = null,
        [Description(MetaDescription)] UcpStandardMeta meta = null,
        [Description(StoreIdDescription)] string store_id = null,
        [Description(BuyerIdDescription)] string buyer_id = null,
        string language = null,
        UcpStandardMapper standardMapper = null,
        CancellationToken cancellationToken = default)
    {
        var call = new UcpMcpStandardCall(profileService, standardMapper, cancellationToken);

        return call.Execute(ModuleConstants.Discovery.Order, async () =>
        {
            RequireId(id);
            var profile = await call.GetProfile();
            var request = new UcpOrderTrackingRequest
            {
                OrderId = id,
                Context = UcpMcpCommerceTools.CreateCartContext(store_id, null, language, buyer_id, null, null),
            };
            UcpMcpCommerceTools.ApplyCartContextDefaults(request.Context, profile);

            var response = await orderService.TrackOrder(request, cancellationToken);
            var order = response?.Order ?? throw new UcpException(
                ModuleConstants.ErrorCodes.OrderNotFound, $"Order '{id}' was not found.", StatusCodes.Status404NotFound);

            return call.Mapper.MapOrder(order, GetStorefrontOrigin(profile, order.StoreId));
        });
    }

    /// <summary>
    /// Whether a call of a tool our tools share with the standard is a standard call: it has the standard wrapper argument
    /// (catalog, cart or checkout) or meta, or, for a tool of an existing resource, the standard id without our own ids.
    /// </summary>
    internal static bool IsStandardCall(object wrapper, UcpStandardMeta meta, string id = null, params string[] ownIds)
    {
        return wrapper != null
            || meta != null
            || !string.IsNullOrWhiteSpace(id) && (ownIds ?? []).All(string.IsNullOrWhiteSpace);
    }

    internal static Task<object> GetProduct(
        UcpMcpStandardCall call,
        IUcpCatalogService catalogService,
        UcpStandardGetProductRequest catalog,
        string fallbackId,
        UcpCatalogSearchRequest flatRequest)
    {
        return call.Execute(ModuleConstants.Discovery.CatalogLookup, async () =>
        {
            var productId = UcpMcpCommerceTools.FirstNotEmpty(catalog?.Id, fallbackId);
            if (string.IsNullOrWhiteSpace(productId))
            {
                throw new UcpException(ModuleConstants.ErrorCodes.InvalidRequest, "catalog.id is required.");
            }

            var request = CreateCatalogRequest(catalog?.Context, flatRequest);
            UcpMcpCommerceTools.ApplyCatalogDefaults(request, await call.GetProfile());
            var response = await catalogService.GetProduct(productId.Trim(), request, call.CancellationToken);

            return call.Mapper.MapGetProductResponse(response, request.Currency);
        });
    }

    internal static Task<object> CreateCart(
        UcpMcpStandardCall call,
        IUcpCartService cartService,
        CartToolArguments arguments,
        UcpStandardCartRequest cart)
    {
        return call.Execute(ModuleConstants.Discovery.Cart, async () =>
        {
            var request = CreateCartRequest(call, arguments, cart, keepLineItemIds: false);
            UcpMcpCommerceTools.ApplyCartDefaults(request, await call.GetProfile());
            var response = await cartService.CreateCart(request, call.CancellationToken);

            return call.Mapper.MapCart(response, request.Currency);
        });
    }

    internal static Task<object> GetCart(
        UcpMcpStandardCall call,
        IUcpCartService cartService,
        string cartId,
        CartToolArguments arguments)
    {
        return call.Execute(ModuleConstants.Discovery.Cart, async () =>
        {
            RequireId(cartId);
            var request = UcpMcpCommerceTools.CreateCartRequest(arguments);
            UcpMcpCommerceTools.ApplyCartDefaults(request, await call.GetProfile());
            var response = await cartService.GetCart(cartId, request, call.CancellationToken);

            return call.Mapper.MapCart(response, request.Currency);
        });
    }

    internal static Task<object> UpdateCart(
        UcpMcpStandardCall call,
        IUcpCartService cartService,
        string cartId,
        CartToolArguments arguments,
        UcpStandardCartRequest cart)
    {
        return call.Execute(ModuleConstants.Discovery.Cart, async () =>
        {
            RequireId(cartId);
            if (cart?.LineItems == null && arguments.LineItems == null)
            {
                // The line items replace the cart's: without them the call would empty the cart.
                throw new UcpException(ModuleConstants.ErrorCodes.InvalidRequest, "cart.line_items is required: send the complete desired line items.");
            }

            var request = CreateCartRequest(call, arguments, cart, keepLineItemIds: true);
            UcpMcpCommerceTools.ApplyCartDefaults(request, await call.GetProfile());
            var response = await cartService.UpdateCart(cartId, request, call.CancellationToken);

            return call.Mapper.MapCart(response, request.Currency);
        });
    }

    /// <summary>
    /// The standard create_checkout: with checkout.cart_id (or cart_id) the checkout of that cart, whose contents win over
    /// the checkout's line_items, context and buyer; otherwise a new cart of checkout.line_items, then its checkout.
    /// </summary>
    internal static Task<object> CreateCheckout(
        UcpMcpStandardCall call,
        IUcpCartService cartService,
        IUcpCheckoutService checkoutService,
        CheckoutToolArguments arguments,
        UcpStandardCheckoutRequest checkout)
    {
        return call.Execute(ModuleConstants.Discovery.Checkout, async () =>
        {
            var profile = await call.GetProfile();
            var cartId = UcpMcpCommerceTools.FirstNotEmpty(checkout?.CartId, arguments.CartId);
            var buyerId = arguments.BuyerId;
            if (string.IsNullOrWhiteSpace(cartId))
            {
                var cart = await CreateCheckoutCart(call, cartService, arguments, checkout, profile);
                cartId = cart?.Id;
                // A new anonymous cart has a new anonymous buyer: its checkout continues as that buyer.
                buyerId = UcpMcpCommerceTools.FirstNotEmpty(buyerId, cart?.BuyerId);
            }

            var request = CreateCheckoutRequest(call, arguments, checkout, cartId, buyerId, useCheckoutBuyer: string.IsNullOrWhiteSpace(checkout?.CartId));
            UcpMcpCommerceTools.ApplyCheckoutDefaults(request, profile);
            var response = await checkoutService.CreateCheckout(request, call.CancellationToken);

            return call.Mapper.MapCheckout(response, profile?.StoreManagedAddresses == true, request.Currency);
        });
    }

    /// <summary>
    /// The standard update_checkout: checkout.line_items (when not empty) replace the cart's lines, then the buyer and
    /// the shipping destination are applied as by our update_checkout.
    /// </summary>
    internal static Task<object> UpdateCheckout(
        UcpMcpStandardCall call,
        IUcpCartService cartService,
        IUcpCheckoutService checkoutService,
        string checkoutId,
        CheckoutToolArguments arguments,
        UcpStandardCheckoutRequest checkout)
    {
        return call.Execute(ModuleConstants.Discovery.Checkout, async () =>
        {
            RequireId(checkoutId);
            var profile = await call.GetProfile();
            if (checkout?.LineItems?.Count > 0)
            {
                var cartArguments = CreateCartArguments(arguments, checkout);
                var cartRequest = CreateCartRequest(call, cartArguments, new UcpStandardCartRequest { LineItems = checkout.LineItems }, keepLineItemIds: true);
                UcpMcpCommerceTools.ApplyCartDefaults(cartRequest, profile);
                var cartResponse = await GetCartService(cartService).UpdateCart(checkoutId, cartRequest, call.CancellationToken);
                // Merging an anonymous cart into a signed-in buyer's cart changes the cart id.
                checkoutId = UcpMcpCommerceTools.FirstNotEmpty(cartResponse?.Cart?.Id, checkoutId);
            }

            var request = CreateCheckoutRequest(call, arguments, checkout, checkoutId, arguments.BuyerId, useCheckoutBuyer: true);
            UcpMcpCommerceTools.ApplyCheckoutDefaults(request, profile);
            var response = await checkoutService.UpdateCheckout(checkoutId, request, call.CancellationToken);

            return call.Mapper.MapCheckout(response, profile?.StoreManagedAddresses == true, request.Currency);
        });
    }

    private static async Task<UcpCart> CreateCheckoutCart(
        UcpMcpStandardCall call,
        IUcpCartService cartService,
        CheckoutToolArguments arguments,
        UcpStandardCheckoutRequest checkout,
        UcpProfile profile)
    {
        if (checkout?.LineItems == null || checkout.LineItems.Count == 0)
        {
            throw new UcpException(ModuleConstants.ErrorCodes.InvalidRequest, "checkout.line_items or checkout.cart_id is required.");
        }

        var cartArguments = CreateCartArguments(arguments, checkout);
        var cartRequest = CreateCartRequest(call, cartArguments, new UcpStandardCartRequest { LineItems = checkout.LineItems }, keepLineItemIds: false);
        UcpMcpCommerceTools.ApplyCartDefaults(cartRequest, profile);
        var cartResponse = await GetCartService(cartService).CreateCart(cartRequest, call.CancellationToken);

        return cartResponse?.Cart;
    }

    private static CartToolArguments CreateCartArguments(CheckoutToolArguments arguments, UcpStandardCheckoutRequest checkout)
    {
        return new CartToolArguments
        {
            StoreId = arguments.StoreId,
            Currency = UcpMcpCommerceTools.FirstNotEmpty(checkout?.Context?.Currency, arguments.Currency),
            Language = UcpMcpCommerceTools.FirstNotEmpty(checkout?.Context?.Language, arguments.Language),
            BuyerId = arguments.BuyerId,
            OrganizationId = arguments.OrganizationId,
        };
    }

    private static UcpCartRequest CreateCartRequest(UcpMcpStandardCall call, CartToolArguments arguments, UcpStandardCartRequest cart, bool keepLineItemIds)
    {
        var request = UcpMcpCommerceTools.CreateCartRequest(new CartToolArguments
        {
            StoreId = arguments.StoreId,
            Currency = UcpMcpCommerceTools.FirstNotEmpty(cart?.Context?.Currency, arguments.Currency),
            Language = UcpMcpCommerceTools.FirstNotEmpty(cart?.Context?.Language, arguments.Language),
            BuyerId = arguments.BuyerId,
            OrganizationId = arguments.OrganizationId,
            CartName = arguments.CartName,
            CartType = arguments.CartType,
            LineItems = call.Mapper.MapLineItemRequests(cart?.LineItems, keepLineItemIds) ?? arguments.LineItems,
            Coupons = arguments.Coupons,
        });
        request.Context.AddressCountry = cart?.Context?.AddressCountry;
        request.Context.AddressRegion = cart?.Context?.AddressRegion;

        return request;
    }

    private static UcpCheckoutRequest CreateCheckoutRequest(
        UcpMcpStandardCall call,
        CheckoutToolArguments arguments,
        UcpStandardCheckoutRequest checkout,
        string cartId,
        string buyerId,
        bool useCheckoutBuyer)
    {
        var buyer = useCheckoutBuyer ? call.Mapper.MapBuyerRequest(checkout?.Buyer) : null;

        return UcpMcpCommerceTools.CreateCheckoutRequest(new CheckoutToolArguments
        {
            CartId = cartId,
            StoreId = arguments.StoreId,
            Currency = UcpMcpCommerceTools.FirstNotEmpty(checkout?.Context?.Currency, arguments.Currency),
            Language = UcpMcpCommerceTools.FirstNotEmpty(checkout?.Context?.Language, arguments.Language),
            BuyerId = buyerId,
            OrganizationId = arguments.OrganizationId,
            Buyer = buyer ?? arguments.Buyer,
            BuyerEmail = arguments.BuyerEmail,
            BuyerName = arguments.BuyerName,
            BuyerPhone = arguments.BuyerPhone,
            ShippingAddress = call.Mapper.MapShippingAddressRequest(checkout?.Fulfillment) ?? arguments.ShippingAddress,
            BillingAddress = arguments.BillingAddress,
            PaymentHandler = arguments.PaymentHandler,
            Notes = arguments.Notes,
        });
    }

    private static UcpCatalogSearchRequest CreateSearchRequest(UcpStandardCatalogSearchRequest catalog, string storeId)
    {
        var limit = catalog?.Pagination?.Limit is > 0 ? catalog.Pagination.Limit : null;
        var request = CreateCatalogRequest(catalog?.Context, new UcpCatalogSearchRequest { StoreId = storeId });
        request.Query = catalog?.Query;
        request.Limit = limit;
        request.Pagination = new UcpPaginationRequest { Cursor = catalog?.Pagination?.Cursor, Limit = limit };
        request.Filters = CreateFilters(catalog?.Filters);

        return request;
    }

    private static UcpCatalogSearchRequest CreateCatalogRequest(UcpStandardContext context, UcpCatalogSearchRequest flatRequest)
    {
        var currency = UcpMcpCommerceTools.FirstNotEmpty(context?.Currency, flatRequest?.Currency);
        var language = UcpMcpCommerceTools.FirstNotEmpty(context?.Language, flatRequest?.Language);

        return new UcpCatalogSearchRequest
        {
            StoreId = flatRequest?.StoreId,
            Currency = currency,
            Language = language,
            Context = new UcpCatalogContext
            {
                StoreId = flatRequest?.StoreId,
                Currency = currency,
                Language = language,
                AddressCountry = context?.AddressCountry,
                AddressRegion = context?.AddressRegion,
                Intent = context?.Intent,
            },
        };
    }

    private static UcpSearchFilters CreateFilters(UcpStandardSearchFilters filters)
    {
        var categories = (filters?.Categories ?? []).Where(category => !string.IsNullOrWhiteSpace(category)).ToList();
        var price = filters?.Price is { Min: not null } or { Max: not null }
            ? new UcpPriceFilter { Min = filters.Price.Min, Max = filters.Price.Max }
            : null;

        return categories.Count == 0 && price == null
            ? null
            : new UcpSearchFilters { Categories = categories, Price = price };
    }

    private static string GetStorefrontOrigin(UcpProfile profile, string storeId)
    {
        var store = UcpMcpCommerceTools.GetStore(profile, storeId);

        return UcpMcpCommerceTools.FirstNotEmpty(store?.SecureUrl, store?.Url, profile?.StorefrontOrigin);
    }

    private static IUcpCartService GetCartService(IUcpCartService cartService)
    {
        return cartService ?? throw new InvalidOperationException($"{nameof(IUcpCartService)} is not registered.");
    }

    private static void RequireId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new UcpException(ModuleConstants.ErrorCodes.InvalidRequest, "id is required.");
        }
    }
}
