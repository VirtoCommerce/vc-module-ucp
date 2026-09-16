using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Models;
using VirtoCommerce.UCP.Core.Services;
using VirtoCommerce.UCP.Web.Mcp.Models;

namespace VirtoCommerce.UCP.Web.Mcp;

[McpServerToolType]
public static class UcpMcpCommerceTools
{
    [McpServerTool(Name = ModuleConstants.McpTools.GetStoreCapabilities, ReadOnly = true, Destructive = false)]
    [Description("Discover UCP capabilities for this Virto Commerce storefront.")]
    public static Task<object> GetStoreCapabilities(
        IUcpProfileService profileService,
        CancellationToken cancellationToken = default)
    {
        return Execute(() => profileService.GetProfile(cancellationToken));
    }

    [McpServerTool(Name = ModuleConstants.McpTools.SearchProducts, ReadOnly = true, Destructive = false)]
    [Description(
        "Search products in this Virto Commerce storefront. Without a Platform bearer token this search is anonymous. " +
        "If the user asks to use their account, act on their behalf, or use their organization or personalized prices, " +
        "do not call this tool until link_buyer_identity succeeds; then call this tool with the same search arguments.")]
    public static Task<object> SearchProducts(
        IUcpProfileService profileService,
        IUcpCatalogService catalogService,
        [Description("Buyer search text.")] string query,
        string store_id = null,
        string currency = null,
        string language = null,
        [Description("Inclusive minimum sell price in minor currency units; for example, 20000 means $200.00 for USD.")] long? price_min = null,
        [Description("Inclusive maximum sell price in minor currency units; for example, 15000 means $150.00 for USD.")] long? price_max = null,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        return Execute(async () =>
        {
            var request = new UcpCatalogSearchRequest
            {
                Query = query,
                StoreId = store_id,
                Currency = currency,
                Language = language,
                Limit = limit,
                Filters = price_min.HasValue || price_max.HasValue
                    ? new UcpSearchFilters
                    {
                        Price = new UcpPriceFilter
                        {
                            Min = price_min,
                            Max = price_max,
                        },
                    }
                    : null,
                Pagination = new UcpPaginationRequest { Limit = limit },
                Context = new UcpCatalogContext
                {
                    StoreId = store_id,
                    Currency = currency,
                    Language = language,
                },
            };
            ApplyCatalogDefaults(request, await profileService.GetProfile(cancellationToken));

            return await catalogService.SearchProducts(request, cancellationToken);
        });
    }

    [McpServerTool(Name = ModuleConstants.McpTools.GetProduct, ReadOnly = true, Destructive = false)]
    [Description("Get one product in this Virto Commerce storefront. Without a Platform bearer token this lookup is anonymous. If the user asks to use their account, act on their behalf, or use their organization or personalized prices, do not call this tool until link_buyer_identity succeeds.")]
    public static Task<object> GetProduct(
        IUcpProfileService profileService,
        IUcpCatalogService catalogService,
        [Description("Stable UCP product id returned by search_products.")] string id = null,
        [Description("Alias for id.")] string product_id = null,
        string store_id = null,
        string currency = null,
        string language = null,
        CancellationToken cancellationToken = default)
    {
        return Execute<object>(async () =>
        {
            var productId = FirstNotEmpty(product_id, id);
            if (string.IsNullOrWhiteSpace(productId))
            {
                throw new UcpException(ModuleConstants.ErrorCodes.InvalidRequest, "id is required.");
            }

            var request = new UcpCatalogSearchRequest
            {
                StoreId = store_id,
                Currency = currency,
                Language = language,
                Context = new UcpCatalogContext
                {
                    StoreId = store_id,
                    Currency = currency,
                    Language = language,
                },
            };
            ApplyCatalogDefaults(request, await profileService.GetProfile(cancellationToken));

            return await catalogService.GetProduct(productId, request, cancellationToken);
        });
    }

