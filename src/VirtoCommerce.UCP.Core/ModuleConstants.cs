using System.Collections.Generic;
using VirtoCommerce.Platform.Core.Settings;

namespace VirtoCommerce.UCP.Core;

public static class ModuleConstants
{
    public const string UcpVersion = "1.0";
    public const string DiscoveryVersion = "2026-04-08";
    public const string Source = "UCP";
    public const string Platform = "VirtoCommerce";
    public const string McpInstructions = """
        This MCP endpoint exposes Virto Commerce UCP tools for the storefront/platform where this MCP server is installed.
        Use typed MCP tools for commerce operations.
        Available tools: get_store_capabilities, search_products, get_product, create_cart, list_carts, get_cart, update_cart, create_checkout, update_checkout, checkout_and_handoff, get_payment_handlers, handoff_checkout, list_countries, resolve_country, list_regions, and track_order.
        Do not pass storefront URLs to MCP tools. This MCP server already represents the target Virto Commerce UCP installation.
        Do not infer another target storefront from the MCP transport URL or user-provided links.
        Commerce tools execute local UCP services directly in this platform process.
        Do not use browser/web/search tools to execute UCP operations when MCP tools are available.
        MCP tool calls are stateless. Arguments from earlier calls are never carried automatically.
        For ordinary public shopping requests, call commerce tools directly without linking an account.
        When the user explicitly asks to act on their behalf, use their account, personalized prices, organization,
        saved data, or orders, you MUST call link_buyer_identity before any buyer-sensitive commerce tool.
        Do not call search_products, get_product, create_cart, list_carts, get_cart, update_cart, checkout, or order tools
        for that request until link_buyer_identity succeeds. The MCP transport then completes Platform OAuth and retries the same call.
        A successful anonymous catalog response can include an info message with code identity_optional.
        If the current user request asks for their account, organization, personalized data, or action on their behalf,
        treat identity_optional as an instruction to call link_buyer_identity and then repeat the exact catalog operation before any mutation.
        Buyer and organization identity always come from the validated Platform bearer token. Never send identity headers and never invent buyer or organization identifiers.
        If the user begins anonymously and later asks to continue on their behalf, call link_buyer_identity,
        then call update_cart with the saved anonymous buyer_id, cart_id, and the complete desired line_items state.
        The server verifies anonymous ownership and delegates the idempotent merge to XCart.
        A request carrying a valid Platform user token is authenticated. To make a genuinely anonymous request after linking, the client must omit that token or reconnect without it to this same MCP URL and tool set.
        Before every tool call, build a fresh argument object, check the tool's required schema, and explicitly repeat every required identifier and nested field.
        For every catalog, cart, or checkout tool that exposes store_id, always pass it. Reuse the exact store_id from the catalog call that returned the selected product and continue using it for cart and checkout calls.
        When store_id, currency, or language are unknown, call get_store_capabilities first and use the returned store metadata.
        If this installation exposes multiple stores without a default, use an explicit store_id from the user or ask the user to choose.
        Before get_product, require id or product_id and store_id. Before create_cart, require store_id and non-empty line_items; every new line item requires product_id and quantity greater than zero.
        After any cart response, preserve cart.id, cart.store_id, and cart.buyer_id.
        For later cart and checkout calls, explicitly repeat cart_id, store_id, and buyer_id whenever those arguments are exposed; never rely on conversational memory to carry them.
        Before update_cart, require the saved cart_id and the complete desired line_items state. Before list_carts, require buyer_id and preserve the same store_id used by the buyer's cart.
        For shopping flows, use MCP tools directly: search products, create or update cart, resolve country/regions, then use checkout_and_handoff when the buyer is ready for hosted checkout.
        Delivery addresses belong in structured shipping_address fields, not notes.
        Before create_checkout or checkout_and_handoff, require the saved cart_id, store_id, and buyer_id when available. Before update_checkout, get_payment_handlers, or handoff_checkout, require the saved checkout_id.
        Before create_checkout, update_checkout, checkout_and_handoff, or handoff_checkout for physical goods, require shipping_address.first_name, shipping_address.last_name, and shipping_address.postal_code.
        Never send a partial shipping_address.
        Ask for every missing value and do not call a checkout or handoff tool yet; never invent address data.
        Resolve country with resolve_country and, when the country defines regions, resolve region_id with list_regions before checkout. City remains free text.
        Before resolve_country, require query. Before list_regions, require country_id. Before track_order, require at least one of order_id, order_number, or the saved cart_id.
        Treat price.amount as the current sell price and list_price.amount as the pre-discount reference price.
        list_carts requires buyer_id for anonymous continuation. In authenticated mode, buyer identity and organization come only from the Platform token; never invent or request identity fields from the user.
        update_cart accepts the complete desired line_items state, not a delta. Reuse the existing cart_id and buyer_id; never call create_cart as a fallback for changing an existing cart.
        After create_cart or update_cart, inspect line_items and messages. If an expected line is missing, call get_cart once to account for asynchronous settling; do not claim that an item was added unless the re-read contains it.
        XAPI GraphQL errors are returned unchanged in MCP structuredContent. Inspect their codes, paths, locations, extensions, and partial data before deciding what to do.
        Every MCP tool result includes a model-visible "Trace ID: ..." content block and _meta.trace_id when an active trace exists. Preserve that id with any reported result or failure so operators can open the exact MCP -> UCP -> XAPI trace.
        A read-only XAPI failure from search_products or get_product may be transient; retry the same read-only tool at most once. Do not automatically retry mutating cart or checkout tools.
        For hosted checkout, return checkout.continue_url to the buyer and keep cart_id for later track_order.
        After hosted checkout, use the saved cart_id and buyer_id with track_order when the user asks about the order; do not require an order number when those saved identifiers are available.
        """;

