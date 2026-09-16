using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.StoreModule.Core.Model;
using VirtoCommerce.StoreModule.Core.Model.Search;
using VirtoCommerce.StoreModule.Core.Services;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Diagnostics;
using VirtoCommerce.UCP.Core.Models;
using VirtoCommerce.UCP.Core.Options;
using VirtoCommerce.UCP.Core.Services;

namespace VirtoCommerce.UCP.Data.Services;

public class UcpProfileService : IUcpProfileService
{
    private const int StoreSearchTake = 50;

    private static readonly string[] SupportedCapabilities =
    [
        ModuleConstants.Capabilities.Catalog,
        ModuleConstants.Capabilities.Cart,
        ModuleConstants.Capabilities.Checkout,
        ModuleConstants.Capabilities.Order,
        ModuleConstants.Capabilities.Geography,
    ];

    private static readonly string[] AuthScopes =
    [
        "openid",
        "profile",
        "offline_access",
    ];

    private static readonly string[] SupportedMcpTools =
    [
        ModuleConstants.McpTools.LinkBuyerIdentity,
        ModuleConstants.McpTools.GetStoreCapabilities,
        ModuleConstants.McpTools.SearchProducts,
        ModuleConstants.McpTools.GetProduct,
        ModuleConstants.McpTools.CreateCart,
        ModuleConstants.McpTools.ListCarts,
        ModuleConstants.McpTools.GetCart,
        ModuleConstants.McpTools.UpdateCart,
        ModuleConstants.McpTools.CreateCheckout,
        ModuleConstants.McpTools.UpdateCheckout,
        ModuleConstants.McpTools.CheckoutAndHandoff,
        ModuleConstants.McpTools.GetPaymentHandlers,
        ModuleConstants.McpTools.HandoffCheckout,
        ModuleConstants.McpTools.TrackOrder,
        ModuleConstants.McpTools.ListCountries,
        ModuleConstants.McpTools.ResolveCountry,
        ModuleConstants.McpTools.ListRegions,
    ];

    private static readonly string[] CheckoutGuidance =
    [
        "For physical goods, shipping_address is required before hosted handoff when it is not already present on the cart.",
        "create_checkout and handoff_checkout accept shipping_address; billing_address can mirror shipping_address unless a separate billing address is supplied.",
        "Delivery and shipping addresses belong in shipping_address, not notes. Notes are order comments only.",
        "shipping_address and billing_address require recipient first_name and last_name.",
        "Hosted checkout address editing works best with recipient first_name, last_name, email, and postal_code.",
        "Country names should be resolved with resolve_country or list_countries before checkout; list_regions resolves province or region ids when the selected country defines regions.",
        "country_code may be ISO2 in tool input, but UCP normalizes it with Virto Commerce platform ICountriesService before writing XCart. region/region_id are normalized with platform country regions when the selected country has regions; city remains a free-text field.",
        "Natural-language addresses should be mapped to shipping_address fields such as country_code, country_name, city, line1, line2, region, postal_code, phone, and email.",
        "default_store_id or store.id from discovery is the default store_id for catalog, cart, and checkout tools. Multiple stores without default_store_id require an explicit store selection.",
        "Address changes after checkout or handoff require update_checkout followed by a new handoff_checkout URL.",
        "When the buyer is ready to pay or continue to hosted checkout, prefer checkout_and_handoff so the response includes the final continue_url.",
        "After hosted checkout, track_order can use the original cart_id before an order_id is available.",
        "For ordinary shopping, call commerce tools directly without linking an account.",
        "When the user explicitly asks to act on their behalf or use their account, organization, personalized prices, saved data, or orders, call link_buyer_identity before buyer-sensitive commerce tools.",
        "Authenticated buyer and organization identity come only from the validated Platform OAuth token. Never send user or organization identity headers.",
        "To upgrade an anonymous cart, call link_buyer_identity and then update_cart with the saved anonymous buyer_id; UCP verifies ownership and delegates merging to XCart.",
    ];

