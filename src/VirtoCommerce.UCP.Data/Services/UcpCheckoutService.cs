using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.Platform.Core.DistributedLock;
using VirtoCommerce.StoreModule.Core.Services;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Diagnostics;
using VirtoCommerce.UCP.Core.Models;
using VirtoCommerce.UCP.Core.Options;
using VirtoCommerce.UCP.Core.Services;
using VirtoCommerce.UCP.Data.Models;

namespace VirtoCommerce.UCP.Data.Services;

public class UcpCheckoutService : UcpServiceBase, IUcpCheckoutService
{
    private const int HandoffSessionTokenBytes = 32;
    private const string HandoffSessionCacheKeyPrefix = "UCP:Handoff:";
    private const string StatusIncomplete = "incomplete";
    private const string StatusRequiresEscalation = "requires_escalation";
    private const string CheckoutCapability = "dev.ucp.shopping.checkout";
    private const string HandoffCapability = "dev.ucp.shopping.checkout.handoff";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IUcpCartService _cartService;
    private readonly IDistributedCache _distributedCache;
    private readonly IDistributedLockService _distributedLock;
    private readonly IStoreService _storeService;
    private readonly UcpOptions _options;

    public UcpCheckoutService(
        IUcpCartService cartService,
        IDistributedCache distributedCache,
        IDistributedLockService distributedLock,
        IHttpContextAccessor httpContextAccessor,
        IOptions<UcpOptions> options,
        IStoreService storeService = null,
        IUcpBuyerContextAccessor buyerContextAccessor = null)
        : base(httpContextAccessor, buyerContextAccessor)
    {
        _cartService = cartService;
        _distributedCache = distributedCache;
        _distributedLock = distributedLock;
        _options = options.Value;
        _storeService = storeService;
    }

    public virtual async Task<UcpCheckoutResponse> CreateCheckout(UcpCheckoutRequest request, CancellationToken cancellationToken = default)
    {
        request ??= new UcpCheckoutRequest();
        NormalizeCheckoutContext(request);
        ValidateSuppliedShippingAddress(request.ShippingAddress);
        var cart = await PrepareCartForCheckout(request, cancellationToken);
        var checkout = CreateCheckout(request, cart, StatusIncomplete);

        checkout.Messages.Add(new UcpMessage
        {
            Type = "info",
            Code = "handoff_required",
            Content = "Checkout is ready for hosted handoff. Provided shipping and billing addresses are already applied to the cart.",
            Severity = "info",
        });
        AddAddressStateMessages(checkout, request);

        return new UcpCheckoutResponse
        {
            Ucp = CreateMetadata("success", CheckoutCapability),
            Checkout = checkout,
            Messages = checkout.Messages,
        };
    }

    public virtual async Task<UcpCheckoutResponse> UpdateCheckout(string checkoutId, UcpCheckoutRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkoutId);

        request ??= new UcpCheckoutRequest();
        request.CartId = FirstNotEmpty(request.CartId, checkoutId);
        NormalizeCheckoutContext(request);
        ValidateSuppliedShippingAddress(request.ShippingAddress);

        var cart = await PrepareCartForCheckout(request, cancellationToken);
        var checkout = CreateCheckout(request, cart, StatusIncomplete);

        checkout.Messages.Add(new UcpMessage
        {
            Type = "info",
            Code = "checkout_updated",
            Content = "Checkout data was updated. Create a new handoff URL to use the latest cart snapshot.",
            Severity = "info",
        });
        AddAddressStateMessages(checkout, request);

