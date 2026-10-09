using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using VirtoCommerce.OrdersModule.Core.Model;
using VirtoCommerce.OrdersModule.Core.Model.Search;
using VirtoCommerce.OrdersModule.Core.Services;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Models;
using VirtoCommerce.UCP.Core.Options;
using VirtoCommerce.UCP.Core.Services;
using VirtoCommerce.UCP.Data.Services;
using Xunit;

namespace VirtoCommerce.UCP.Tests;

[Trait("Category", "Unit")]
public class UcpOrderServiceTests
{
    [Fact]
    public async Task TrackOrder_ByCartId_FindsOrderByShoppingCartIdWithoutXOrderSession()
    {
        var order = CreateOrder("order-1", "cart-1", "buyer-1");
        var orderSearchService = new StubCustomerOrderSearchService(order);
        var service = CreateService(orderSearchService: orderSearchService);

        var response = await service.TrackOrder(new UcpOrderTrackingRequest
        {
            CartId = "cart-1",
            Context = new UcpCartContext
            {
                BuyerId = "buyer-1",
                Language = "en-US",
            },
        }, TestContext.Current.CancellationToken);

        Assert.Equal("order-1", response.Order.Id);
        Assert.Equal("CO123", response.Order.Number);
        Assert.Equal("Completed", response.Order.Status);
        Assert.Equal("cart-1", response.Order.CartId);
        Assert.Equal("buyer-1", response.Order.BuyerId);
        Assert.Equal(129900, response.Order.Totals.Total.Amount);
        Assert.Single(response.Order.LineItems);
        Assert.Single(response.Order.Shipments);
        Assert.Single(response.Order.Payments);
        Assert.Equal("UPS", response.Order.Shipments[0].ShipmentMethodCode);
        Assert.Equal("1Z999", response.Order.Shipments[0].TrackingNumber);
        Assert.Equal("ReadyToShip", response.Order.Shipments[0].Status);
        Assert.True(response.Order.Shipments[0].Approved);
        Assert.Equal("Paid", response.Order.Payments[0].Status);
        Assert.Contains(response.Messages, x => x.Code == "shipment_tracking_available");

        var criteria = orderSearchService.Criteria.Single(x => x.HasParentOperation == false);
        Assert.Equal("buyer-1", criteria.CustomerId);
        Assert.Equal(50, criteria.Take);
        Assert.Equal("CreatedDate:desc", criteria.Sort);
        Assert.Equal(CustomerOrderResponseGroup.Full.ToString(), criteria.ResponseGroup);
    }

    [Fact]
    public async Task TrackOrder_ByCartId_ReturnsStructuredNotFound()
    {
        var orderSearchService = new StubCustomerOrderSearchService(CreateOrder("order-1", "another-cart", "buyer-1"));
        var service = CreateService(orderSearchService: orderSearchService);

        var exception = await Assert.ThrowsAsync<UcpException>(() => service.TrackOrder(new UcpOrderTrackingRequest
        {
            CartId = "missing-cart",
            Context = new UcpCartContext { BuyerId = "buyer-1" },
        }, TestContext.Current.CancellationToken));

        Assert.Equal(ModuleConstants.ErrorCodes.OrderNotFound, exception.Code);
        Assert.Equal(404, exception.StatusCode);
    }

    [Fact]
    public async Task TrackOrder_ByCartId_RejectsAnotherBuyer()
    {
        var order = CreateOrder("order-1", "cart-1", "storefront-guest");
        var orderSearchService = new StubCustomerOrderSearchService(order);
        var service = CreateService(orderSearchService: orderSearchService);

        var exception = await Assert.ThrowsAsync<UcpException>(() => service.TrackOrder(new UcpOrderTrackingRequest
        {
            CartId = "cart-1",
            Context = new UcpCartContext { BuyerId = "ucp-anonymous-original" },
        }, TestContext.Current.CancellationToken));

        Assert.Equal(404, exception.StatusCode);
        Assert.All(orderSearchService.Criteria, x => Assert.Equal("ucp-anonymous-original", x.CustomerId));
    }