    private static readonly (string Name, string Method, string Path, string Capability, string Status, string Description)[] EndpointOperations =
    [
        (ModuleConstants.McpTools.LinkBuyerIdentity, "MCP", ModuleConstants.Endpoints.Mcp, "identity_linking", "available", "Trigger Platform OAuth account linking before buyer-sensitive commerce operations."),
        (ModuleConstants.McpTools.GetStoreCapabilities, "GET", ModuleConstants.Endpoints.Discovery, "profile", "available", "Read UCP capabilities, callable MCP tools, endpoint metadata, auth hints, headers, and integration guidance."),
        (ModuleConstants.McpTools.SearchProducts, "POST", ModuleConstants.Endpoints.CatalogSearch, ModuleConstants.Capabilities.Catalog, "available", "Search buyer-aware catalog products."),
        (ModuleConstants.McpTools.GetProduct, "GET", ModuleConstants.Endpoints.CatalogProduct, ModuleConstants.Capabilities.Catalog, "available", "Get one buyer-aware product by stable product id."),
        (ModuleConstants.McpTools.CreateCart, "POST", ModuleConstants.Endpoints.CartCreate, ModuleConstants.Capabilities.Cart, "available", "Create a cart and optionally add the first item."),
        (ModuleConstants.McpTools.ListCarts, "GET", ModuleConstants.Endpoints.CartList, ModuleConstants.Capabilities.Cart, "available", "List recent buyer-scoped carts."),
        (ModuleConstants.McpTools.GetCart, "GET", ModuleConstants.Endpoints.CartGet, ModuleConstants.Capabilities.Cart, "available", "Read cart lines, totals, coupons, addresses, shipments, payments, and continue_url."),
        (ModuleConstants.McpTools.UpdateCart, "PUT", ModuleConstants.Endpoints.CartUpdate, ModuleConstants.Capabilities.Cart, "available", "Update cart items and coupons. With a linked Platform identity it can safely merge the saved anonymous cart through XCart."),
        (
            ModuleConstants.McpTools.CreateCheckout,
            "POST",
            ModuleConstants.Endpoints.CheckoutCreate,
            ModuleConstants.Capabilities.Checkout,
            "available",
            "Create checkout snapshot. Delivery addresses belong in structured shipping_address fields; country and region are normalized through platform dictionaries before XCart is updated."
        ),
        (
            ModuleConstants.McpTools.UpdateCheckout,
            "PATCH",
            ModuleConstants.Endpoints.CheckoutUpdate,
            ModuleConstants.Capabilities.Checkout,
            "available",
            "Update checkout address data before payment. A new handoff URL is required after shipping_address or billing_address changes."
        ),
        (
            ModuleConstants.McpTools.CheckoutAndHandoff,
            "MCP",
            ModuleConstants.Endpoints.Mcp,
            ModuleConstants.Capabilities.Checkout,
            "available",
            "Create checkout and immediately create the hosted checkout handoff URL. Prefer this when the buyer is ready to pay or continue to storefront checkout."
        ),
        (ModuleConstants.McpTools.GetPaymentHandlers, "GET", ModuleConstants.Endpoints.CheckoutPaymentHandlers, ModuleConstants.Capabilities.Checkout, "available", "Read supported payment handlers. hosted_checkout is the current available handler."),
        (
            ModuleConstants.McpTools.HandoffCheckout,
            "POST",
            ModuleConstants.Endpoints.CheckoutHandoff,
            ModuleConstants.Capabilities.Checkout,
            "available",
            "Create hosted checkout handoff URL. For physical goods, shipping_address is expected before handoff; billing_address defaults to shipping_address when no separate billing address is provided."
        ),
        (ModuleConstants.McpTools.TrackOrder, "GET", ModuleConstants.Endpoints.OrderTrack, ModuleConstants.Capabilities.Order, "available", "Track an order by order id or number when the user provides one."),
        (ModuleConstants.McpTools.TrackOrder, "GET", ModuleConstants.Endpoints.OrderTrackByCart, ModuleConstants.Capabilities.Order, "available", "After hosted checkout, track the created order by the original cart_id."),
        (ModuleConstants.McpTools.ListCountries, "GET", ModuleConstants.Endpoints.GeographyCountries, ModuleConstants.Capabilities.Geography, "available", "List or search Virto Commerce platform countries before checkout country normalization."),
        (ModuleConstants.McpTools.ResolveCountry, "GET", ModuleConstants.Endpoints.GeographyCountryResolve, ModuleConstants.Capabilities.Geography, "available", "Resolve a country query such as ISO2, ISO3, or platform country name to the Virto Commerce platform country id."),
        (ModuleConstants.McpTools.ListRegions, "GET", ModuleConstants.Endpoints.GeographyRegions, ModuleConstants.Capabilities.Geography, "available", "List platform regions/provinces for a resolved country id. City remains free text."),
        ("storefront_restore", "POST", ModuleConstants.Endpoints.StorefrontRestore, ModuleConstants.Capabilities.Checkout, "available_storefront", "Storefront-only restore endpoint for ucp_session."),
    ];