    public static class Capabilities
    {
        public const string Catalog = "catalog";
        public const string Cart = "cart";
        public const string Checkout = "checkout";
        public const string Order = "order";
        public const string Geography = "geography";
    }

    public static class Discovery
    {
        public const string Service = "com.virtocommerce.ucp";
    }

    public static class Headers
    {
        public const string CorrelationId = "X-Correlation-Id";
        public const string TraceId = "X-Trace-Id";
        public const string IdempotencyKey = "Idempotency-Key";
        public const string AgentApiKey = "X-Agent-Api-Key";
        public const string BuyerUserId = "X-Buyer-User-Id";
        public const string BuyerOrganizationId = "X-Buyer-Organization-Id";
    }

    public static class PaymentHandlers
    {
        public const string HostedCheckout = "hosted_checkout";
        public const string NativeCard = "native_card";
        public const string GooglePay = "google_pay";
    }

    public static class ErrorCodes
    {
        public const string IdentityOptional = "identity_optional";
        public const string IdentityRequired = "identity_required";
        public const string BuyerContextMismatch = "buyer_context_mismatch";
        public const string MissingStoreId = "missing_store_id";
        public const string ProductNotFound = "product_not_found";
        public const string CartNotFound = "cart_not_found";
        public const string OrderNotFound = "order_not_found";
        public const string XApiExecutionFailed = "xapi_execution_failed";
        public const string XApiInvalidResponse = "xapi_invalid_response";
        public const string InvalidRequest = "invalid_request";
    }

    public static class Endpoints
    {
        public const string Discovery = "/.well-known/ucp";
        public const string Mcp = "/ucp/mcp";
        public const string McpProtectedResourceMetadata = "/.well-known/oauth-protected-resource/ucp/mcp";
        public const string CatalogSearch = "/ucp/v1/catalog/search";
        public const string CatalogProduct = "/ucp/v1/catalog/products/{id}";
        public const string CartCreate = "/ucp/v1/carts";
        public const string CartList = "/ucp/v1/carts";
        public const string CartGet = "/ucp/v1/carts/{cartId}";
        public const string CartUpdate = "/ucp/v1/carts/{cartId}";
        public const string CheckoutCreate = "/ucp/v1/checkouts";
        public const string CheckoutUpdate = "/ucp/v1/checkouts/{checkoutId}";
        public const string CheckoutPaymentHandlers = "/ucp/v1/checkouts/{checkoutId}/payment-handlers";
        public const string CheckoutHandoff = "/ucp/v1/checkouts/{checkoutId}/handoff";
        public const string OrderTrack = "/ucp/v1/orders/{orderId}";
        public const string OrderTrackByCart = "/ucp/v1/orders?cart_id={cartId}";
        public const string GeographyCountries = "/ucp/v1/geography/countries";
        public const string GeographyCountryResolve = "/ucp/v1/geography/countries/resolve";
        public const string GeographyRegions = "/ucp/v1/geography/countries/{countryId}/regions";
        public const string StorefrontRestore = "/ucp/v1/internal/handoff/restore";
    }