    [McpServerTool(Name = ModuleConstants.McpTools.CreateCart, ReadOnly = false, Destructive = false)]
    [Description(
        "Create a cart in this Virto Commerce storefront. Without a Platform bearer token this creates an anonymous cart. " +
        "If the user asks for 'my cart', to act on their behalf, or to use their account or organization, do not call this tool " +
        "until link_buyer_identity succeeds. Buyer and organization then come only from the validated Platform token; never invent them.")]
    public static Task<object> CreateCart(
        IUcpProfileService profileService,
        IUcpCartService cartService,
        [Description("Cart line items to add.")] IList<UcpCartLineItemRequest> line_items,
        string store_id = null,
        string currency = null,
        string language = null,
        [Description("Stable buyer identifier. Preserve and reuse the cart.buyer_id returned by this tool for later cart and checkout calls.")] string buyer_id = null,
        [Description("Stable cart name used together with buyer_id and store_id.")] string cart_name = null,
        string cart_type = null,
        IList<string> coupons = null,
        CancellationToken cancellationToken = default)
    {
        return Execute(async () =>
        {
            var request = CreateCartRequest(new CartToolArguments
            {
                StoreId = store_id,
                Currency = currency,
                Language = language,
                BuyerId = buyer_id,
                CartName = cart_name,
                CartType = cart_type,
                LineItems = line_items,
                Coupons = coupons,
            });
            ApplyCartDefaults(request, await profileService.GetProfile(cancellationToken));

            return await cartService.CreateCart(request, cancellationToken);
        });
    }

    [McpServerTool(Name = ModuleConstants.McpTools.ListCarts, ReadOnly = true, Destructive = false)]
    [Description(
        "List buyer-scoped carts in this Virto Commerce storefront. Anonymous continuation requires buyer_id. " +
        "For the user's account, organization, or saved carts, do not call this tool until link_buyer_identity succeeds; " +
        "buyer and organization then come from the Platform token. Global cart listing is not allowed.")]
    [SuppressMessage("Maintainability", "S107", Justification = "Parameters define the public MCP tool schema.")]
    public static Task<object> ListCarts(
        IUcpProfileService profileService,
        IUcpCartService cartService,
        string store_id = null,
        string currency = null,
        string language = null,
        [Description("Required for anonymous continuation; authenticated mode derives buyer identity from the Platform token.")] string buyer_id = null,
        string cart_name = null,
        string cart_type = null,
        string cursor = null,
        int limit = 10,
        string sort = null,
        CancellationToken cancellationToken = default)
    {
        return Execute(async () =>
        {
            var request = new UcpCartListRequest
            {
                Pagination = new UcpPaginationRequest { Cursor = cursor, Limit = limit },
                Sort = sort,
                Context = CreateCartContext(store_id, currency, language, buyer_id, cart_name, cart_type),
            };
            ApplyCartContextDefaults(request.Context, await profileService.GetProfile(cancellationToken));

            return await cartService.ListCarts(request, cancellationToken);
        });
    }

    [McpServerTool(Name = ModuleConstants.McpTools.GetCart, ReadOnly = true, Destructive = false)]
    [Description("Get a cart in this Virto Commerce storefront. For the user's account, organization, or saved cart, do not call this tool until link_buyer_identity succeeds; authenticated buyer identity comes from the Platform token.")]
    public static Task<object> GetCart(
        IUcpProfileService profileService,
        IUcpCartService cartService,
        string cart_id,
        string store_id = null,
        string currency = null,
        string language = null,
        string buyer_id = null,
        CancellationToken cancellationToken = default)
    {
        return Execute(async () =>
        {
            var request = CreateCartRequest(new CartToolArguments
            {
                StoreId = store_id,
                Currency = currency,
                Language = language,
                BuyerId = buyer_id,
            });
            ApplyCartDefaults(request, await profileService.GetProfile(cancellationToken));

            return await cartService.GetCart(cart_id, request, cancellationToken);
        });
    }