    private readonly UcpOptions _options;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IStoreService _storeService;
    private readonly IStoreSearchService _storeSearchService;

    public UcpProfileService(
        IOptions<UcpOptions> options,
        IHttpContextAccessor httpContextAccessor,
        IStoreService storeService = null,
        IStoreSearchService storeSearchService = null)
    {
        _options = options.Value;
        _httpContextAccessor = httpContextAccessor;
        _storeService = storeService;
        _storeSearchService = storeSearchService;
    }

    public virtual async Task<UcpProfile> GetProfile(CancellationToken cancellationToken = default)
    {
        var request = _httpContextAccessor.HttpContext?.Request;
        var storeProfiles = await GetStoreProfiles();
        var storeProfile = storeProfiles.FirstOrDefault(x => x.IsDefault);
        var origin = GetConfiguredStorefrontOrigin(storeProfile) ?? GetRequestOrigin(request);

        var result = new UcpProfile
        {
            Ucp = CreateDiscoveryProfile(request),
            UcpVersion = ModuleConstants.UcpVersion,
            Platform = ModuleConstants.Platform,
            StorefrontOrigin = origin,
            DefaultStoreId = storeProfile?.Id,
            Store = storeProfile,
            Endpoints = new UcpEndpointProfile
            {
                UcpBaseUrl = BuildUcpBaseUrl(request),
                HandoffUrlTemplate = GetHandoffTemplate(origin),
            },
            Auth = new UcpProfileAuth
            {
                Agent = "mcp_transport",
                AnonymousCatalog = _options.AnonymousCatalog,
                BuyerDelegation = "platform_oauth_bearer",
                BuyerIdentitySource = "platform_claims_principal",
                AuthorizationServer = request == null ? null : $"{GetRequestOrigin(request)}{request.PathBase}/",
                ProtectedResourceMetadata = BuildProtectedResourceMetadataUrl(request),
            },
            Headers = new UcpHeaderProfile
            {
                AgentApiKey = ModuleConstants.Headers.AgentApiKey,
                CorrelationId = ModuleConstants.Headers.CorrelationId,
                TraceId = ModuleConstants.Headers.TraceId,
                IdempotencyKey = ModuleConstants.Headers.IdempotencyKey,
            },
            Errors = new UcpErrorProfile
            {
                Schema = "ucp_error",
                Codes =
                {
                    ModuleConstants.ErrorCodes.InvalidRequest,
                    ModuleConstants.ErrorCodes.IdentityRequired,
                    ModuleConstants.ErrorCodes.BuyerContextMismatch,
                    ModuleConstants.ErrorCodes.MissingStoreId,
                    ModuleConstants.ErrorCodes.ProductNotFound,
                    ModuleConstants.ErrorCodes.CartNotFound,
                    ModuleConstants.ErrorCodes.OrderNotFound,
                    ModuleConstants.ErrorCodes.XApiInvalidResponse,
                },
            },
        };

        foreach (var candidate in storeProfiles)
        {
            result.Stores.Add(candidate);
        }

        foreach (var scope in AuthScopes)
        {
            result.Auth.Scopes.Add(scope);
        }

        foreach (var capability in SupportedCapabilities)
        {
            result.Capabilities.Add(capability);
        }

        AddEndpointOperations(result.Endpoints);
        AddSupportedMcpTools(result);
        AddCheckoutGuidance(result);

        foreach (var paymentHandler in UcpPaymentHandlerProfiles.Create())
        {
            result.PaymentHandlers.Add(paymentHandler);
        }

        return result;
    }

