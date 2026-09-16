using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using VirtoCommerce.Platform.Core.DistributedLock;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Diagnostics;
using VirtoCommerce.UCP.Core.Models;
using VirtoCommerce.UCP.Core.Options;
using VirtoCommerce.UCP.Core.Services;
using VirtoCommerce.UCP.Data.Services;
using Xunit;

namespace VirtoCommerce.UCP.Tests;

[Trait("Category", "Unit")]
public class UcpCheckoutServiceTests
{
    [Fact]
    public async Task CreateCheckout_ReturnsCheckoutSnapshotFromCart()
    {
        var service = CreateService(new StubCartService(CreateCart()));

        var response = await service.CreateCheckout(new UcpCheckoutRequest
        {
            CartId = "cart-1",
            Context = new UcpCartContext { StoreId = "store-acme", Currency = "USD", Language = "en-US" },
        }, TestContext.Current.CancellationToken);

        Assert.Equal("cart-1", response.Checkout.Id);
        Assert.Equal("incomplete", response.Checkout.Status);
        Assert.Equal("buyer-1", response.Checkout.Buyer.Id);
        Assert.Contains(response.Checkout.PaymentHandlers, x => x.Code == ModuleConstants.PaymentHandlers.HostedCheckout && x.Available);
        Assert.Contains(response.Messages, x => x.Code == "handoff_required");
        Assert.Null(response.Checkout.ContinueUrl);
    }

    [Fact]
    public async Task HandoffCheckout_ReturnsContinueUrlAndRestoreReadsToken()
    {
        var cart = CreateCart();
        cart.Addresses.Add(CreateShippingAddress());
        var cache = new StubDistributedCache();
        var distributedLock = new TestDistributedLockService();
        var service = CreateService(new StubCartService(cart), cache, distributedLock);

        var handoff = await service.HandoffCheckout("cart-1", new UcpCheckoutRequest
        {
            Context = new UcpCartContext { StoreId = "store-acme", Currency = "USD", Language = "en-US" },
            Buyer = new UcpCheckoutBuyer { Email = "buyer@example.com" },
        }, TestContext.Current.CancellationToken);

        Assert.Equal("requires_escalation", handoff.Checkout.Status);
        Assert.Contains("ucp_session=", handoff.Checkout.ContinueUrl);
        Assert.Contains(handoff.Messages, x => x.Code == "shipping_required");

        var token = handoff.Checkout.ContinueUrl.Split("ucp_session=").Last();
        var cacheKey = GetHandoffCacheKey(token);
        Assert.DoesNotContain(token, cacheKey, StringComparison.Ordinal);
        var payloadJson = Encoding.UTF8.GetString(cache.Get(cacheKey));
        using (var payload = JsonDocument.Parse(payloadJson))
        {
            Assert.True(payload.RootElement.TryGetProperty("issued_at", out _));
            Assert.True(payload.RootElement.TryGetProperty("expires_at", out _));
            Assert.False(payload.RootElement.TryGetProperty("access_token", out _));
            Assert.False(payload.RootElement.TryGetProperty("refresh_token", out _));
            Assert.DoesNotContain(token, payloadJson, StringComparison.Ordinal);
        }
        var restore = await service.RestoreHandoff(new UcpHandoffRestoreRequest
        {
            UcpSession = System.Uri.UnescapeDataString(token),
        }, TestContext.Current.CancellationToken);

        Assert.Equal("cart-1", restore.Checkout.CartId);
        Assert.Equal("buyer@example.com", restore.Checkout.Buyer.Email);
        Assert.Equal("buyer-1", restore.Checkout.Buyer.Id);
        Assert.Equal("buyer-1", restore.AnonymousBuyerId);
        Assert.Contains(cacheKey, distributedLock.ResourceKeys);

        var replay = await Assert.ThrowsAsync<UcpException>(() => service.RestoreHandoff(new UcpHandoffRestoreRequest
        {
            UcpSession = token,
        }, TestContext.Current.CancellationToken));
        Assert.Equal(ModuleConstants.ErrorCodes.InvalidRequest, replay.Code);
    }