    [McpServerTool(Name = ModuleConstants.McpTools.UpdateCart, ReadOnly = false, Destructive = false)]
    [Description("Update cart state in this Virto Commerce storefront. For the user's account, organization, or cart, do not call this tool until link_buyer_identity succeeds; authenticated buyer identity comes from the Platform token.")]
    public static Task<object> UpdateCart(
        IUcpProfileService profileService,
        IUcpCartService cartService,
        string cart_id,
        IList<UcpCartLineItemRequest> line_items,
        string store_id = null,
        string currency = null,
        string language = null,
        string buyer_id = null,
        string cart_name = null,
        string cart_type = null,
        IList<string> coupons = null,
        CancellationToken cancellationToken = default)
    {
        return Execute(async () =>
        {
            var request = CreateCartRequest(new CartToolArguments
            {
                StoreId = store_id,
                Currency = currency,
                Language = language,
                BuyerId = buyer_id,
                CartName = cart_name,
                CartType = cart_type,
                LineItems = line_items,
                Coupons = coupons,
            });
            ApplyCartDefaults(request, await profileService.GetProfile(cancellationToken));

            return await cartService.UpdateCart(cart_id, request, cancellationToken);
        });
    }

    [McpServerTool(Name = ModuleConstants.McpTools.CreateCheckout, ReadOnly = false, Destructive = false)]
    [Description(
        "Create checkout in this Virto Commerce storefront. For the user's account or organization, do not call this tool " +
        "until link_buyer_identity succeeds. For physical goods, do not call until shipping_address.first_name, " +
        "shipping_address.last_name, and shipping_address.postal_code are provided; ask the user for missing values.")]
    public static Task<object> CreateCheckout(
        IUcpProfileService profileService,
        IUcpCheckoutService checkoutService,
        string cart_id,
        string store_id = null,
        string currency = null,
        string language = null,
        string buyer_id = null,
        UcpCheckoutBuyer buyer = null,
        string buyer_email = null,
        string buyer_name = null,
        string buyer_phone = null,
        UcpCheckoutAddress shipping_address = null,
        UcpCheckoutAddress billing_address = null,
        string payment_handler = null,
        string notes = null,
        CancellationToken cancellationToken = default)
    {
        return Execute(async () =>
        {
            var request = CreateCheckoutRequest(new CheckoutToolArguments
            {
                CartId = cart_id,
                StoreId = store_id,
                Currency = currency,
                Language = language,
                BuyerId = buyer_id,
                Buyer = buyer,
                BuyerEmail = buyer_email,
                BuyerName = buyer_name,
                BuyerPhone = buyer_phone,
                ShippingAddress = shipping_address,
                BillingAddress = billing_address,
                PaymentHandler = payment_handler,
                Notes = notes,
            });
            ApplyCheckoutDefaults(request, await profileService.GetProfile(cancellationToken));

            return await checkoutService.CreateCheckout(request, cancellationToken);
        });
    }

    [McpServerTool(Name = ModuleConstants.McpTools.UpdateCheckout, ReadOnly = false, Destructive = false)]
    [Description("Update checkout address or buyer data in this Virto Commerce storefront. For the user's account or organization, do not call this tool until link_buyer_identity succeeds.")]
    public static Task<object> UpdateCheckout(
        IUcpProfileService profileService,
        IUcpCheckoutService checkoutService,
        string checkout_id,
        string cart_id = null,
        string store_id = null,
        string currency = null,
        string language = null,
        string buyer_id = null,
        UcpCheckoutBuyer buyer = null,
        string buyer_email = null,
        string buyer_name = null,
        string buyer_phone = null,
        UcpCheckoutAddress shipping_address = null,
        UcpCheckoutAddress billing_address = null,
        string payment_handler = null,
        string notes = null,
        CancellationToken cancellationToken = default)
    {
        return Execute(async () =>
        {
            var request = CreateCheckoutRequest(new CheckoutToolArguments
            {
                CartId = FirstNotEmpty(cart_id, checkout_id),
                StoreId = store_id,
                Currency = currency,
                Language = language,
                BuyerId = buyer_id,
                Buyer = buyer,
                BuyerEmail = buyer_email,
                BuyerName = buyer_name,
                BuyerPhone = buyer_phone,
                ShippingAddress = shipping_address,
                BillingAddress = billing_address,
                PaymentHandler = payment_handler,
                Notes = notes,
            });
            ApplyCheckoutDefaults(request, await profileService.GetProfile(cancellationToken));

            var checkout = await checkoutService.UpdateCheckout(checkout_id, request, cancellationToken);

            return new UcpMcpUpdateCheckoutResult
            {
                Result = checkout,
                NextStep = new UcpMcpNextToolStep
                {
                    Tool = ModuleConstants.McpTools.HandoffCheckout,
                    Reason = "Address was updated; create a fresh hosted checkout URL with the latest address snapshot.",
                    Arguments = CreateHandoffNextStepArguments(checkout_id, request),
                },
            };
        });
    }