    protected virtual UcpDiscoveryProfile CreateDiscoveryProfile(HttpRequest request)
    {
        var result = new UcpDiscoveryProfile
        {
            Version = ModuleConstants.DiscoveryVersion,
            Status = "success",
        };

        AddDiscoveryService(result, "mcp", BuildMcpUrl(request));

        foreach (var capability in SupportedCapabilities)
        {
            result.Capabilities[$"{ModuleConstants.Discovery.Service}.{capability}"] =
            [
                new UcpCapabilityVersion
                {
                    Version = ModuleConstants.DiscoveryVersion,
                },
            ];
        }

        return result;
    }

    protected virtual void AddDiscoveryService(UcpDiscoveryProfile profile, string transport, string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out _))
        {
            return;
        }

        if (!profile.Services.TryGetValue(ModuleConstants.Discovery.Service, out var services))
        {
            services = new List<UcpDiscoveryServiceProfile>();
            profile.Services[ModuleConstants.Discovery.Service] = services;
        }

        services.Add(new UcpDiscoveryServiceProfile
        {
            Version = ModuleConstants.DiscoveryVersion,
            Transport = transport,
            Endpoint = endpoint,
        });
    }

    protected virtual void AddEndpointOperations(UcpEndpointProfile endpoints)
    {
        foreach (var operation in EndpointOperations)
        {
            AddOperation(endpoints, operation.Name, operation.Method, operation.Path, operation.Capability, operation.Status, operation.Description);
        }
    }

    protected virtual void AddOperation(UcpEndpointProfile endpoints, string name, string method, string path, string capability, string status, string description)
    {
        endpoints.Operations.Add(new UcpOperationProfile
        {
            Name = name,
            Method = method,
            Path = path,
            Capability = capability,
            Status = status,
            Description = description,
        });
    }

    protected virtual void AddSupportedMcpTools(UcpProfile profile)
    {
        foreach (var tool in SupportedMcpTools)
        {
            profile.McpTools.Add(tool);
        }
    }

    protected virtual void AddCheckoutGuidance(UcpProfile profile)
    {
        foreach (var guidance in CheckoutGuidance)
        {
            profile.AgentGuidance.Add(guidance);
        }
    }

    protected virtual async Task<IList<UcpStoreProfile>> GetStoreProfiles()
    {
        var configuredStore = await GetConfiguredDefaultStore();
        if (HasConfiguredDefaultStore(configuredStore))
        {
            return CreateConfiguredStoreProfiles(configuredStore);
        }

        return await GetDiscoveredStoreProfiles();
    }

    protected virtual bool HasConfiguredDefaultStore(Store configuredStore)
    {
        return configuredStore != null || !string.IsNullOrWhiteSpace(_options.DefaultStoreId);
    }

    protected virtual IList<UcpStoreProfile> CreateConfiguredStoreProfiles(Store configuredStore)
    {
        var source = configuredStore == null ? "configuration" : "store_service";
        var configuredProfile = CreateStoreProfile(configuredStore, isDefault: true, source);

        return configuredProfile == null
            ? new List<UcpStoreProfile>()
            : new List<UcpStoreProfile> { configuredProfile };
    }

    protected virtual async Task<IList<UcpStoreProfile>> GetDiscoveredStoreProfiles()
    {
        var stores = await SearchOpenStores();

        return stores
            .Select(store => CreateStoreProfile(store, isDefault: stores.Count == 1, source: "store_search"))
            .Where(profile => profile != null)
            .ToList();
    }

    protected virtual async Task<Store> GetConfiguredDefaultStore()
    {
        if (_storeService == null || string.IsNullOrWhiteSpace(_options.DefaultStoreId))
        {
            return null;
        }

        return await UcpDiagnostics.ExecuteDependency(
            "stores",
            "GetStore",
            () => _storeService.GetNoCloneAsync(_options.DefaultStoreId));
    }

    protected virtual async Task<IList<Store>> SearchOpenStores()
    {
        if (_storeSearchService == null)
        {
            return new List<Store>();
        }

        var result = await UcpDiagnostics.ExecuteDependency(
            "stores",
            "SearchStores",
            () => _storeSearchService.SearchAsync(new StoreSearchCriteria
            {
                StoreStates = new[] { StoreState.Open },
                Take = StoreSearchTake,
            }));

        return result?.Results?
            .Where(store => store != null)
            .ToList() ?? new List<Store>();
    }

    protected virtual UcpStoreProfile CreateStoreProfile(Store store, bool isDefault, string source)
    {
        if (store == null && string.IsNullOrWhiteSpace(_options.DefaultStoreId))
        {
            return null;
        }

        return new UcpStoreProfile
        {
            Id = ResolveStoreId(store),
            Name = store?.Name,
            Url = NormalizeUrl(store?.Url),
            SecureUrl = NormalizeUrl(store?.SecureUrl),
            DefaultCurrency = ResolveStoreCurrency(store),
            DefaultLanguage = ResolveStoreLanguage(store),
            Source = source,
            IsDefault = isDefault,
        };
    }

    protected virtual string ResolveStoreId(Store store)
    {
        return store?.Id ?? _options.DefaultStoreId;
    }

    protected virtual string ResolveStoreCurrency(Store store)
    {
        return store?.DefaultCurrency ?? _options.DefaultCurrency;
    }

    protected virtual string ResolveStoreLanguage(Store store)
    {
        return store?.DefaultLanguage ?? _options.DefaultCultureName;
    }

    protected virtual string GetConfiguredStorefrontOrigin(UcpStoreProfile store)
    {
        if (!string.IsNullOrWhiteSpace(_options.StorefrontOrigin))
        {
            return _options.StorefrontOrigin.TrimEnd('/');
        }

        var storeUrl = FirstNotEmpty(store?.SecureUrl, store?.Url);
        if (!string.IsNullOrWhiteSpace(storeUrl))
        {
            return storeUrl.TrimEnd('/');
        }

        return null;
    }

    protected virtual string GetRequestOrigin(HttpRequest request)
    {
        return request == null
            ? null
            : $"{request.Scheme}://{request.Host}".TrimEnd('/');
    }

    protected virtual string BuildUcpBaseUrl(HttpRequest request)
    {
        if (!string.IsNullOrWhiteSpace(_options.UcpBaseUrl))
        {
            return _options.UcpBaseUrl.TrimEnd('/');
        }

        return request == null
            ? "/ucp/v1"
            : $"{request.Scheme}://{request.Host}/ucp/v1".TrimEnd('/');
    }

    protected virtual string BuildMcpUrl(HttpRequest request)
    {
        var origin = GetRequestOrigin(request);
        if (string.IsNullOrWhiteSpace(origin) && Uri.TryCreate(_options.UcpBaseUrl, UriKind.Absolute, out var ucpBaseUri))
        {
            origin = ucpBaseUri.GetLeftPart(UriPartial.Authority);
        }

        return string.IsNullOrWhiteSpace(origin)
            ? null
            : $"{origin}{ModuleConstants.Endpoints.Mcp}";
    }

    protected virtual string BuildProtectedResourceMetadataUrl(HttpRequest request)
    {
        var origin = GetRequestOrigin(request);
        return string.IsNullOrWhiteSpace(origin)
            ? ModuleConstants.Endpoints.McpProtectedResourceMetadata
            : origin + request.PathBase + ModuleConstants.Endpoints.McpProtectedResourceMetadata;
    }

    protected virtual string GetHandoffTemplate(string origin)
    {
        if (!string.IsNullOrWhiteSpace(_options.HandoffUrlTemplate))
        {
            return _options.HandoffUrlTemplate;
        }

        return string.IsNullOrWhiteSpace(origin)
            ? "/checkout?ucp_session={token}"
            : $"{origin}/checkout?ucp_session={{token}}";
    }

    protected static string FirstNotEmpty(params string[] values)
    {
        return values?.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
    }

    protected static string NormalizeUrl(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.TrimEnd('/');
    }
}