    [Fact]
    public async Task CreateCheckout_IgnoresBuyerIdFromBuyerPayload()
    {
        var service = CreateService(new StubCartService(CreateCart()));

        var response = await service.CreateCheckout(new UcpCheckoutRequest
        {
            CartId = "cart-1",
            Context = new UcpCartContext { StoreId = "store-acme", Currency = "USD", Language = "en-US" },
            Buyer = new UcpCheckoutBuyer { Id = "forged-buyer", Email = "buyer@example.com" },
        }, TestContext.Current.CancellationToken);

        Assert.Equal("buyer-1", response.Checkout.Buyer.Id);
        Assert.Equal("buyer@example.com", response.Checkout.Buyer.Email);
    }

    [Fact]
    public async Task RestoreHandoff_AuthenticatedPayloadRejectsDifferentPlatformBuyer()
    {
        var cart = CreateCart();
        cart.BuyerId = "user-1";
        cart.OrganizationId = "org-1";
        cart.Addresses.Add(CreateShippingAddress());
        var httpContextAccessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        httpContextAccessor.HttpContext.User = CreateAuthenticatedPrincipal("user-1", "org-1");
        var service = new UcpCheckoutService(
            new StubCartService(cart),
            new StubDistributedCache(),
            new TestDistributedLockService(),
            httpContextAccessor,
            Options.Create(new UcpOptions
            {
                DefaultStoreId = "store-acme",
                DefaultCurrency = "USD",
                DefaultCultureName = "en-US",
                HandoffUrlTemplate = "https://storefront.example/checkout?ucp_session={token}",
            }),
            buyerContextAccessor: new UcpBuyerContextAccessor(httpContextAccessor));
        var handoff = await service.HandoffCheckout("cart-1", new UcpCheckoutRequest
        {
            Context = new UcpCartContext
            {
                BuyerId = "user-1",
                OrganizationId = "org-1",
                StoreId = "store-acme",
                Currency = "USD",
                Language = "en-US",
            },
        }, TestContext.Current.CancellationToken);
        var token = Uri.UnescapeDataString(handoff.Checkout.ContinueUrl.Split("ucp_session=").Last());
        httpContextAccessor.HttpContext.User = CreateAuthenticatedPrincipal("user-2", "org-1");

        var exception = await Assert.ThrowsAsync<UcpException>(() => service.RestoreHandoff(new UcpHandoffRestoreRequest
        {
            UcpSession = token,
        }, TestContext.Current.CancellationToken));

        Assert.Equal(ModuleConstants.ErrorCodes.BuyerContextMismatch, exception.Code);
        Assert.Equal(StatusCodes.Status403Forbidden, exception.StatusCode);
    }

    [Fact]
    public async Task RestoreHandoff_AuthenticatedPayloadRequiresPlatformBuyer()
    {
        var cart = CreateCart();
        cart.BuyerId = "user-1";
        cart.OrganizationId = "org-1";
        cart.Addresses.Add(CreateShippingAddress());
        var httpContextAccessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        httpContextAccessor.HttpContext.User = CreateAuthenticatedPrincipal("user-1", "org-1");
        var service = new UcpCheckoutService(
            new StubCartService(cart),
            new StubDistributedCache(),
            new TestDistributedLockService(),
            httpContextAccessor,
            Options.Create(new UcpOptions
            {
                DefaultStoreId = "store-acme",
                DefaultCurrency = "USD",
                DefaultCultureName = "en-US",
                HandoffUrlTemplate = "https://storefront.example/checkout?ucp_session={token}",
            }),
            buyerContextAccessor: new UcpBuyerContextAccessor(httpContextAccessor));
        var handoff = await service.HandoffCheckout("cart-1", new UcpCheckoutRequest
        {
            Context = new UcpCartContext
            {
                BuyerId = "user-1",
                OrganizationId = "org-1",
                StoreId = "store-acme",
                Currency = "USD",
                Language = "en-US",
            },
        }, TestContext.Current.CancellationToken);
        var token = Uri.UnescapeDataString(handoff.Checkout.ContinueUrl.Split("ucp_session=").Last());
        httpContextAccessor.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());

        var exception = await Assert.ThrowsAsync<UcpException>(() => service.RestoreHandoff(new UcpHandoffRestoreRequest
        {
            UcpSession = token,
        }, TestContext.Current.CancellationToken));