    [McpServerTool(Name = ModuleConstants.McpTools.GetPaymentHandlers, ReadOnly = true, Destructive = false)]
    [Description("Get payment handlers for a checkout in this Virto Commerce storefront. For the user's account or organization, do not call this tool until link_buyer_identity succeeds. This only discovers handlers; it does not execute a payment.")]
    public static Task<object> GetPaymentHandlers(
        IUcpCheckoutService checkoutService,
        string checkout_id,
        CancellationToken cancellationToken = default)
    {
        return Execute(() => checkoutService.GetPaymentHandlers(checkout_id, cancellationToken));
    }

    [McpServerTool(Name = ModuleConstants.McpTools.CheckoutAndHandoff, ReadOnly = false, Destructive = false)]
    [Description(
        "Create checkout and immediately create a hosted checkout handoff URL. For the user's account or organization, " +
        "do not call this tool until link_buyer_identity succeeds. For physical goods, do not call until shipping_address.first_name, " +
        "shipping_address.last_name, and shipping_address.postal_code are provided; ask the user for missing values. " +
        "This does not execute a payment.")]
    public static Task<object> CheckoutAndHandoff(
        IUcpProfileService profileService,
        IUcpCheckoutService checkoutService,
        string cart_id,
        string store_id = null,
        string currency = null,
        string language = null,
        string buyer_id = null,
        UcpCheckoutBuyer buyer = null,
        string buyer_email = null,
        string buyer_name = null,
        string buyer_phone = null,
        UcpCheckoutAddress shipping_address = null,
        UcpCheckoutAddress billing_address = null,
        string payment_handler = null,
        string notes = null,
        CancellationToken cancellationToken = default)
    {
        return Execute(async () =>
        {
            var request = CreateCheckoutRequest(new CheckoutToolArguments
            {
                CartId = cart_id,
                StoreId = store_id,
                Currency = currency,
                Language = language,
                BuyerId = buyer_id,
                Buyer = buyer,
                BuyerEmail = buyer_email,
                BuyerName = buyer_name,
                BuyerPhone = buyer_phone,
                ShippingAddress = shipping_address,
                BillingAddress = billing_address,
                PaymentHandler = payment_handler,
                Notes = notes,
            });
            ApplyCheckoutDefaults(request, await profileService.GetProfile(cancellationToken));

            var checkout = await checkoutService.CreateCheckout(request, cancellationToken);
            var checkoutId = ResolveCheckoutId(checkout, cart_id);
            request.CartId = ResolveCheckoutCartId(request.CartId, checkout, cart_id);

            var handoff = await checkoutService.HandoffCheckout(checkoutId, request, cancellationToken);
            var effectiveBuyerId = ResolveCheckoutBuyerId(checkout, buyer_id);
            var effectiveLanguage = FirstNotEmpty(request.Language, request.Context?.Language);

            return CreateCheckoutAndHandoffResult(request, checkout, handoff, effectiveBuyerId, effectiveLanguage);
        });
    }

