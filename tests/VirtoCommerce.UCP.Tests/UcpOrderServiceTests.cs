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
    public async Task TrackOrder_ByCartId_WithPlacedAfter_ReturnsOrdersPlacedSinceThenOldestFirst()
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

        Assert.Equal(["order-later", "order-newest"], response.Orders.Select(x => x.Id));
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

        Assert.Equal(["order-main", "order-child-1", "order-child-2"], response.Orders.Select(x => x.Id));
        Assert.Equal("order-main", response.Order.Id);
        Assert.Equal([null, "order-main", "order-main"], response.Orders.Select(x => x.ParentOrderId));
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

        Assert.Equal(["order-b", "order-b1"], response.Orders.Select(x => x.Id));
        Assert.Equal("order-b", response.Order.Id);
    }

    [Fact]
    public async Task TrackOrder_ByCartId_WithPlacedAfter_WhenMainAndChildShareCreatedDate_ReturnsMainFirst()
    {
        var main = CreateOrder("order-main", "cart-1", "buyer-1");
        var child = CreateOrder("order-child", "cart-1", "buyer-1");
        child.ParentOperationId = main.Id;
        var service = CreateService(orderSearchService: new StubCustomerOrderSearchService(child, main));

        var response = await service.TrackOrder(new UcpOrderTrackingRequest
        {
            CartId = "cart-1",
            PlacedAfter = new DateTimeOffset(2026, 6, 16, 8, 0, 0, TimeSpan.Zero),
            Context = new UcpCartContext { BuyerId = "buyer-1" },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(["order-main", "order-child"], response.Orders.Select(x => x.Id));
        Assert.Equal("order-main", response.Order.Id);
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

    [Fact]
    public async Task ListOrders_ReturnsTheBuyersOrdersNewestFirstWithPaidTotalAndAmountDue()
    {
        var paid = CreateListedOrder("order-paid", "buyer-1", "2026-06-16T09:00:00Z", total: 1299m, approvedPayment: 1299m);
        var due = CreateListedOrder("order-due", "buyer-1", "2026-06-18T09:00:00Z", total: 500m, pendingPayment: 500m);
        var foreign = CreateListedOrder("order-foreign", "buyer-2", "2026-06-19T09:00:00Z", total: 10m);
        var searchService = new StubCustomerOrderSearchService(foreign, due, paid);
        var service = CreateService(orderSearchService: searchService);

        var response = await service.ListOrders(new UcpOrderListRequest
        {
            Context = new UcpCartContext { BuyerId = "buyer-1", StoreId = "store-acme" },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(["order-due", "order-paid"], response.Orders.Select(x => x.Id));
        Assert.Equal(50000, response.Orders[0].Totals.AmountDue.Amount);
        Assert.Equal(0, response.Orders[0].Totals.PaidTotal.Amount);
        Assert.Equal(50000, response.Orders[0].Payments.Single().Amount.Amount);
        Assert.Equal(0, response.Orders[1].Totals.AmountDue.Amount);
        Assert.Equal(129900, response.Orders[1].Totals.PaidTotal.Amount);
        Assert.False(response.Pagination.HasNextPage);
        Assert.Null(response.Pagination.Cursor);

        var criteria = searchService.Criteria.Single();
        Assert.Equal("buyer-1", criteria.CustomerId);
        Assert.Equal(["store-acme"], criteria.StoreIds);
        Assert.Equal("CreatedDate:desc", criteria.Sort);
    }

    [Fact]
    public async Task ListOrders_UnpaidOnly_KeepsOrdersWithAnAmountDueAndLeavesOutCancelledOnes()
    {
        var paid = CreateListedOrder("order-paid", "buyer-1", "2026-06-16T09:00:00Z", total: 100m, approvedPayment: 100m);
        var partly = CreateListedOrder("order-partly", "buyer-1", "2026-06-17T09:00:00Z", total: 100m, approvedPayment: 40m);
        var cancelled = CreateListedOrder("order-cancelled", "buyer-1", "2026-06-18T09:00:00Z", total: 100m);
        cancelled.IsCancelled = true;
        var service = CreateService(orderSearchService: new StubCustomerOrderSearchService(cancelled, partly, paid));

        var response = await service.ListOrders(new UcpOrderListRequest
        {
            Context = new UcpCartContext { BuyerId = "buyer-1" },
            UnpaidOnly = true,
        }, TestContext.Current.CancellationToken);

        var order = Assert.Single(response.Orders);
        Assert.Equal("order-partly", order.Id);
        Assert.Equal(6000, order.Totals.AmountDue.Amount);
        Assert.Equal("60.00 USD", order.Totals.AmountDue.FormattedAmount);
        Assert.Equal("40.00 USD", order.Totals.PaidTotal.FormattedAmount);
        Assert.Equal(0, response.Orders.Count(x => x.Id == "order-cancelled"));
        Assert.Contains(response.Messages, x => x.Code == "total_count_before_payment_filter");
    }

    [Fact]
    public async Task ListOrders_PagesWithTheReturnedCursor()
    {
        var orders = Enumerable.Range(1, 3)
            .Select(i => CreateListedOrder($"order-{i}", "buyer-1", $"2026-06-1{i}T09:00:00Z", total: 10m))
            .Reverse()
            .ToArray();
        var service = CreateService(orderSearchService: new StubCustomerOrderSearchService(orders));

        var first = await service.ListOrders(new UcpOrderListRequest
        {
            Context = new UcpCartContext { BuyerId = "buyer-1" },
            Pagination = new UcpPaginationRequest { Limit = 2 },
        }, TestContext.Current.CancellationToken);
        var second = await service.ListOrders(new UcpOrderListRequest
        {
            Context = new UcpCartContext { BuyerId = "buyer-1" },
            Pagination = new UcpPaginationRequest { Limit = 2, Cursor = first.Pagination.Cursor },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(["order-3", "order-2"], first.Orders.Select(x => x.Id));
        Assert.True(first.Pagination.HasNextPage);
        Assert.Equal("2", first.Pagination.Cursor);
        Assert.Equal(3, first.Pagination.TotalCount);
        Assert.Equal(["order-1"], second.Orders.Select(x => x.Id));
        Assert.False(second.Pagination.HasNextPage);
    }

    [Fact]
    public async Task ListOrders_PassesStatusesAndDatesToTheSearch()
    {
        var searchService = new StubCustomerOrderSearchService();
        var service = CreateService(orderSearchService: searchService);
        var after = DateTimeOffset.Parse("2026-06-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var before = DateTimeOffset.Parse("2026-06-30T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        await service.ListOrders(new UcpOrderListRequest
        {
            Context = new UcpCartContext { BuyerId = "buyer-1" },
            Statuses = ["New", " ", "Processing"],
            PlacedAfter = after,
            PlacedBefore = before,
        }, TestContext.Current.CancellationToken);

        var criteria = searchService.Criteria.Single();
        Assert.Equal(["New", "Processing"], criteria.Statuses);
        Assert.Equal(after.UtcDateTime, criteria.StartDate);
        Assert.Equal(before.UtcDateTime, criteria.EndDate);
    }

    [Fact]
    public async Task ListOrders_WithoutBuyer_ReturnsIdentityRequired()
    {
        var service = CreateService();

        var exception = await Assert.ThrowsAsync<UcpException>(() => service.ListOrders(new UcpOrderListRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(ModuleConstants.ErrorCodes.IdentityRequired, exception.Code);
    }

    [Fact]
    public async Task ListOrders_RejectsACursorItDidNotIssue()
    {
        var service = CreateService();

        var exception = await Assert.ThrowsAsync<UcpException>(() => service.ListOrders(new UcpOrderListRequest
        {
            Context = new UcpCartContext { BuyerId = "buyer-1" },
            Pagination = new UcpPaginationRequest { Cursor = "abc" },
        }, TestContext.Current.CancellationToken));

        Assert.Equal(ModuleConstants.ErrorCodes.InvalidRequest, exception.Code);
    }

    [Fact]
    public async Task ListOrders_PaidOnly_KeepsFullyPaidOrdersAndLeavesOutCancelledAndPartlyPaidOnes()
    {
        var paid = CreateListedOrder("order-paid", "buyer-1", "2026-06-16T09:00:00Z", total: 100m, approvedPayment: 100m);
        var partly = CreateListedOrder("order-partly", "buyer-1", "2026-06-17T09:00:00Z", total: 100m, approvedPayment: 40m);
        var pending = CreateListedOrder("order-pending", "buyer-1", "2026-06-18T09:00:00Z", total: 100m, pendingPayment: 100m);
        var cancelled = CreateListedOrder("order-cancelled", "buyer-1", "2026-06-19T09:00:00Z", total: 100m, approvedPayment: 100m);
        cancelled.IsCancelled = true;
        var service = CreateService(orderSearchService: new StubCustomerOrderSearchService(cancelled, pending, partly, paid));

        var response = await service.ListOrders(new UcpOrderListRequest
        {
            Context = new UcpCartContext { BuyerId = "buyer-1" },
            PaidOnly = true,
        }, TestContext.Current.CancellationToken);

        var order = Assert.Single(response.Orders);
        Assert.Equal("order-paid", order.Id);
        Assert.Equal(0, order.Totals.AmountDue.Amount);
        Assert.Equal("100.00 USD", order.Totals.PaidTotal.FormattedAmount);
        Assert.Contains(response.Messages, x => x.Code == "total_count_before_payment_filter");
    }

    [Fact]
    public async Task ListOrders_RejectsUnpaidOnlyTogetherWithPaidOnly()
    {
        var service = CreateService();

        var exception = await Assert.ThrowsAsync<UcpException>(() => service.ListOrders(new UcpOrderListRequest
        {
            Context = new UcpCartContext { BuyerId = "buyer-1" },
            UnpaidOnly = true,
            PaidOnly = true,
        }, TestContext.Current.CancellationToken));

        Assert.Equal(ModuleConstants.ErrorCodes.InvalidRequest, exception.Code);
    }

    [Fact]
    public async Task ListOrders_WithoutStoreId_UsesTheDefaultStore()
    {
        var searchService = new StubCustomerOrderSearchService();
        var service = CreateService(orderSearchService: searchService, defaultStoreId: "store-default");

        await service.ListOrders(new UcpOrderListRequest
        {
            Context = new UcpCartContext { BuyerId = "buyer-1" },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(["store-default"], searchService.Criteria.Single().StoreIds);
    }

    [Fact]
    public async Task ListOrders_WithoutPaymentFilter_ReadsOnlyTheRequestedPage()
    {
        var searchService = new StubCustomerOrderSearchService();
        var service = CreateService(orderSearchService: searchService);

        await service.ListOrders(new UcpOrderListRequest
        {
            Context = new UcpCartContext { BuyerId = "buyer-1" },
            Pagination = new UcpPaginationRequest { Limit = 5 },
        }, TestContext.Current.CancellationToken);
        await service.ListOrders(new UcpOrderListRequest
        {
            Context = new UcpCartContext { BuyerId = "buyer-1" },
            UnpaidOnly = true,
            Pagination = new UcpPaginationRequest { Limit = 5 },
        }, TestContext.Current.CancellationToken);

        Assert.Equal([5, 50], searchService.Criteria.Select(x => x.Take));
    }

    [Fact]
    public async Task ListOrders_NamesTheStandardOrderCapability()
    {
        var service = CreateService(orderSearchService: new StubCustomerOrderSearchService(
            CreateListedOrder("order-1", "buyer-1", "2026-06-16T09:00:00Z", total: 10m)));

        var response = await service.ListOrders(new UcpOrderListRequest
        {
            Context = new UcpCartContext { BuyerId = "buyer-1" },
        }, TestContext.Current.CancellationToken);

        Assert.Equal([ModuleConstants.Discovery.Order], response.Ucp.Capabilities.Keys);
    }

    [Fact]
    public async Task ListOrders_WithBothPaymentFiltersAndNoBuyer_ReturnsIdentityRequired()
    {
        var service = CreateService();

        var exception = await Assert.ThrowsAsync<UcpException>(() => service.ListOrders(new UcpOrderListRequest
        {
            UnpaidOnly = true,
            PaidOnly = true,
        }, TestContext.Current.CancellationToken));

        Assert.Equal(ModuleConstants.ErrorCodes.IdentityRequired, exception.Code);
    }

    [Fact]
    public async Task ListOrders_PaymentFilters_DoNotDependOnTheMappedOrder()
    {
        var paid = CreateListedOrder("order-paid", "buyer-1", "2026-06-16T09:00:00Z", total: 100m, approvedPayment: 100m);
        var due = CreateListedOrder("order-due", "buyer-1", "2026-06-17T09:00:00Z", total: 100m);
        var service = new OrderServiceWithoutTotals(new StubCustomerOrderSearchService(due, paid));

        var unpaid = await service.ListOrders(new UcpOrderListRequest
        {
            Context = new UcpCartContext { BuyerId = "buyer-1" },
            UnpaidOnly = true,
        }, TestContext.Current.CancellationToken);
        var paidOnly = await service.ListOrders(new UcpOrderListRequest
        {
            Context = new UcpCartContext { BuyerId = "buyer-1" },
            PaidOnly = true,
        }, TestContext.Current.CancellationToken);

        Assert.Equal(["order-due"], unpaid.Orders.Select(x => x.Id));
        Assert.Equal(["order-paid"], paidOnly.Orders.Select(x => x.Id));
    }

    [Fact]
    public async Task ListOrders_UsesTheOverriddenPaymentState()
    {
        // A captured-only rule: an approved payment that is not captured yet does not count as paid.
        var authorized = CreateListedOrder("order-authorized", "buyer-1", "2026-06-16T09:00:00Z", total: 100m, approvedPayment: 100m);
        var service = new OrderServiceCountingCapturedOnly(new StubCustomerOrderSearchService(authorized));

        var response = await service.ListOrders(new UcpOrderListRequest
        {
            Context = new UcpCartContext { BuyerId = "buyer-1" },
            UnpaidOnly = true,
        }, TestContext.Current.CancellationToken);

        var order = Assert.Single(response.Orders);
        Assert.Equal(0, order.Totals.PaidTotal.Amount);
        Assert.Equal(10000, order.Totals.AmountDue.Amount);
    }

    [Fact]
    public async Task ListOrders_FormatsAmountsWithThePlatformCurrency()
    {
        var order = CreateListedOrder("order-1", "buyer-1", "2026-06-16T09:00:00Z", total: 1234.5m);
        var currencies = new StubCurrencyService(
            new VirtoCommerce.CoreModule.Core.Currency.Currency(new VirtoCommerce.CoreModule.Core.Common.Language("en-US"), "USD", "US dollar", "$", 1m),
            new VirtoCommerce.CoreModule.Core.Currency.Currency(new VirtoCommerce.CoreModule.Core.Common.Language("en-US"), "JPY", "Yen", "¥", 1m) { DecimalDigits = 0 });
        var service = new UcpOrderService(
            new StubCustomerOrderService(),
            new StubCustomerOrderSearchService(order),
            CreateHttpContextAccessor(),
            Options.Create(new UcpOptions { DefaultCultureName = "en-US" }),
            new TestBuyerContextAccessor(),
            currencyService: currencies);

        var usd = await service.ListOrders(new UcpOrderListRequest
        {
            Context = new UcpCartContext { BuyerId = "buyer-1" },
        }, TestContext.Current.CancellationToken);
        order.Currency = "JPY";
        var jpy = await service.ListOrders(new UcpOrderListRequest
        {
            Context = new UcpCartContext { BuyerId = "buyer-1" },
        }, TestContext.Current.CancellationToken);

        Assert.Equal("$1,234.50", usd.Orders.Single().Totals.Total.FormattedAmount);
        Assert.Equal(123450, usd.Orders.Single().Totals.Total.Amount);
        Assert.Equal("¥1,235", jpy.Orders.Single().Totals.Total.FormattedAmount);
        // JPY has no minor unit in the platform (DecimalDigits 0), so amount is whole yen, not hundredths.
        Assert.Equal(1235, jpy.Orders.Single().Totals.Total.Amount);
    }

    private sealed class StubCurrencyService(params VirtoCommerce.CoreModule.Core.Currency.Currency[] currencies)
        : VirtoCommerce.CoreModule.Core.Currency.ICurrencyService
    {
        public Task<IEnumerable<VirtoCommerce.CoreModule.Core.Currency.Currency>> GetAllCurrenciesAsync()
        {
            return Task.FromResult<IEnumerable<VirtoCommerce.CoreModule.Core.Currency.Currency>>(currencies);
        }

        public Task SaveChangesAsync(VirtoCommerce.CoreModule.Core.Currency.Currency[] currencies) => Task.CompletedTask;

        public Task DeleteCurrenciesAsync(string[] codes) => Task.CompletedTask;
    }

    [Fact]
    public async Task ListOrders_TakesItsPageSizesFromTheStoreSettings()
    {
        var orders = Enumerable.Range(1, 5)
            .Select(i => CreateListedOrder($"order-{i}", "buyer-1", $"2026-06-1{i}T09:00:00Z", total: 10m))
            .Reverse()
            .ToArray();
        var searchService = new StubCustomerOrderSearchService(orders);
        var service = CreateService(orderSearchService: searchService, options: new UcpOptions
        {
            OrderListDefaultLimit = 2,
            OrderListMaxLimit = 3,
        });

        var byDefault = await service.ListOrders(new UcpOrderListRequest
        {
            Context = new UcpCartContext { BuyerId = "buyer-1" },
        }, TestContext.Current.CancellationToken);
        var tooMany = await service.ListOrders(new UcpOrderListRequest
        {
            Context = new UcpCartContext { BuyerId = "buyer-1" },
            Pagination = new UcpPaginationRequest { Limit = 100 },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(2, byDefault.Orders.Count);
        Assert.Equal(3, tooMany.Orders.Count);
    }

    [Fact]
    public async Task ListOrders_StopsAtTheConfiguredScanLimit()
    {
        // Ten orders, none unpaid: with a scan limit of 4 one call reads 4 and hands back a cursor to continue.
        var orders = Enumerable.Range(0, 10)
            .Select(i => CreateListedOrder($"order-{i}", "buyer-1", $"2026-06-1{i}T09:00:00Z", total: 10m, approvedPayment: 10m))
            .Reverse()
            .ToArray();
        var service = CreateService(orderSearchService: new StubCustomerOrderSearchService(orders), options: new UcpOptions
        {
            OrderListMaxLimit = 2,
            OrderListMaxScan = 4,
        });

        var response = await service.ListOrders(new UcpOrderListRequest
        {
            Context = new UcpCartContext { BuyerId = "buyer-1" },
            UnpaidOnly = true,
        }, TestContext.Current.CancellationToken);

        Assert.Empty(response.Orders);
        Assert.Equal("4", response.Pagination.Cursor);
        Assert.Contains(response.Messages, x => x.Code == "scan_limit_reached");
    }

    [Fact]
    public async Task ListOrders_WithInconsistentSettings_StillReturnsAPage()
    {
        var orders = Enumerable.Range(1, 3)
            .Select(i => CreateListedOrder($"order-{i}", "buyer-1", $"2026-06-1{i}T09:00:00Z", total: 10m))
            .ToArray();
        var service = CreateService(orderSearchService: new StubCustomerOrderSearchService(orders), options: new UcpOptions
        {
            OrderListDefaultLimit = 20,
            OrderListMaxLimit = 0,
            OrderListMaxScan = 0,
        });

        var response = await service.ListOrders(new UcpOrderListRequest
        {
            Context = new UcpCartContext { BuyerId = "buyer-1" },
        }, TestContext.Current.CancellationToken);

        Assert.Single(response.Orders);
        Assert.True(response.Pagination.HasNextPage);
    }

    [Fact]
    public async Task TrackOrder_UsesTheRegisteredPaymentStateService()
    {
        var order = CreateListedOrder("order-1", "buyer-1", "2026-06-16T09:00:00Z", total: 100m, approvedPayment: 100m);
        var service = new UcpOrderService(
            new StubCustomerOrderService(order),
            new StubCustomerOrderSearchService(order),
            CreateHttpContextAccessor(),
            Options.Create(new UcpOptions()),
            new TestBuyerContextAccessor(),
            paymentStateService: new CapturedOnlyPaymentStateService());

        var response = await service.TrackOrder(new UcpOrderTrackingRequest
        {
            OrderId = "order-1",
            Context = new UcpCartContext { BuyerId = "buyer-1" },
        }, TestContext.Current.CancellationToken);

        Assert.Equal(0, response.Order.Totals.PaidTotal.Amount);
        Assert.Equal(10000, response.Order.Totals.AmountDue.Amount);
    }

    private sealed class OrderServiceWithoutTotals(ICustomerOrderSearchService searchService)
        : UcpOrderService(new StubCustomerOrderService(), searchService, CreateHttpContextAccessor(), Microsoft.Extensions.Options.Options.Create(new UcpOptions()), new TestBuyerContextAccessor())
    {
        protected override UcpOrder MapOrder(CustomerOrder orderModel)
        {
            return new UcpOrder { Id = orderModel.Id };
        }
    }

    private sealed class OrderServiceCountingCapturedOnly(ICustomerOrderSearchService searchService)
        : UcpOrderService(
            new StubCustomerOrderService(),
            searchService,
            CreateHttpContextAccessor(),
            Microsoft.Extensions.Options.Options.Create(new UcpOptions()),
            new TestBuyerContextAccessor(),
            paymentStateService: new CapturedOnlyPaymentStateService());

    // A downstream rule: an approved payment counts as paid only once it is captured.
    private sealed class CapturedOnlyPaymentStateService : UcpOrderPaymentStateService
    {
        protected override bool IsPaidPayment(PaymentIn payment)
        {
            return base.IsPaidPayment(payment) && payment.CapturedDate.HasValue;
        }
    }

    private static CustomerOrder CreateListedOrder(string id, string buyerId, string createdAt, decimal total, decimal? approvedPayment = null, decimal? pendingPayment = null)
    {
        var order = CreateOrder(id, "cart-" + id, buyerId);
        order.Number = id.ToUpperInvariant();
        order.CreatedDate = DateTime.Parse(createdAt, System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime();
        order.Status = "New";
        order.Total = total;
        order.InPayments = [];
        if (approvedPayment.HasValue)
        {
            order.InPayments.Add(new PaymentIn { Id = id + "-paid", Number = "PAY-" + id, Status = "Paid", IsApproved = true, Sum = approvedPayment.Value, Currency = "USD" });
        }

        if (pendingPayment.HasValue)
        {
            order.InPayments.Add(new PaymentIn { Id = id + "-pending", Number = "PAY-" + id, Status = "New", IsApproved = false, Sum = pendingPayment.Value, Currency = "USD" });
        }

        return order;
    }

    private static UcpOrderService CreateService(
        ICustomerOrderService orderService = null,
        ICustomerOrderSearchService orderSearchService = null,
        string defaultStoreId = null,
        UcpOptions options = null)
    {
        options ??= new UcpOptions();
        options.DefaultCultureName ??= "en-US";
        options.DefaultStoreId ??= defaultStoreId;

        return new UcpOrderService(
            orderService ?? new StubCustomerOrderService(),
            orderSearchService ?? new StubCustomerOrderSearchService(),
            CreateHttpContextAccessor(),
            Options.Create(options),
            new TestBuyerContextAccessor());
    }

    private static HttpContextAccessor CreateHttpContextAccessor()
    {
        var httpContextAccessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext(),
        };
        httpContextAccessor.HttpContext.TraceIdentifier = "trace-order";

        return httpContextAccessor;
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
                EndDate = criteria.EndDate,
                StoreIds = criteria.StoreIds,
                Statuses = criteria.Statuses,
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
                .Where(order => criteria.EndDate == null || order.CreatedDate <= criteria.EndDate)
                .Where(order => criteria.StoreIds == null || criteria.StoreIds.Length == 0 || criteria.StoreIds.Contains(order.StoreId))
                .Where(order => criteria.Statuses == null || criteria.Statuses.Length == 0 || criteria.Statuses.Contains(order.Status))
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