        return new UcpCheckoutResponse
        {
            Ucp = CreateMetadata("success", CheckoutCapability),
            Checkout = checkout,
            Messages = checkout.Messages,
        };
    }

    public virtual Task<UcpPaymentHandlersResponse> GetPaymentHandlers(string checkoutId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkoutId);

        return Task.FromResult(new UcpPaymentHandlersResponse
        {
            Ucp = CreateMetadata("success", CheckoutCapability),
            CheckoutId = checkoutId,
            PaymentHandlers = UcpPaymentHandlerProfiles.Create(),
        });
    }

    public virtual async Task<UcpCheckoutHandoffResponse> HandoffCheckout(string checkoutId, UcpCheckoutRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkoutId);

        request ??= new UcpCheckoutRequest();
        request.CartId = FirstNotEmpty(request.CartId, checkoutId);
        NormalizeCheckoutContext(request);
        ValidateSuppliedShippingAddress(request.ShippingAddress);

        var cart = await PrepareCartForCheckout(request, cancellationToken);
        ValidateEffectiveShippingAddress(request.ShippingAddress, cart);
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(Math.Max(1, _options.HandoffTokenTtlMinutes));
        var checkout = CreateCheckout(request, cart, StatusRequiresEscalation);
        checkout.ExpiresAt = expiresAt;
        var sessionToken = await StoreHandoffPayload(checkout, request.Context, expiresAt, cancellationToken);
        checkout.ContinueUrl = await BuildContinueUrl(sessionToken, checkout.Cart.StoreId);
        checkout.Messages.Add(new UcpMessage
        {
            Type = "info",
            Code = "shipping_required",
            Content = "Hosted checkout is ready. Provided shipping and billing addresses are already applied to the cart.",
            Severity = "info",
        });
        AddAddressStateMessages(checkout, request);

        return new UcpCheckoutHandoffResponse
        {
            Ucp = CreateMetadata("success", HandoffCapability),
            Checkout = checkout,
            Messages = checkout.Messages,
        };
    }

    public virtual async Task<UcpHandoffRestoreResponse> RestoreHandoff(UcpHandoffRestoreRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request?.UcpSession))
        {
            throw CreateException(ModuleConstants.ErrorCodes.InvalidRequest, "ucp_session is required.");
        }

        var cacheKey = GetHandoffSessionCacheKey(request.UcpSession);
        using (await AsyncLock.GetLockByKey(cacheKey).LockAsync())
        {
            return await _distributedLock.ExecuteAsync(
                cacheKey,
                () => RestoreHandoffCore(cacheKey, cancellationToken),
                lockTimeout: TimeSpan.FromSeconds(30),
                tryLockTimeout: TimeSpan.FromSeconds(5),
                retryInterval: TimeSpan.FromMilliseconds(50),
                cancellationToken: cancellationToken);
        }
    }

    private async Task<UcpHandoffRestoreResponse> RestoreHandoffCore(string cacheKey, CancellationToken cancellationToken)
    {
        var cacheResult = await UcpDiagnostics.ExecuteDependency(
            "cache",
            "distributed-cache",
            "GetHandoffSession",
            async () =>
            {
                var payloadJson = await _distributedCache.GetStringAsync(cacheKey, cancellationToken);
                if (string.IsNullOrWhiteSpace(payloadJson))
                {
                    return (Payload: (CheckoutHandoffTokenPayload)null, Outcome: "miss");
                }

                CheckoutHandoffTokenPayload payload;
                try
                {
                    payload = JsonSerializer.Deserialize<CheckoutHandoffTokenPayload>(payloadJson, JsonOptions);
                }
                catch (JsonException)
                {
                    return (Payload: (CheckoutHandoffTokenPayload)null, Outcome: "corrupt");
                }

                if (payload == null)
                {
                    return (Payload: (CheckoutHandoffTokenPayload)null, Outcome: "corrupt");
                }

                return payload.ExpiresAt <= DateTimeOffset.UtcNow
                    ? (Payload: (CheckoutHandoffTokenPayload)null, Outcome: "expired")
                    : (Payload: payload, Outcome: "hit");
            },
            result => result.Outcome);

        var payload = cacheResult.Payload;
        if (payload == null)
        {
            throw CreateException(ModuleConstants.ErrorCodes.InvalidRequest, "ucp_session is invalid or expired.");
        }

        var buyerContext = ResolveBuyerContext(
            requestedBuyerIds: [payload.BuyerId],
            requestedOrganizationIds: [payload.OrganizationId],
            requireBuyer: true,
            requireAuthenticatedBuyer: payload.RequiresAuthentication);

        var context = new UcpCartContext
        {
            StoreId = payload.StoreId,
            Currency = payload.Currency,
            Language = payload.CultureName,
            BuyerId = buyerContext.PublicBuyerId,
            OrganizationId = buyerContext.OrganizationId,
        };

        var cart = await GetCartForCheckout(payload.CartId, context, cancellationToken);
        await UcpDiagnostics.ExecuteDependency(
            "cache",
            "distributed-cache",
            "RemoveHandoffSession",
            () => _distributedCache.RemoveAsync(cacheKey, cancellationToken));
        var checkout = new UcpCheckout
        {
            Id = payload.CheckoutId,
            CartId = payload.CartId,
            Status = StatusRequiresEscalation,
            Cart = cart,
            PaymentHandlers = UcpPaymentHandlerProfiles.Create(),
            Buyer = payload.Buyer,
            ShippingAddress = payload.ShippingAddress,
            BillingAddress = payload.BillingAddress,
            ShippingMethodId = payload.ShippingMethodId,
            PaymentHandler = payload.PaymentHandler,
            ExpiresAt = payload.ExpiresAt,
        };

        return new UcpHandoffRestoreResponse
        {
            Ucp = CreateMetadata("success", HandoffCapability),
            Checkout = checkout,
            AnonymousBuyerId = buyerContext.IsAuthenticated ? null : buyerContext.PublicBuyerId,
        };
    }

    protected virtual async Task<UcpCart> GetCartForCheckout(string cartId, UcpCartContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cartId))
        {
            throw CreateException(ModuleConstants.ErrorCodes.InvalidRequest, "cart_id is required.");
        }

        var response = await _cartService.GetCart(
            cartId,
            new UcpCartRequest { Context = context },
            cancellationToken);
        if (response.Cart.LineItems.Count == 0)
        {
            throw CreateException(ModuleConstants.ErrorCodes.InvalidRequest, "Checkout requires a non-empty cart.");
        }

        return response.Cart;
    }

    protected virtual async Task<UcpCart> PrepareCartForCheckout(UcpCheckoutRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.CartId))
        {
            throw CreateException(ModuleConstants.ErrorCodes.InvalidRequest, "cart_id is required.");
        }

        var response = request.ShippingAddress != null || request.BillingAddress != null
            ? await _cartService.ApplyCheckoutData(request.CartId, request, cancellationToken)
            : await _cartService.GetCart(
                request.CartId,
                new UcpCartRequest { Context = request.Context },
                cancellationToken);

        if (response.Cart.LineItems.Count == 0)
        {
            throw CreateException(ModuleConstants.ErrorCodes.InvalidRequest, "Checkout requires a non-empty cart.");
        }

        return response.Cart;
    }

    protected virtual void NormalizeCheckoutContext(UcpCheckoutRequest request)
    {
        request.Context ??= new UcpCartContext();
        request.Context.StoreId = FirstNotEmpty(request.StoreId, request.Context.StoreId);
        request.Context.Currency = FirstNotEmpty(request.Currency, request.Context.Currency);
        request.Context.Language = FirstNotEmpty(request.Language, request.Context.Language);
        request.Context.BuyerId = FirstNotEmpty(request.BuyerId, request.Context.BuyerId);
        request.Context.OrganizationId = FirstNotEmpty(request.OrganizationId, request.Context.OrganizationId);
    }

    protected virtual void ValidateSuppliedShippingAddress(UcpCheckoutAddress address)
    {
        if (address != null)
        {
            ValidateShippingAddress(address.FirstName, address.LastName, address.PostalCode);
        }
    }

    protected virtual void ValidateEffectiveShippingAddress(UcpCheckoutAddress requestedAddress, UcpCart cart)
    {
        if (requestedAddress != null)
        {
            return;
        }

        var cartAddress = cart?.Shipments?
            .Select(shipment => shipment.DeliveryAddress)
            .FirstOrDefault(address => address != null) ??
            cart?.Addresses?.FirstOrDefault(address => string.Equals(address.AddressType, "shipping", StringComparison.OrdinalIgnoreCase));

        if (cartAddress == null)
        {
            throw CreateException(ModuleConstants.ErrorCodes.InvalidRequest, "shipping_address is required for hosted checkout.");
        }

        ValidateShippingAddress(cartAddress.FirstName, cartAddress.LastName, cartAddress.PostalCode);
    }

    protected virtual void ValidateShippingAddress(string firstName, string lastName, string postalCode)
    {
        if (string.IsNullOrWhiteSpace(firstName) || string.IsNullOrWhiteSpace(lastName))
        {
            throw CreateException(ModuleConstants.ErrorCodes.InvalidRequest, "shipping_address.first_name and shipping_address.last_name are required for hosted checkout.");
        }

        if (string.IsNullOrWhiteSpace(postalCode))
        {
            throw CreateException(ModuleConstants.ErrorCodes.InvalidRequest, "shipping_address.postal_code is required for hosted checkout.");
        }
    }

    protected virtual UcpCheckout CreateCheckout(UcpCheckoutRequest request, UcpCart cart, string status)
    {
        return new UcpCheckout
        {
            Id = cart.Id,
            CartId = cart.Id,
            Status = status,
            Cart = cart,
            PaymentHandlers = UcpPaymentHandlerProfiles.Create(),
            Buyer = MergeBuyer(request.Buyer, cart),
            ShippingAddress = MergeAddress(request.ShippingAddress, cart, "shipping"),
            BillingAddress = MergeAddress(request.BillingAddress, cart, "billing") ?? MergeAddress(request.ShippingAddress, cart, "shipping"),
            ShippingMethodId = request.ShippingMethodId,
            PaymentHandler = FirstNotEmpty(request.PaymentHandler, ModuleConstants.PaymentHandlers.HostedCheckout),
        };
    }

    protected virtual void AddAddressStateMessages(UcpCheckout checkout, UcpCheckoutRequest request)
    {
        if (checkout.ShippingAddress == null)
        {
            if (!string.IsNullOrWhiteSpace(request?.Notes))
            {
                checkout.Messages.Add(new UcpMessage
                {
                    Type = "warning",
                    Code = "shipping_address_not_notes",
                    Content = "Order notes are not used as the shipping address.",
                    Severity = "warning",
                });
            }

            checkout.Messages.Add(new UcpMessage
            {
                Type = "warning",
                Code = "shipping_address_missing",
                Content = "Shipping address is not set.",
                Severity = "warning",
            });
        }
        else
        {
            checkout.Messages.Add(new UcpMessage
            {
                Type = "info",
                Code = "shipping_address_prefilled",
                Content = "Shipping address is applied to the cart snapshot.",
                Severity = "info",
            });

            if (string.IsNullOrWhiteSpace(checkout.ShippingAddress.PostalCode))
            {
                checkout.Messages.Add(new UcpMessage
                {
                    Type = "warning",
                    Code = "shipping_postal_code_missing",
                    Content = "Shipping address postal_code is missing.",
                    Severity = "warning",
                });
            }
        }
    }

    protected virtual UcpCheckoutAddress MergeAddress(UcpCheckoutAddress requestedAddress, UcpCart cart, string addressType)
    {
        return MergeAddress(ToCheckoutAddress(GetCartCheckoutAddress(cart, addressType)), requestedAddress);
    }

    protected virtual UcpCheckoutAddress MergeAddress(UcpCheckoutAddress cartAddress, UcpCheckoutAddress requestedAddress)
    {
        if (cartAddress == null)
        {
            return requestedAddress;
        }

        if (requestedAddress == null)
        {
            return cartAddress;
        }

        return new UcpCheckoutAddress
        {
            Id = FirstNotEmpty(cartAddress.Id, requestedAddress.Id),
            Name = FirstNotEmpty(cartAddress.Name, requestedAddress.Name),
            Organization = FirstNotEmpty(cartAddress.Organization, requestedAddress.Organization),
            FirstName = FirstNotEmpty(cartAddress.FirstName, requestedAddress.FirstName),
            LastName = FirstNotEmpty(cartAddress.LastName, requestedAddress.LastName),
            Line1 = FirstNotEmpty(cartAddress.Line1, requestedAddress.Line1),
            Line2 = FirstNotEmpty(cartAddress.Line2, requestedAddress.Line2),
            City = FirstNotEmpty(cartAddress.City, requestedAddress.City),
            Region = FirstNotEmpty(cartAddress.Region, requestedAddress.Region),
            RegionId = FirstNotEmpty(cartAddress.RegionId, requestedAddress.RegionId),
            PostalCode = FirstNotEmpty(cartAddress.PostalCode, requestedAddress.PostalCode),
            CountryCode = FirstNotEmpty(cartAddress.CountryCode, requestedAddress.CountryCode),
            CountryName = FirstNotEmpty(cartAddress.CountryName, requestedAddress.CountryName),
            Phone = FirstNotEmpty(cartAddress.Phone, requestedAddress.Phone),
            Email = FirstNotEmpty(cartAddress.Email, requestedAddress.Email),
        };
    }

    protected virtual UcpCartAddress GetCartCheckoutAddress(UcpCart cart, string addressType)
    {
        if (cart == null)
        {
            return null;
        }

        if (string.Equals(addressType, "shipping", StringComparison.OrdinalIgnoreCase))
        {
            return cart.Shipments.LastOrDefault(x => x.DeliveryAddress != null)?.DeliveryAddress
                ?? cart.Addresses.LastOrDefault(x => string.Equals(x.AddressType, addressType, StringComparison.OrdinalIgnoreCase));
        }

        if (string.Equals(addressType, "billing", StringComparison.OrdinalIgnoreCase))
        {
            return cart.Payments.LastOrDefault(x => x.BillingAddress != null)?.BillingAddress
                ?? cart.Addresses.LastOrDefault(x => string.Equals(x.AddressType, addressType, StringComparison.OrdinalIgnoreCase));
        }

        return cart.Addresses.LastOrDefault(x => string.Equals(x.AddressType, addressType, StringComparison.OrdinalIgnoreCase));
    }

    protected virtual UcpCheckoutAddress ToCheckoutAddress(UcpCartAddress address)
    {
        if (address == null)
        {
            return null;
        }

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

    protected virtual async Task<string> StoreHandoffPayload(UcpCheckout checkout, UcpCartContext context, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        var buyerContext = ResolveBuyerContext(
            requestedBuyerIds: [checkout.Buyer?.Id, checkout.Cart?.BuyerId, context?.BuyerId],
            requestedOrganizationIds: [checkout.Cart?.OrganizationId, context?.OrganizationId],
            requireBuyer: true);
        var sessionToken = GenerateSessionToken();
        var payload = new CheckoutHandoffTokenPayload
        {
            CheckoutId = checkout.Id,
            CartId = checkout.CartId,
            StoreId = checkout.Cart.StoreId,
            Currency = checkout.Cart.Currency,
            CultureName = FirstNotEmpty(context?.Language, _options.DefaultCultureName, "en-US"),
            BuyerId = buyerContext.PublicBuyerId,
            OrganizationId = buyerContext.OrganizationId,
            RequiresAuthentication = buyerContext.IsAuthenticated,
            IssuedAt = DateTimeOffset.UtcNow,
            Buyer = checkout.Buyer,
            ShippingAddress = checkout.ShippingAddress,
            BillingAddress = checkout.BillingAddress,
            ShippingMethodId = checkout.ShippingMethodId,
            PaymentHandler = checkout.PaymentHandler,
            ExpiresAt = expiresAt,
        };

        await UcpDiagnostics.ExecuteDependency(
            "cache",
            "distributed-cache",
            "SetHandoffSession",
            () => _distributedCache.SetStringAsync(
                GetHandoffSessionCacheKey(sessionToken),
                JsonSerializer.Serialize(payload, JsonOptions),
                new DistributedCacheEntryOptions
                {
                    AbsoluteExpiration = expiresAt,
                },
                cancellationToken));

        return sessionToken;
    }

    protected virtual string GenerateSessionToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(HandoffSessionTokenBytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    protected virtual string GetHandoffSessionCacheKey(string sessionToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionToken);
        var tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(sessionToken));
        return HandoffSessionCacheKeyPrefix + Convert.ToHexString(tokenHash);
    }

    protected virtual async Task<string> BuildContinueUrl(string token, string storeId)
    {
        var storefrontOrigin = await GetStorefrontOrigin(storeId);
        var template = FirstNotEmpty(_options.HandoffUrlTemplate, $"{storefrontOrigin?.TrimEnd('/')}/checkout?ucp_session={{token}}");
        return template.Replace("{token}", Uri.EscapeDataString(token), StringComparison.Ordinal);
    }

    protected virtual async Task<string> GetStorefrontOrigin(string storeId)
    {
        if (_storeService != null && !string.IsNullOrWhiteSpace(storeId))
        {
            var store = await UcpDiagnostics.ExecuteDependency(
                "stores",
                "GetStore",
                () => _storeService.GetNoCloneAsync(storeId));
            var storeUrl = FirstNotEmpty(store?.SecureUrl, store?.Url);

            if (!string.IsNullOrWhiteSpace(storeUrl))
            {
                return storeUrl.TrimEnd('/');
            }
        }

        return _options.StorefrontOrigin?.TrimEnd('/');
    }

    protected virtual UcpCheckoutBuyer MergeBuyer(UcpCheckoutBuyer buyer, UcpCart cart)
    {
        buyer ??= new UcpCheckoutBuyer();
        buyer.Id = cart.BuyerId;
        return buyer;
    }

}