    [McpServerTool(Name = ModuleConstants.McpTools.HandoffCheckout, ReadOnly = false, Destructive = false)]
    [Description(
        "Create a hosted checkout handoff URL. For the user's account or organization, do not call this tool until " +
        "link_buyer_identity succeeds. For physical goods, do not call until shipping_address.first_name, " +
        "shipping_address.last_name, and shipping_address.postal_code are provided; ask the user for missing values. " +
        "This does not execute a payment.")]
    public static Task<object> HandoffCheckout(
        IUcpProfileService profileService,
        IUcpCheckoutService checkoutService,
        string checkout_id,
        string cart_id = null,
        string store_id = null,
        string currency = null,
        string language = null,
        string buyer_id = null,
        UcpCheckoutBuyer buyer = null,
        string buyer_email = null,
        string buyer_name = null,
        string buyer_phone = null,
        UcpCheckoutAddress shipping_address = null,
        UcpCheckoutAddress billing_address = null,
        string payment_handler = null,
        string notes = null,
        CancellationToken cancellationToken = default)
    {
        return Execute(async () =>
        {
            var request = CreateCheckoutRequest(new CheckoutToolArguments
            {
                CartId = FirstNotEmpty(cart_id, checkout_id),
                StoreId = store_id,
                Currency = currency,
                Language = language,
                BuyerId = buyer_id,
                Buyer = buyer,
                BuyerEmail = buyer_email,
                BuyerName = buyer_name,
                BuyerPhone = buyer_phone,
                ShippingAddress = shipping_address,
                BillingAddress = billing_address,
                PaymentHandler = payment_handler,
                Notes = notes,
            });
            ApplyCheckoutDefaults(request, await profileService.GetProfile(cancellationToken));

            var handoff = await checkoutService.HandoffCheckout(checkout_id, request, cancellationToken);
            var effectiveBuyerId = FirstNotEmpty(handoff?.Checkout?.Buyer?.Id, handoff?.Checkout?.Cart?.BuyerId, buyer_id);
            var effectiveLanguage = FirstNotEmpty(request.Language, request.Context?.Language);

            return new UcpMcpHandoffCheckoutResult
            {
                Result = handoff,
                LastCheckout = new UcpMcpLastCheckout
                {
                    CartId = request.CartId,
                    BuyerId = effectiveBuyerId,
                    OrganizationId = request.OrganizationId,
                    Language = effectiveLanguage,
                    CheckoutId = checkout_id,
                },
                NextStepAfterPayment = new UcpMcpNextToolStep
                {
                    Tool = ModuleConstants.McpTools.TrackOrder,
                    Arguments = CreateTrackOrderArguments(request.CartId, effectiveBuyerId, effectiveLanguage),
                },
            };
        });
    }

    [McpServerTool(Name = ModuleConstants.McpTools.ListCountries, ReadOnly = true, Destructive = false)]
    [Description("List or search countries in this Virto Commerce storefront.")]
    public static Task<object> ListCountries(
        IUcpGeographyService geographyService,
        string query = null,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        return Execute(() => geographyService.ListCountries(new UcpCountriesQuery { Query = query, Limit = limit }, cancellationToken));
    }

    [McpServerTool(Name = ModuleConstants.McpTools.ResolveCountry, ReadOnly = true, Destructive = false)]
    [Description("Resolve country text to a Virto Commerce platform country id in this storefront.")]
    public static Task<object> ResolveCountry(
        IUcpGeographyService geographyService,
        string query,
        CancellationToken cancellationToken = default)
    {
        return Execute(() => geographyService.ResolveCountry(query, cancellationToken));
    }

    [McpServerTool(Name = ModuleConstants.McpTools.ListRegions, ReadOnly = true, Destructive = false)]
    [Description("List regions for a country in this Virto Commerce storefront.")]
    public static Task<object> ListRegions(
        IUcpGeographyService geographyService,
        string country_id,
        CancellationToken cancellationToken = default)
    {
        return Execute(() => geographyService.ListRegions(country_id, cancellationToken));
    }