        Assert.Equal(ModuleConstants.ErrorCodes.IdentityRequired, exception.Code);
        Assert.Equal(StatusCodes.Status401Unauthorized, exception.StatusCode);
    }

    [Fact]
    public async Task RestoreHandoff_DoesNotMaskCacheProviderJsonExceptionAsInvalidSession()
    {
        var expected = new JsonException("cache provider failure");
        var service = CreateService(
            new StubCartService(CreateCart()),
            new StubDistributedCache(expected));

        var actual = await Assert.ThrowsAsync<JsonException>(() => service.RestoreHandoff(
            new UcpHandoffRestoreRequest { UcpSession = "valid-shape-token" },
            TestContext.Current.CancellationToken));

        Assert.Same(expected, actual);
    }

    [Theory]
    [InlineData(null, "miss", true)]
    [InlineData("{not-json", "corrupt", true)]
    [InlineData("{\"checkout_id\":\"checkout-1\",\"cart_id\":\"cart-1\",\"store_id\":\"store-acme\",\"currency\":\"USD\",\"culture_name\":\"en-US\",\"expires_at\":\"2000-01-01T00:00:00+00:00\"}", "expired", true)]
    [InlineData("{\"checkout_id\":\"checkout-1\",\"cart_id\":\"cart-1\",\"store_id\":\"store-acme\",\"currency\":\"USD\",\"culture_name\":\"en-US\",\"expires_at\":\"2099-01-01T00:00:00+00:00\"}", "hit", false)]
    public async Task RestoreHandoff_RecordsCacheReadOutcome(string payloadJson, string expectedOutcome, bool expectFailure)
    {
        const string parentSourceName = "VCST5544.Tests.CacheOutcome";
        const string token = "cache-outcome-token";
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = CreateActivityListener(parentSourceName, stopped);
        using var parentSource = new ActivitySource(parentSourceName);
        using var parent = parentSource.StartActivity("POST ucp/v1/internal/handoff/restore", ActivityKind.Server);
        Assert.NotNull(parent);

        var cache = new StubDistributedCache();
        if (payloadJson != null)
        {
            cache.SetRaw(GetHandoffCacheKey(token), payloadJson);
        }
        var service = CreateService(new StubCartService(CreateCart()), cache);

        var restore = () => service.RestoreHandoff(
            new UcpHandoffRestoreRequest { UcpSession = token },
            TestContext.Current.CancellationToken);
        if (expectFailure)
        {
            await Assert.ThrowsAsync<UcpException>(restore);
        }
        else
        {
            var response = await restore();
            Assert.Equal("cart-1", response.Checkout.CartId);
        }

        var dependency = Assert.Single(stopped, activity =>
            activity.DisplayName == "VC distributed-cache GetHandoffSession" &&
            activity.TraceId == parent.TraceId);
        Assert.Equal(expectedOutcome, dependency.GetTagItem("vc.dependency.outcome"));
    }

    [Fact]
    public async Task HandoffCheckout_AppliesAddressBeforeCreatingToken()
    {
        var cartService = new StubCartService(CreateCart());
        var service = CreateService(cartService);

        await service.HandoffCheckout("cart-1", new UcpCheckoutRequest
        {
            Context = new UcpCartContext { StoreId = "store-acme", Currency = "USD", Language = "en-US" },
            ShippingAddress = new UcpCheckoutAddress
            {
                FirstName = "Ada",
                LastName = "Buyer",
                Line1 = "1 Main St",
                City = "Seattle",
                PostalCode = "98101",
                CountryCode = "US",
            },
        }, TestContext.Current.CancellationToken);

        Assert.Single(cartService.AppliedCheckoutRequests);
        Assert.Equal("cart-1", cartService.AppliedCheckoutRequests[0].cartId);
        Assert.Equal("1 Main St", cartService.AppliedCheckoutRequests[0].request.ShippingAddress.Line1);
    }

    [Fact]
    public void AddressJson_UsesPostalCodeContractName()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new UcpCheckoutAddress
        {
            PostalCode = "98101",
        });

        Assert.Contains("\"postal_code\":\"98101\"", json);
        Assert.DoesNotContain("postalCode", json);
        Assert.DoesNotContain("zip", json);
    }

    [Fact]
    public async Task HandoffCheckout_AcceptsTopLevelContextAliases()
    {
        var cartService = new StubCartService(CreateCart());
        var service = CreateService(cartService);

        await service.HandoffCheckout("cart-1", new UcpCheckoutRequest
        {
            StoreId = "store-acme",
            Currency = "USD",
            Language = "en-US",
            BuyerId = "buyer-1",
            ShippingAddress = new UcpCheckoutAddress
            {
                FirstName = "Ada",
                LastName = "Buyer",
                Line1 = "1 Main St",
                City = "Seattle",
                PostalCode = "98101",
                CountryCode = "US",
            },
        }, TestContext.Current.CancellationToken);

        var context = cartService.AppliedCheckoutRequests[0].request.Context;

        Assert.Equal("store-acme", context.StoreId);
        Assert.Equal("USD", context.Currency);
        Assert.Equal("en-US", context.Language);
        Assert.Equal("buyer-1", context.BuyerId);
    }

    [Fact]
    public async Task UpdateCheckout_AppliesAddressAndReturnsCheckoutUpdatedMessage()
    {
        var cartService = new StubCartService(CreateCart());
        var service = CreateService(cartService);

        var response = await service.UpdateCheckout("cart-1", new UcpCheckoutRequest
        {
            Context = new UcpCartContext { StoreId = "store-acme", Currency = "USD", Language = "en-US" },
            ShippingAddress = new UcpCheckoutAddress
            {
                FirstName = "Grace",
                LastName = "Buyer",
                Line1 = "2 Main St",
                City = "Portland",
                PostalCode = "97201",
                CountryCode = "US",
            },
        }, TestContext.Current.CancellationToken);

        Assert.Single(cartService.AppliedCheckoutRequests);
        Assert.Equal("cart-1", cartService.AppliedCheckoutRequests[0].cartId);
        Assert.Equal("2 Main St", response.Checkout.ShippingAddress.Line1);
        Assert.Contains(response.Messages, x => x.Code == "checkout_updated");
    }

    [Fact]
    public async Task CreateCheckout_UsesAddressFromCartSnapshot()
    {
        var cart = CreateCart();
        cart.Addresses.Add(new UcpCartAddress
        {
            Id = "ship-1",
            AddressType = "shipping",
            FirstName = "Ada",
            LastName = "Buyer",
            Line1 = "1 Main St",
            City = "Seattle",
            PostalCode = "98101",
            CountryCode = "US",
        });
        var service = CreateService(new StubCartService(cart));

        var response = await service.CreateCheckout(new UcpCheckoutRequest
        {
            CartId = "cart-1",
            Context = new UcpCartContext { StoreId = "store-acme", Currency = "USD", Language = "en-US" },
        }, TestContext.Current.CancellationToken);

        Assert.Equal("1 Main St", response.Checkout.ShippingAddress.Line1);
        Assert.Contains(response.Messages, x => x.Code == "shipping_address_prefilled");
    }

    [Fact]
    public async Task CreateCheckout_WarnsWhenShippingPostalCodeIsMissing()
    {
        var cart = CreateCart();
        cart.Addresses.Add(new UcpCartAddress
        {
            Id = "ship-1",
            AddressType = "shipping",
            FirstName = "Ada",
            LastName = "Buyer",
            Line1 = "1 Main St",
            City = "Seattle",
            CountryCode = "US",
        });
        var service = CreateService(new StubCartService(cart));

        var response = await service.CreateCheckout(new UcpCheckoutRequest
        {
            CartId = "cart-1",
            Context = new UcpCartContext { StoreId = "store-acme", Currency = "USD", Language = "en-US" },
        }, TestContext.Current.CancellationToken);

        Assert.Contains(response.Messages, x => x.Code == "shipping_postal_code_missing" && x.Content.Contains("postal_code is missing", System.StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpdateCheckout_UsesRequestedPostalCodeWhenCartSnapshotOmitsIt()
    {
        var cart = CreateCart();
        cart.Addresses.Add(new UcpCartAddress
        {
            Id = "ship-1",
            AddressType = "shipping",
            FirstName = "Ada",
            LastName = "Buyer",
            Line1 = "2 Main St",
            City = "Bellevue",
            CountryCode = "US",
        });
        var service = CreateService(new StubCartService(cart));

        var response = await service.UpdateCheckout("cart-1", new UcpCheckoutRequest
        {
            Context = new UcpCartContext { StoreId = "store-acme", Currency = "USD", Language = "en-US" },
            ShippingAddress = new UcpCheckoutAddress
            {
                FirstName = "Ada",
                LastName = "Buyer",
                Line1 = "2 Main St",
                City = "Bellevue",
                PostalCode = "98004",
                CountryCode = "US",
            },
        }, TestContext.Current.CancellationToken);

        Assert.Equal("98004", response.Checkout.ShippingAddress.PostalCode);
        Assert.DoesNotContain(response.Messages, x => x.Code == "shipping_postal_code_missing");
    }

    [Fact]
    public async Task HandoffCheckout_RejectsAddressInNotes()
    {
        var service = CreateService(new StubCartService(CreateCart()));

        var exception = await Assert.ThrowsAsync<UcpException>(() => service.HandoffCheckout("cart-1", new UcpCheckoutRequest
        {
            Context = new UcpCartContext { StoreId = "store-acme", Currency = "USD", Language = "en-US" },
            Notes = "United States, Seattle, 1 Main St Apt 100",
        }, TestContext.Current.CancellationToken));

        Assert.Equal(ModuleConstants.ErrorCodes.InvalidRequest, exception.Code);
        Assert.Contains("shipping_address is required", exception.Message);
    }

    [Fact]
    public async Task CreateCheckout_RejectsSuppliedAddressWithoutPostalCode()
    {
        var service = CreateService(new StubCartService(CreateCart()));

        var exception = await Assert.ThrowsAsync<UcpException>(() => service.CreateCheckout(new UcpCheckoutRequest
        {
            CartId = "cart-1",
            Context = new UcpCartContext { StoreId = "store-acme", Currency = "USD", Language = "en-US" },
            ShippingAddress = new UcpCheckoutAddress
            {
                FirstName = "Jane",
                LastName = "Doe",
                Line1 = "1 Main St",
                City = "Seattle",
                CountryCode = "US",
            },
        }, TestContext.Current.CancellationToken));

        Assert.Equal(ModuleConstants.ErrorCodes.InvalidRequest, exception.Code);
        Assert.Contains("shipping_address.postal_code is required", exception.Message);
    }

    [Fact]
    public async Task CreateCheckout_RejectsSuppliedAddressWithoutRecipientName()
    {
        var service = CreateService(new StubCartService(CreateCart()));

        var exception = await Assert.ThrowsAsync<UcpException>(() => service.CreateCheckout(new UcpCheckoutRequest
        {
            CartId = "cart-1",
            Context = new UcpCartContext { StoreId = "store-acme", Currency = "USD", Language = "en-US" },
            ShippingAddress = new UcpCheckoutAddress
            {
                Line1 = "1 Main St",
                City = "Seattle",
                PostalCode = "98101",
                CountryCode = "US",
            },
        }, TestContext.Current.CancellationToken));

        Assert.Equal(ModuleConstants.ErrorCodes.InvalidRequest, exception.Code);
        Assert.Contains("shipping_address.first_name", exception.Message);
    }

    private static UcpCheckoutService CreateService(
        IUcpCartService cartService,
        IDistributedCache distributedCache = null,
        IDistributedLockService distributedLock = null)
    {
        var httpContextAccessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext(),
        };
        httpContextAccessor.HttpContext.TraceIdentifier = "trace-checkout";

        return new UcpCheckoutService(
            cartService,
            distributedCache ?? new StubDistributedCache(),
            distributedLock ?? new TestDistributedLockService(),
            httpContextAccessor,
            Options.Create(new UcpOptions
            {
                DefaultStoreId = "store-acme",
                DefaultCurrency = "USD",
                DefaultCultureName = "en-US",
                StorefrontOrigin = "https://storefront.example",
                HandoffUrlTemplate = "https://storefront.example/checkout?ucp_session={token}",
            }),
            buyerContextAccessor: new TestBuyerContextAccessor());
    }

    private static string GetHandoffCacheKey(string sessionToken)
    {
        var tokenHash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sessionToken));
        return "UCP:Handoff:" + Convert.ToHexString(tokenHash);
    }

    private static ActivityListener CreateActivityListener(string parentSourceName, ConcurrentQueue<Activity> stopped)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == UcpDiagnostics.ActivitySourceName || source.Name == parentSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => stopped.Enqueue(activity),
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static UcpCart CreateCart()
    {
        return new UcpCart
        {
            Id = "cart-1",
            StoreId = "store-acme",
            Currency = "USD",
            BuyerId = "buyer-1",
            LineItems =
            {
                new UcpCartLineItem
                {
                    Id = "line-1",
                    ProductId = "product-1",
                    Quantity = 1,
                },
            },
            Totals = new UcpCartTotals
            {
                Total = new UcpMoney { Amount = 1000, Currency = "USD", FormattedAmount = "$10.00" },
            },
        };
    }

    private static ClaimsPrincipal CreateAuthenticatedPrincipal(string userId, string organizationId)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", userId),
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim("organization_id", organizationId),
        ], "Bearer"));
    }

    private static UcpCartAddress CreateShippingAddress()
    {
        return new UcpCartAddress
        {
            Id = "ship-1",
            AddressType = "shipping",
            FirstName = "Ada",
            LastName = "Buyer",
            Line1 = "1 Main St",
            City = "Seattle",
            PostalCode = "98101",
            CountryCode = "US",
        };
    }

    private sealed class StubCartService : IUcpCartService
    {
        private readonly UcpCart _cart;

        public StubCartService(UcpCart cart)
        {
            _cart = cart;
        }

        public Task<UcpCartResponse> CreateCart(UcpCartRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new UcpCartResponse { Cart = _cart });
        }

        public Task<UcpCartListResponse> ListCarts(UcpCartListRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new UcpCartListResponse { Carts = { _cart } });
        }

        public Task<UcpCartResponse> GetCart(string cartId, UcpCartRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new UcpCartResponse { Cart = _cart });
        }

        public Task<UcpCartResponse> UpdateCart(string cartId, UcpCartRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new UcpCartResponse { Cart = _cart });
        }

        public List<(string cartId, UcpCheckoutRequest request)> AppliedCheckoutRequests { get; } = [];

        public Task<UcpCartResponse> ApplyCheckoutData(string cartId, UcpCheckoutRequest request, CancellationToken cancellationToken = default)
        {
            AppliedCheckoutRequests.Add((cartId, request));
            return Task.FromResult(new UcpCartResponse { Cart = _cart });
        }
    }

    private sealed class StubDistributedCache : IDistributedCache
    {
        private readonly Dictionary<string, byte[]> _items = [];
        private readonly System.Exception _getException;

        public StubDistributedCache(System.Exception getException = null)
        {
            _getException = getException;
        }

        public byte[] Get(string key)
        {
            if (_getException != null)
            {
                throw _getException;
            }

            return _items.GetValueOrDefault(key);
        }

        public Task<byte[]> GetAsync(string key, CancellationToken token = default)
        {
            if (_getException != null)
            {
                return Task.FromException<byte[]>(_getException);
            }

            return Task.FromResult(Get(key));
        }

        public void Refresh(string key)
        {
        }

        public Task RefreshAsync(string key, CancellationToken token = default)
        {
            return Task.CompletedTask;
        }

        public void Remove(string key)
        {
            _items.Remove(key);
        }

        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            Remove(key);
            return Task.CompletedTask;
        }

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            _items[key] = value;
        }

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Set(key, value, options);
            return Task.CompletedTask;
        }

        public void SetRaw(string key, string value)
        {
            _items[key] = Encoding.UTF8.GetBytes(value);
        }
    }

    private sealed class TestDistributedLockService : IDistributedLockService
    {
        public ConcurrentQueue<string> ResourceKeys { get; } = new();

        public T Execute<T>(
            string resourceKey,
            Func<T> resolver,
            TimeSpan? lockTimeout = null,
            TimeSpan? tryLockTimeout = null,
            TimeSpan? retryInterval = null,
            CancellationToken? cancellationToken = null)
        {
            ResourceKeys.Enqueue(resourceKey);
            return resolver();
        }

        public Task<T> ExecuteAsync<T>(
            string resourceKey,
            Func<Task<T>> resolver,
            TimeSpan? lockTimeout = null,
            TimeSpan? tryLockTimeout = null,
            TimeSpan? retryInterval = null,
            CancellationToken? cancellationToken = null)
        {
            ResourceKeys.Enqueue(resourceKey);
            return resolver();
        }
    }
}