    public static class Operations
    {
        public const string GetStoreCapabilities = "get_store_capabilities";
        public const string SearchProducts = "search_products";
        public const string GetProduct = "get_product";
        public const string CreateCart = "create_cart";
        public const string ListCarts = "list_carts";
        public const string GetCart = "get_cart";
        public const string UpdateCart = "update_cart";
        public const string CreateCheckout = "create_checkout";
        public const string UpdateCheckout = "update_checkout";
        public const string CheckoutAndHandoff = "checkout_and_handoff";
        public const string GetPaymentHandlers = "get_payment_handlers";
        public const string HandoffCheckout = "handoff_checkout";
        public const string TrackOrder = "track_order";
        public const string ListCountries = "list_countries";
        public const string ResolveCountry = "resolve_country";
        public const string ListRegions = "list_regions";
        public const string RestoreHandoff = "restore_handoff";
        public const string LinkBuyerIdentity = "link_buyer_identity";
    }

    public static class McpTools
    {
        public static IReadOnlySet<string> UcpToolNames { get; } = new HashSet<string>(System.StringComparer.Ordinal)
        {
            GetStoreCapabilities,
            SearchProducts,
            GetProduct,
            CreateCart,
            ListCarts,
            GetCart,
            UpdateCart,
            CreateCheckout,
            UpdateCheckout,
            CheckoutAndHandoff,
            GetPaymentHandlers,
            HandoffCheckout,
            TrackOrder,
            ListCountries,
            ResolveCountry,
            ListRegions,
        };

        public const string GetStoreCapabilities = Operations.GetStoreCapabilities;
        public const string SearchProducts = Operations.SearchProducts;
        public const string GetProduct = Operations.GetProduct;
        public const string CreateCart = Operations.CreateCart;
        public const string ListCarts = Operations.ListCarts;
        public const string GetCart = Operations.GetCart;
        public const string UpdateCart = Operations.UpdateCart;
        public const string CreateCheckout = Operations.CreateCheckout;
        public const string UpdateCheckout = Operations.UpdateCheckout;
        public const string CheckoutAndHandoff = Operations.CheckoutAndHandoff;
        public const string GetPaymentHandlers = Operations.GetPaymentHandlers;
        public const string HandoffCheckout = Operations.HandoffCheckout;
        public const string TrackOrder = Operations.TrackOrder;
        public const string ListCountries = Operations.ListCountries;
        public const string ResolveCountry = Operations.ResolveCountry;
        public const string ListRegions = Operations.ListRegions;
        public const string LinkBuyerIdentity = Operations.LinkBuyerIdentity;

        public static bool IsUcpTool(string name)
        {
            return name != null && UcpToolNames.Contains(name);
        }
    }

    public static class Security
    {
        public static class Permissions
        {
            public const string Access = "ucp:access";
            public const string Create = "ucp:create";
            public const string Read = "ucp:read";
            public const string Update = "ucp:update";
            public const string Delete = "ucp:delete";

            public static string[] AllPermissions { get; } =
            [
                Access,
                Create,
                Read,
                Update,
                Delete,
            ];
        }
    }

    public static class Settings
    {
        public static class General
        {
            public static SettingDescriptor UcpEnabled { get; } = new()
            {
                Name = "UCP.Enabled",
                GroupName = "UCP|General",
                ValueType = SettingValueType.Boolean,
                DefaultValue = false,
            };

            public static IEnumerable<SettingDescriptor> AllGeneralSettings
            {
                get
                {
                    yield return UcpEnabled;
                }
            }
        }

        public static IEnumerable<SettingDescriptor> AllSettings
        {
            get
            {
                return General.AllGeneralSettings;
            }
        }
    }
}