    [McpServerTool(Name = ModuleConstants.McpTools.TrackOrder, ReadOnly = true, Destructive = false)]
    [Description("Track order by order id, order number, or cart id in this Virto Commerce storefront. For the user's account, organization, or orders, do not call this tool until link_buyer_identity succeeds.")]
    public static Task<object> TrackOrder(
        IUcpProfileService profileService,
        IUcpOrderService orderService,
        string order_id = null,
        string order_number = null,
        string cart_id = null,
        string store_id = null,
        string currency = null,
        string language = null,
        string buyer_id = null,
        CancellationToken cancellationToken = default)
    {
        return Execute(async () =>
        {
            var request = new UcpOrderTrackingRequest
            {
                OrderId = order_id,
                OrderNumber = order_number,
                CartId = cart_id,
                Context = CreateCartContext(store_id, currency, language, buyer_id, null, null),
            };
            ApplyCartContextDefaults(request.Context, await profileService.GetProfile(cancellationToken));

            return await orderService.TrackOrder(request, cancellationToken);
        });
    }

    private static async Task<object> Execute<T>(Func<Task<T>> action)
    {
        return await action();
    }

    private static UcpCartRequest CreateCartRequest(CartToolArguments arguments)
    {
        return new UcpCartRequest
        {
            StoreId = arguments.StoreId,
            Currency = arguments.Currency,
            Language = arguments.Language,
            BuyerId = arguments.BuyerId,
            OrganizationId = arguments.OrganizationId,
            CartName = arguments.CartName,
            CartType = arguments.CartType,
            LineItems = arguments.LineItems ?? [],
            Coupons = arguments.Coupons ?? [],
            Context = CreateCartContext(
                arguments.StoreId,
                arguments.Currency,
                arguments.Language,
                arguments.BuyerId,
                arguments.CartName,
                arguments.CartType),
        };
    }

    private static UcpCheckoutRequest CreateCheckoutRequest(CheckoutToolArguments arguments)
    {
        var buyer = MergeBuyerHints(
            arguments.Buyer,
            arguments.BuyerId,
            arguments.BuyerEmail,
            arguments.BuyerName,
            arguments.BuyerPhone);

        return new UcpCheckoutRequest
        {
            CartId = arguments.CartId,
            StoreId = arguments.StoreId,
            Currency = arguments.Currency,
            Language = arguments.Language,
            BuyerId = arguments.BuyerId,
            OrganizationId = arguments.OrganizationId,
            Buyer = buyer,
            ShippingAddress = arguments.ShippingAddress,
            BillingAddress = arguments.BillingAddress,
            PaymentHandler = arguments.PaymentHandler,
            Notes = arguments.Notes,
            Context = CreateCartContext(
                arguments.StoreId,
                arguments.Currency,
                arguments.Language,
                arguments.BuyerId,
                null,
                null),
        };
    }

    private static UcpCheckoutBuyer MergeBuyerHints(
        UcpCheckoutBuyer buyer,
        string buyerId,
        string buyerEmail,
        string buyerName,
        string buyerPhone)
    {
        if (buyer == null && !HasBuyerHints(buyerId, buyerEmail, buyerName, buyerPhone))
        {
            return null;
        }

        buyer ??= new UcpCheckoutBuyer();
        buyer.Id = FirstNotEmpty(buyer.Id, buyerId);
        buyer.Email = FirstNotEmpty(buyer.Email, buyerEmail);
        buyer.Name = FirstNotEmpty(buyer.Name, buyerName);
        buyer.Phone = FirstNotEmpty(buyer.Phone, buyerPhone);

        return buyer;
    }

    private static bool HasBuyerHints(string buyerId, string buyerEmail, string buyerName, string buyerPhone)
    {
        return !string.IsNullOrWhiteSpace(buyerId)
            || !string.IsNullOrWhiteSpace(buyerEmail)
            || !string.IsNullOrWhiteSpace(buyerName)
            || !string.IsNullOrWhiteSpace(buyerPhone);
    }