    [Fact]
    public async Task TrackOrder_ByCartId_FindsOrderBeyondFirstPage()
    {
        var orders = Enumerable.Range(0, 51).Select(x => CreateOrder($"order-{x}", $"cart-{x}", "buyer-1")).ToArray();
        var searchService = new StubCustomerOrderSearchService(orders);
        var service = CreateService(orderSearchService: searchService);

        var response = await service.TrackOrder(new UcpOrderTrackingRequest
        {
            CartId = "cart-50",
            Context = new UcpCartContext { BuyerId = "buyer-1" },
        }, TestContext.Current.CancellationToken);

        Assert.Equal("order-50", response.Order.Id);
        Assert.Equal([0, 50], searchService.Criteria.Where(x => x.HasParentOperation == false).Select(x => x.Skip));
        Assert.All(searchService.Criteria, x => Assert.Equal("buyer-1", x.CustomerId));
    }

    [Fact]
    public async Task TrackOrder_ByCartId_WithoutPlacedAfter_ReturnsOnlyTheNewestOrder()
    {
        var orders = Enumerable.Range(0, 56).Select(x => CreateOrder($"order-{x}", $"cart-{x}", "buyer-1")).ToArray();
        orders[3].ShoppingCartId = "cart-split";
        orders[49].ShoppingCartId = "cart-split";
        orders[52].ShoppingCartId = "CART-SPLIT";
        var searchService = new StubCustomerOrderSearchService(orders);
        var service = CreateService(orderSearchService: searchService);

        var response = await service.TrackOrder(new UcpOrderTrackingRequest
        {
            CartId = "cart-split",
            Context = new UcpCartContext { BuyerId = "buyer-1" },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(["order-3"], response.Orders.Select(x => x.Id));
        Assert.Equal("order-3", response.Order.Id);
        Assert.Equal([0], searchService.Criteria.Where(x => x.HasParentOperation == false).Select(x => x.Skip));
    }

    [Fact]
    public async Task TrackOrder_ByCartId_WithPlacedAfter_ReturnsOrdersPlacedSinceThenNewestFirst()
    {
        var placedAfter = new DateTimeOffset(2026, 6, 16, 10, 0, 0, TimeSpan.Zero);
        var newest = CreateOrder("order-newest", "cart-1", "buyer-1");
        newest.CreatedDate = placedAfter.UtcDateTime.AddMinutes(30);
        var other = CreateOrder("order-other", "cart-2", "buyer-1");
        other.CreatedDate = placedAfter.UtcDateTime.AddMinutes(20);
        var later = CreateOrder("order-later", "cart-1", "buyer-1");
        later.CreatedDate = placedAfter.UtcDateTime.AddMinutes(10);
        var before = CreateOrder("order-before", "cart-1", "buyer-1");
        before.CreatedDate = placedAfter.UtcDateTime.AddMinutes(-10);
        var searchService = new StubCustomerOrderSearchService(newest, other, later, before);
        var service = CreateService(orderSearchService: searchService);

        var response = await service.TrackOrder(new UcpOrderTrackingRequest
        {
            CartId = "cart-1",
            PlacedAfter = placedAfter,
            Context = new UcpCartContext { BuyerId = "buyer-1" },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(["order-newest", "order-later"], response.Orders.Select(x => x.Id));
        Assert.Equal("order-newest", response.Order.Id);
        Assert.All(searchService.Criteria, x => Assert.Equal(placedAfter.UtcDateTime, x.StartDate));
    }

    [Fact]
    public async Task TrackOrder_ByCartId_WithPlacedAfter_WhenNoOrderPlacedYet_ReturnsNotFound()
    {
        var placedAfter = new DateTimeOffset(2026, 6, 16, 10, 0, 0, TimeSpan.Zero);
        var older = CreateOrder("order-older", "cart-1", "buyer-1");
        older.CreatedDate = placedAfter.UtcDateTime.AddDays(-3);
        var service = CreateService(orderSearchService: new StubCustomerOrderSearchService(older));

        var exception = await Assert.ThrowsAsync<UcpException>(() => service.TrackOrder(new UcpOrderTrackingRequest
        {
            CartId = "cart-1",
            PlacedAfter = placedAfter,
            Context = new UcpCartContext { BuyerId = "buyer-1" },
        }, TestContext.Current.CancellationToken));

        Assert.Equal(ModuleConstants.ErrorCodes.OrderNotFound, exception.Code);
        Assert.Equal(404, exception.StatusCode);
        Assert.Equal($"No order was placed from cart 'cart-1' at or after {placedAfter.UtcDateTime:O}.", exception.Message);
    }

    [Fact]
    public async Task TrackOrder_ByCartId_WithPlacedAfter_ReturnsChildOrdersWithParentOrderId()
    {
        var placedAfter = new DateTimeOffset(2026, 6, 16, 8, 0, 0, TimeSpan.Zero);
        var main = CreateOrder("order-main", "cart-1", "buyer-1");
        var child1 = CreateOrder("order-child-1", "cart-1", "buyer-1");
        child1.ParentOperationId = main.Id;
        child1.CreatedDate = main.CreatedDate.AddMinutes(10);
        var child2 = CreateOrder("order-child-2", "cart-1", "buyer-1");
        child2.ParentOperationId = main.Id;
        child2.CreatedDate = main.CreatedDate.AddMinutes(20);
        var searchService = new StubCustomerOrderSearchService(child2, child1, main);
        var service = CreateService(orderSearchService: searchService);

        var response = await service.TrackOrder(new UcpOrderTrackingRequest
        {
            CartId = "cart-1",
            PlacedAfter = placedAfter,
            Context = new UcpCartContext { BuyerId = "buyer-1" },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(["order-child-2", "order-child-1", "order-main"], response.Orders.Select(x => x.Id));
        Assert.Equal("order-main", response.Order.Id);
        Assert.Equal(["order-main", "order-main", null], response.Orders.Select(x => x.ParentOrderId));
        Assert.All(searchService.Criteria, x => Assert.Null(x.HasParentOperation));
    }

    [Fact]
    public async Task TrackOrder_ByCartId_WithoutPlacedAfter_ReturnsNewestOrderWithItsChildOrders()
    {
        var mainA = CreateOrder("order-a", "cart-1", "buyer-1");
        mainA.CreatedDate = mainA.CreatedDate.AddHours(-2);
        var childA = CreateOrder("order-a1", "cart-1", "buyer-1");
        childA.ParentOperationId = mainA.Id;
        childA.CreatedDate = mainA.CreatedDate.AddMinutes(5);
        var mainB = CreateOrder("order-b", "cart-1", "buyer-1");
        var childB = CreateOrder("order-b1", "cart-1", "buyer-1");
        childB.ParentOperationId = mainB.Id;
        childB.CreatedDate = mainB.CreatedDate.AddMinutes(5);
        var service = CreateService(orderSearchService: new StubCustomerOrderSearchService(childB, mainB, childA, mainA));

        var response = await service.TrackOrder(new UcpOrderTrackingRequest
        {
            CartId = "cart-1",
            Context = new UcpCartContext { BuyerId = "buyer-1" },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(["order-b1", "order-b"], response.Orders.Select(x => x.Id));
        Assert.Equal("order-b", response.Order.Id);
    }

    [Fact]
    public async Task TrackOrder_ByCartId_WhenPagesShiftDuringScan_ReturnsEachOrderOnce()
    {
        // A new order created mid-scan pushes the last row of page 1 onto page 2,
        // so the stub serves the same order at index 49 and index 50.
        var orders = Enumerable.Range(0, 50).Select(x => CreateOrder($"order-{x}", $"cart-{x}", "buyer-1")).ToList();
        orders[3].ShoppingCartId = "cart-split";
        orders[49].ShoppingCartId = "cart-split";
        orders.Add(CreateOrder("order-49", "cart-split", "buyer-1"));
        var searchService = new StubCustomerOrderSearchService(orders.ToArray());
        var service = CreateService(orderSearchService: searchService);

        var response = await service.TrackOrder(new UcpOrderTrackingRequest
        {
            CartId = "cart-split",
            PlacedAfter = new DateTimeOffset(2026, 6, 16, 8, 0, 0, TimeSpan.Zero),
            Context = new UcpCartContext { BuyerId = "buyer-1" },
        }, TestContext.Current.CancellationToken);

        Assert.Equal([0, 50], searchService.Criteria.Select(x => x.Skip));
        Assert.Equal(["order-3", "order-49"], response.Orders.Select(x => x.Id));
    }

    [Fact]
    public async Task TrackOrder_ByCartId_WithSingleOrder_ReturnsItInOrders()
    {
        var service = CreateService(orderSearchService: new StubCustomerOrderSearchService(
            CreateOrder("order-1", "cart-1", "buyer-1"),
            CreateOrder("order-2", "cart-2", "buyer-1")));

        var response = await service.TrackOrder(new UcpOrderTrackingRequest
        {
            CartId = "cart-1",
            Context = new UcpCartContext { BuyerId = "buyer-1" },
        }, TestContext.Current.CancellationToken);

        Assert.Equal("order-1", Assert.Single(response.Orders).Id);
        Assert.Equal("order-1", response.Order.Id);
    }

    [Fact]
    public async Task TrackOrder_ByOrderNumber_ReturnsOnlyThatOrderInOrders()
    {
        var order = CreateOrder("order-1", "cart-1", "buyer-1");
        order.Number = "CO-777";
        var other = CreateOrder("order-2", "cart-1", "buyer-1");
        var service = CreateService(orderSearchService: new StubCustomerOrderSearchService(order, other));

        var response = await service.TrackOrder(new UcpOrderTrackingRequest
        {
            OrderNumber = "CO-777",
            Context = new UcpCartContext { BuyerId = "buyer-1" },
        }, TestContext.Current.CancellationToken);

        Assert.Equal("order-1", Assert.Single(response.Orders).Id);
        Assert.Equal("order-1", response.Order.Id);
    }

    [Fact]
    public async Task TrackOrder_ByCartId_RejectsAnotherOrganization()
    {
        var order = CreateOrder("order-1", "cart-1", "buyer-1");
        order.OrganizationId = "org-1";
        var service = CreateService(orderSearchService: new StubCustomerOrderSearchService(order));

        var exception = await Assert.ThrowsAsync<UcpException>(() => service.TrackOrder(new UcpOrderTrackingRequest
        {
            CartId = "cart-1",
            Context = new UcpCartContext { BuyerId = "buyer-1", OrganizationId = "org-2" },
        }, TestContext.Current.CancellationToken));

        Assert.Equal(404, exception.StatusCode);
    }

    [Fact]
    public async Task TrackOrder_ByOrderId_UsesOrdersService()
    {
        var orderService = new StubCustomerOrderService(CreateOrder("order-1", "cart-1", "buyer-1"));
        var service = CreateService(orderService: orderService);

        var response = await service.TrackOrder(new UcpOrderTrackingRequest
        {
            OrderId = "order-1",
            Context = new UcpCartContext { BuyerId = "buyer-1" },
        }, TestContext.Current.CancellationToken);

        Assert.Equal("order-1", response.Order.Id);
        Assert.Equal("cart-1", response.Order.CartId);
        Assert.Equal("order-1", orderService.RequestedIds.Single());
    }

    [Fact]
    public async Task TrackOrder_ByOrderId_ReturnsNotFoundWhenBuyerDoesNotMatch()
    {
        var orderService = new StubCustomerOrderService(CreateOrder("order-1", "cart-1", "buyer-1"));
        var service = CreateService(orderService: orderService);

        var exception = await Assert.ThrowsAsync<UcpException>(() => service.TrackOrder(new UcpOrderTrackingRequest
        {
            OrderId = "order-1",
            Context = new UcpCartContext { BuyerId = "buyer-2" },
        }, TestContext.Current.CancellationToken));

        Assert.Equal(ModuleConstants.ErrorCodes.OrderNotFound, exception.Code);
        Assert.Equal(404, exception.StatusCode);
    }

    [Fact]
    public async Task TrackOrder_ByOrderId_ReturnsNotFoundWithoutBuyerScope()
    {
        var orderService = new StubCustomerOrderService(CreateOrder("order-1", "cart-1", "buyer-1"));
        var service = CreateService(orderService: orderService);

        var exception = await Assert.ThrowsAsync<UcpException>(() => service.TrackOrder(new UcpOrderTrackingRequest
        {
            OrderId = "order-1",
        }, TestContext.Current.CancellationToken));

        Assert.Equal(ModuleConstants.ErrorCodes.OrderNotFound, exception.Code);
        Assert.Equal(404, exception.StatusCode);
        Assert.Empty(orderService.RequestedIds);
    }

    [Fact]
    public async Task TrackOrder_ByOrderNumber_SearchesWithinBuyerScope()
    {
        var orderSearchService = new StubCustomerOrderSearchService(
            CreateOrder("order-1", "cart-1", "buyer-1"),
            CreateOrder("order-2", "cart-2", "buyer-2"));
        var service = CreateService(orderSearchService: orderSearchService);

        var response = await service.TrackOrder(new UcpOrderTrackingRequest
        {
            OrderNumber = "CO123",
            Context = new UcpCartContext { BuyerId = "buyer-2" },
        }, TestContext.Current.CancellationToken);

        Assert.Equal("order-2", response.Order.Id);
        var criteria = orderSearchService.Criteria.Single();
        Assert.Equal("buyer-2", criteria.CustomerId);
        Assert.Equal("CO123", criteria.Number);
        Assert.Equal(CustomerOrderResponseGroup.Full.ToString(), criteria.ResponseGroup);
    }

    [Fact]
    public async Task TrackOrder_ByOrderNumber_ReturnsNotFoundWithoutBuyerScope()
    {
        var orderSearchService = new StubCustomerOrderSearchService(CreateOrder("order-1", "cart-1", "buyer-1"));
        var service = CreateService(orderSearchService: orderSearchService);

        var exception = await Assert.ThrowsAsync<UcpException>(() => service.TrackOrder(new UcpOrderTrackingRequest
        {
            OrderNumber = "CO123",
        }, TestContext.Current.CancellationToken));

        Assert.Equal(ModuleConstants.ErrorCodes.OrderNotFound, exception.Code);
        Assert.Equal(404, exception.StatusCode);
        Assert.Empty(orderSearchService.Criteria);
    }

    [Fact]
    public async Task TrackOrder_ComputesLineTotalsWhenExtendedPriceIsMissing()
    {
        var order = CreateOrder("order-1", "cart-1", "buyer-1");
        order.SubTotal = 2598m;
        order.Total = 2598m;
        var orderItem = order.Items.Single();
        orderItem.Quantity = 2;
        orderItem.ExtendedPrice = 0m;
        var orderService = new StubCustomerOrderService(order);
        var service = CreateService(orderService: orderService);

        var response = await service.TrackOrder(new UcpOrderTrackingRequest
        {
            OrderId = "order-1",
            Context = new UcpCartContext { BuyerId = "buyer-1" },
        }, TestContext.Current.CancellationToken);

        var lineItem = Assert.Single(response.Order.LineItems);
        Assert.Equal(129900, lineItem.PlacedPrice.Amount);
        Assert.Equal(259800, lineItem.LineTotal.Amount);
    }

    private static UcpOrderService CreateService(
        ICustomerOrderService orderService = null,
        ICustomerOrderSearchService orderSearchService = null)
    {
        var httpContextAccessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext(),
        };
        httpContextAccessor.HttpContext.TraceIdentifier = "trace-order";

        return new UcpOrderService(
            orderService ?? new StubCustomerOrderService(),
            orderSearchService ?? new StubCustomerOrderSearchService(),
            httpContextAccessor,
            Options.Create(new UcpOptions
            {
                DefaultCultureName = "en-US",
            }),
            new TestBuyerContextAccessor());
    }

    private static CustomerOrder CreateOrder(string id, string cartId, string buyerId)
    {
        var address = new Address
        {
            Key = "addr-1",
            Name = "Ada Buyer",
            FirstName = "Ada",
            LastName = "Buyer",
            Line1 = "1 Main St",
            City = "Seattle",
            CountryCode = "US",
            CountryName = "United States",
            RegionId = "WA",
            RegionName = "Washington",
            PostalCode = "98101",
            Phone = "555-0100",
            Email = "ada@example.test",
        };

        return new CustomerOrder
        {
            Id = id,
            Number = "CO123",
            ShoppingCartId = cartId,
            StoreId = "store-acme",
            CustomerId = buyerId,
            CustomerName = "Ada Buyer",
            CreatedDate = DateTime.Parse("2026-06-16T09:00:00Z").ToUniversalTime(),
            Status = "Completed",
            Currency = "USD",
            SubTotal = 1299m,
            Total = 1299m,
            Items =
            [
                new LineItem
                {
                    Id = "line-1",
                    ImageUrl = "https://example.test/item.png",
                    Name = "Samsung Galaxy S26",
                    ProductId = "product-1",
                    Quantity = 1,
                    Sku = "S26-BLK",
                    Currency = "USD",
                    Price = 1299m,
                    PlacedPrice = 1299m,
                    ExtendedPrice = 1299m,
                },
            ],
            Shipments =
            [
                new Shipment
                {
                    ShipmentMethodCode = "UPS",
                    ShipmentMethodOption = "Ground",
                    TrackingNumber = "1Z999",
                    TrackingUrl = "https://track.example.test/1Z999",
                    Number = "SHIP123",
                    Status = "ReadyToShip",
                    IsApproved = true,
                    DeliveryDate = DateTime.Parse("2026-06-20T09:00:00Z").ToUniversalTime(),
                    Currency = "USD",
                    DeliveryAddress = address,
                },
            ],
            InPayments =
            [
                new PaymentIn
                {
                    Id = "payment-1",
                    Number = "PAY123",
                    IsApproved = true,
                    Status = "Paid",
                    GatewayCode = "test",
                    Currency = "USD",
                    BillingAddress = address,
                },
            ],
        };
    }

    private sealed class StubCustomerOrderService : ICustomerOrderService
    {
        private readonly List<CustomerOrder> _orders;

        public StubCustomerOrderService(params CustomerOrder[] orders)
        {
            _orders = orders.ToList();
        }

        public List<string> RequestedIds { get; } = [];

        public Task<IList<CustomerOrder>> GetAsync(IList<string> ids, string responseGroup, bool clone)
        {
            foreach (var id in ids)
            {
                RequestedIds.Add(id);
            }

            return Task.FromResult<IList<CustomerOrder>>(_orders.Where(order => ids.Contains(order.Id)).ToList());
        }

        public Task<IList<CustomerOrder>> GetByOuterIdsAsync(IList<string> outerIds, string responseGroup, bool clone)
        {
            return Task.FromResult<IList<CustomerOrder>>(new List<CustomerOrder>());
        }

        public Task SaveChangesAsync(IList<CustomerOrder> models)
        {
            return Task.CompletedTask;
        }

        public Task DeleteAsync(IList<string> ids, bool softDelete)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class StubCustomerOrderSearchService : ICustomerOrderSearchService
    {
        private readonly List<CustomerOrder> _orders;

        public StubCustomerOrderSearchService(params CustomerOrder[] orders)
        {
            _orders = orders.ToList();
        }

        public List<CustomerOrderSearchCriteria> Criteria { get; } = [];

        public Task<CustomerOrderSearchResult> SearchAsync(CustomerOrderSearchCriteria criteria, bool clone)
        {
            Criteria.Add(new CustomerOrderSearchCriteria
            {
                CustomerId = criteria.CustomerId,
                OrganizationId = criteria.OrganizationId,
                Number = criteria.Number,
                StartDate = criteria.StartDate,
                HasParentOperation = criteria.HasParentOperation,
                ParentOperationId = criteria.ParentOperationId,
                Take = criteria.Take,
                Skip = criteria.Skip,
                Sort = criteria.Sort,
                ResponseGroup = criteria.ResponseGroup,
            });

            var results = _orders
                .Where(order => string.IsNullOrWhiteSpace(criteria.CustomerId) || order.CustomerId == criteria.CustomerId)
                .Where(order => string.IsNullOrWhiteSpace(criteria.OrganizationId) || order.OrganizationId == criteria.OrganizationId)
                .Where(order => string.IsNullOrWhiteSpace(criteria.Number) || order.Number == criteria.Number)
                .Where(order => criteria.StartDate == null || order.CreatedDate >= criteria.StartDate)
                .Where(order => criteria.ParentOperationId == null || order.ParentOperationId == criteria.ParentOperationId)
                .Where(order => criteria.HasParentOperation == null || (order.ParentOperationId != null) == criteria.HasParentOperation)
                .ToList();

            return Task.FromResult(new CustomerOrderSearchResult
            {
                Results = results.Skip(criteria.Skip).Take(criteria.Take > 0 ? criteria.Take : _orders.Count).ToList(),
                TotalCount = results.Count,
            });
        }
    }
}