    private static string ResolveCheckoutId(UcpCheckoutResponse checkout, string fallbackCartId)
    {
        return FirstNotEmpty(checkout?.Checkout?.Id, checkout?.Checkout?.CartId, fallbackCartId);
    }

    private static string ResolveCheckoutCartId(string requestCartId, UcpCheckoutResponse checkout, string fallbackCartId)
    {
        return FirstNotEmpty(requestCartId, checkout?.Checkout?.CartId, fallbackCartId);
    }

    private static string ResolveCheckoutBuyerId(UcpCheckoutResponse checkout, string fallbackBuyerId)
    {
        return FirstNotEmpty(checkout?.Checkout?.Buyer?.Id, checkout?.Checkout?.Cart?.BuyerId, fallbackBuyerId);
    }

    private static UcpMcpCheckoutAndHandoffResult CreateCheckoutAndHandoffResult(
        UcpCheckoutRequest request,
        UcpCheckoutResponse checkout,
        UcpCheckoutHandoffResponse handoff,
        string buyerId,
        string language)
    {
        return new UcpMcpCheckoutAndHandoffResult
        {
            Ok = true,
            CartId = request.CartId,
            BuyerId = buyerId,
            Checkout = checkout,
            Handoff = handoff,
            ContinueUrl = handoff?.Checkout?.ContinueUrl,
            NextStepAfterPayment = new UcpMcpNextToolStep
            {
                Tool = ModuleConstants.McpTools.TrackOrder,
                Arguments = CreateTrackOrderArguments(request.CartId, buyerId, language),
            },
        };
    }

    private static Dictionary<string, object> CreateTrackOrderArguments(string cartId, string buyerId, string language)
    {
        var arguments = new Dictionary<string, object>();
        AddNextStepString(arguments, "cart_id", cartId);
        AddNextStepString(arguments, "buyer_id", buyerId);
        AddNextStepString(arguments, "language", language);

        return arguments;
    }

    private static Dictionary<string, object> CreateHandoffNextStepArguments(string checkoutId, UcpCheckoutRequest request)
    {
        var arguments = new Dictionary<string, object>();
        AddNextStepString(arguments, "checkout_id", checkoutId);
        AddNextStepString(arguments, "cart_id", request.CartId);
        AddNextStepString(arguments, "store_id", request.StoreId);
        AddNextStepString(arguments, "currency", request.Currency);
        AddNextStepString(arguments, "language", request.Language);
        AddNextStepString(arguments, "buyer_id", request.BuyerId);
        AddNextStepObject(arguments, "shipping_address", request.ShippingAddress);
        AddNextStepObject(arguments, "billing_address", request.BillingAddress ?? request.ShippingAddress);
        AddNextStepString(arguments, "payment_handler", FirstNotEmpty(request.PaymentHandler, ModuleConstants.PaymentHandlers.HostedCheckout));

        return arguments;
    }

    private static void AddNextStepString(Dictionary<string, object> arguments, string name, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            arguments[name] = value;
        }
    }

    private static void AddNextStepObject(Dictionary<string, object> arguments, string name, object value)
    {
        if (value != null)
        {
            arguments[name] = value;
        }
    }

    private static UcpCartContext CreateCartContext(
        string storeId,
        string currency,
        string language,
        string buyerId,
        string cartName,
        string cartType)
    {
        return new UcpCartContext
        {
            StoreId = storeId,
            Currency = currency,
            Language = language,
            BuyerId = buyerId,
            CartName = cartName,
            CartType = cartType,
        };
    }

    private static void ApplyCatalogDefaults(UcpCatalogSearchRequest request, UcpProfile profile)
    {
        request.Context ??= new UcpCatalogContext();
        var requestedStoreId = FirstNotEmpty(request.Context.StoreId, request.StoreId);
        var store = GetStore(profile, requestedStoreId);
        request.Context.StoreId = FirstNotEmpty(requestedStoreId, store?.Id);
        request.Context.Currency = FirstNotEmpty(request.Context.Currency, request.Currency, store?.DefaultCurrency);
        request.Context.Language = FirstNotEmpty(request.Context.Language, request.Language, store?.DefaultLanguage);
        request.StoreId = FirstNotEmpty(request.StoreId, request.Context.StoreId);
        request.Currency = FirstNotEmpty(request.Currency, request.Context.Currency);
        request.Language = FirstNotEmpty(request.Language, request.Context.Language);
    }

    private static void ApplyCartDefaults(UcpCartRequest request, UcpProfile profile)
    {
        request.Context ??= new UcpCartContext();
        ApplyCartContextDefaults(request.Context, profile);
        request.StoreId = FirstNotEmpty(request.StoreId, request.Context.StoreId);
        request.Currency = FirstNotEmpty(request.Currency, request.Context.Currency);
        request.Language = FirstNotEmpty(request.Language, request.Context.Language);
        request.BuyerId = FirstNotEmpty(request.BuyerId, request.Context.BuyerId);
        request.OrganizationId = FirstNotEmpty(request.OrganizationId, request.Context.OrganizationId);
        request.CartName = FirstNotEmpty(request.CartName, request.Context.CartName);
        request.CartType = FirstNotEmpty(request.CartType, request.Context.CartType);
    }

    private static void ApplyCheckoutDefaults(UcpCheckoutRequest request, UcpProfile profile)
    {
        request.Context ??= new UcpCartContext();
        ApplyCartContextDefaults(request.Context, profile);
        request.StoreId = FirstNotEmpty(request.StoreId, request.Context.StoreId);
        request.Currency = FirstNotEmpty(request.Currency, request.Context.Currency);
        request.Language = FirstNotEmpty(request.Language, request.Context.Language);
        request.BuyerId = FirstNotEmpty(request.BuyerId, request.Context.BuyerId);
        request.OrganizationId = FirstNotEmpty(request.OrganizationId, request.Context.OrganizationId);
    }

    private static void ApplyCartContextDefaults(UcpCartContext context, UcpProfile profile)
    {
        var store = GetStore(profile, context.StoreId);
        context.StoreId = FirstNotEmpty(context.StoreId, store?.Id);
        context.Currency = FirstNotEmpty(context.Currency, store?.DefaultCurrency);
        context.Language = FirstNotEmpty(context.Language, store?.DefaultLanguage);
    }

    private static UcpStoreProfile GetStore(UcpProfile profile, string storeId)
    {
        if (profile == null)
        {
            return null;
        }

        var selectedStore = GetSelectedStore(profile.Store, storeId);
        if (selectedStore != null)
        {
            return selectedStore;
        }

        if (!string.IsNullOrWhiteSpace(storeId))
        {
            return FindStore(profile.Stores, storeId);
        }

        return GetDefaultStore(profile);
    }

    private static UcpStoreProfile GetSelectedStore(UcpStoreProfile store, string requestedStoreId)
    {
        return store != null &&
            (string.IsNullOrWhiteSpace(requestedStoreId) || StoreIdEquals(store, requestedStoreId))
                ? store
                : null;
    }

    private static UcpStoreProfile GetDefaultStore(UcpProfile profile)
    {
        var defaultStore = FindStore(profile.Stores, profile.DefaultStoreId);
        return defaultStore ?? profile.Stores?.FirstOrDefault(store => store.IsDefault) ?? GetOnlyStore(profile.Stores);
    }

    private static UcpStoreProfile FindStore(IList<UcpStoreProfile> stores, string storeId)
    {
        return string.IsNullOrWhiteSpace(storeId)
            ? null
            : stores?.FirstOrDefault(store => StoreIdEquals(store, storeId));
    }

    private static bool StoreIdEquals(UcpStoreProfile store, string storeId)
    {
        return string.Equals(store.Id, storeId, StringComparison.OrdinalIgnoreCase);
    }

    private static UcpStoreProfile GetOnlyStore(IList<UcpStoreProfile> stores)
    {
        return stores?.Count == 1 ? stores[0] : null;
    }

    private static string FirstNotEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }
}
