using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using VirtoCommerce.OrdersModule.Core.Model;
using VirtoCommerce.OrdersModule.Core.Model.Search;
using VirtoCommerce.OrdersModule.Core.Services;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Diagnostics;
using VirtoCommerce.UCP.Core.Models;
using VirtoCommerce.UCP.Core.Options;
using VirtoCommerce.UCP.Core.Services;
using VirtoCommerce.UCP.Data.Models;

namespace VirtoCommerce.UCP.Data.Services;

public class UcpOrderService : UcpServiceBase, IUcpOrderService
{
    private const int _orderLookupPageSize = 50;
    private static readonly string _orderResponseGroup = CustomerOrderResponseGroup.Full.ToString();

    private readonly ICustomerOrderService _customerOrderService;
    private readonly ICustomerOrderSearchService _customerOrderSearchService;
    private readonly UcpOptions _options;

    public UcpOrderService(
        ICustomerOrderService customerOrderService,
        ICustomerOrderSearchService customerOrderSearchService,
        IHttpContextAccessor httpContextAccessor,
        IOptions<UcpOptions> options,
        IUcpBuyerContextAccessor buyerContextAccessor = null)
        : base(httpContextAccessor, buyerContextAccessor)
    {
        _customerOrderService = customerOrderService;
        _customerOrderSearchService = customerOrderSearchService;
        _options = options.Value;
    }

    public virtual async Task<UcpOrderResponse> TrackOrder(UcpOrderTrackingRequest request, CancellationToken cancellationToken = default)
    {
        request ??= new UcpOrderTrackingRequest();
        var orderRequest = BuildOrderExecutionRequest(request);

        CustomerOrder orderModel;
        if (!string.IsNullOrWhiteSpace(orderRequest.CartId))
        {
            orderModel = await FindOrderByCartId(orderRequest, cancellationToken);
        }
        else
        {
            orderModel = await FindOrderByIdOrNumber(orderRequest);
        }

        if (orderModel == null)
        {
            var lookup = FirstNotEmpty(orderRequest.OrderId, orderRequest.OrderNumber, orderRequest.CartId);
            throw CreateException(ModuleConstants.ErrorCodes.OrderNotFound, $"Order '{lookup}' was not found.", StatusCodes.Status404NotFound);
        }

        var order = MapOrder(orderModel);
        return new UcpOrderResponse
        {
            Ucp = CreateMetadata("success", "dev.ucp.shopping.order.track"),
            Order = order,
            Messages = order.Messages,
        };
    }

    private OrderExecutionRequest BuildOrderExecutionRequest(UcpOrderTrackingRequest request)
    {
        var orderId = FirstNotEmpty(request.OrderId, request.OrderNumber);
        if (string.IsNullOrWhiteSpace(orderId) && string.IsNullOrWhiteSpace(request.CartId))
        {
            throw CreateException(ModuleConstants.ErrorCodes.InvalidRequest, "order_id, order_number, or cart_id is required.");
        }

        var buyerContext = ResolveBuyerContext(
            requestedBuyerIds: [request.Context?.BuyerId],
            requestedOrganizationIds: [request.Context?.OrganizationId],
            requireBuyer: true);

        return new OrderExecutionRequest
        {
            OrderId = request.OrderId,
            OrderNumber = request.OrderNumber,
            CartId = request.CartId,
            CultureName = FirstNotEmpty(request.Context?.Language, _options.DefaultCultureName),
            UserId = buyerContext.UserId,
            OrganizationId = buyerContext.OrganizationId,
        };
    }

    private async Task<CustomerOrder> FindOrderByIdOrNumber(OrderExecutionRequest request)
    {
        if (!HasOrderScope(request))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(request.OrderId))
        {
            var orders = await UcpDiagnostics.ExecuteDependency(
                "orders",
                "GetOrdersById",
                () => _customerOrderService.GetAsync([request.OrderId], _orderResponseGroup, clone: false));
            var order = orders.FirstOrDefault();
            if (IsOrderInScope(order, request))
            {
                return order;
            }
        }

        var number = FirstNotEmpty(request.OrderNumber, request.OrderId);
        if (string.IsNullOrWhiteSpace(number))
        {
            return null;
        }

        var result = await UcpDiagnostics.ExecuteDependency(
            "orders",
            "SearchOrdersByNumber",
            () => _customerOrderSearchService.SearchAsync(new CustomerOrderSearchCriteria
            {
                CustomerId = request.UserId,
                OrganizationId = request.OrganizationId,
                Number = number,
                Take = 1,
                ResponseGroup = _orderResponseGroup,
            }, clone: false));

        return result.Results.FirstOrDefault(x => IsOrderInScope(x, request));
    }

    private async Task<CustomerOrder> FindOrderByCartId(OrderExecutionRequest request, CancellationToken cancellationToken)
    {
        if (!HasOrderScope(request))
        {
            return null;
        }

        var criteria = new CustomerOrderSearchCriteria
        {
            CustomerId = request.UserId,
            OrganizationId = request.OrganizationId,
            Take = _orderLookupPageSize,
            Sort = "CreatedDate:desc",
            ResponseGroup = _orderResponseGroup,
        };

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await UcpDiagnostics.ExecuteDependency(
                "orders",
                "SearchOrdersByCartScoped",
                () => _customerOrderSearchService.SearchAsync(criteria, clone: false));
            var order = result.Results.FirstOrDefault(x => IsOrderInScope(x, request) &&
                string.Equals(x.ShoppingCartId, request.CartId, StringComparison.OrdinalIgnoreCase));
            if (order != null)
            {
                return order;
            }

            criteria.Skip += result.Results.Count;
            if (result.Results.Count == 0 || criteria.Skip >= result.TotalCount)
            {
                return null;
            }
        }
    }

    protected virtual UcpOrder MapOrder(CustomerOrder orderModel)
    {
        var currency = orderModel.Currency;
        var order = new UcpOrder
        {
            Id = orderModel.Id,
            Number = orderModel.Number,
            Status = orderModel.Status,
            StatusDisplayValue = orderModel.Status,
            CreatedAt = orderModel.CreatedDate.ToString("O"),
            CartId = orderModel.ShoppingCartId,
            StoreId = orderModel.StoreId,
            BuyerId = orderModel.CustomerId,
            CustomerName = orderModel.CustomerName,
            Currency = currency,
            Totals = new UcpOrderTotals
            {
                Subtotal = CreateMoney(orderModel.SubTotal, currency),
                Total = CreateMoney(orderModel.Total, currency),
                TaxTotal = CreateMoney(orderModel.TaxTotal, currency),
                DiscountTotal = CreateMoney(orderModel.DiscountTotal, currency),
                ShippingSubtotal = CreateMoney(orderModel.ShippingSubTotal, currency),
                ShippingTotal = CreateMoney(orderModel.ShippingTotal, currency),
            },
            LineItems = MapLineItems(orderModel.Items, currency),
            Shipments = MapShipments(orderModel.Shipments, currency),
            Payments = MapPayments(orderModel.InPayments, currency),
        };

        order.Messages = CreateOrderMessages(order);
        return order;
    }

    protected virtual IList<UcpOrderLineItem> MapLineItems(IEnumerable<LineItem> items, string currency)
    {
        return (items ?? [])
            .Select(item =>
            {
                var itemCurrency = FirstNotEmpty(item.Currency, currency);
                var placedPrice = item.PlacedPrice != 0 ? item.PlacedPrice : item.Price;
                var lineTotal = item.ExtendedPrice != 0 ? item.ExtendedPrice : placedPrice * item.Quantity;

                return new UcpOrderLineItem
                {
                    Id = item.Id,
                    ProductId = item.ProductId,
                    Sku = item.Sku,
                    Name = item.Name,
                    Status = item.Status,
                    ImageUrl = item.ImageUrl,
                    Quantity = item.Quantity,
                    UnitPrice = CreateMoney(item.Price, itemCurrency),
                    PlacedPrice = CreateMoney(placedPrice, itemCurrency),
                    LineTotal = CreateMoney(lineTotal, itemCurrency),
                    DiscountTotal = CreateMoney(item.DiscountTotal, itemCurrency),
                    TaxTotal = CreateMoney(item.TaxTotal, itemCurrency),
                };
            })
            .ToList();
    }

    private static bool HasOrderScope(OrderExecutionRequest request)
    {
        return !string.IsNullOrWhiteSpace(request.UserId) || !string.IsNullOrWhiteSpace(request.OrganizationId);
    }

    private static bool IsOrderInScope(CustomerOrder order, OrderExecutionRequest request)
    {
        if (order == null || !HasOrderScope(request))
        {
            return false;
        }

        return (string.IsNullOrWhiteSpace(request.UserId) || string.Equals(order.CustomerId, request.UserId, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(request.OrganizationId) || string.Equals(order.OrganizationId, request.OrganizationId, StringComparison.OrdinalIgnoreCase));
    }

    protected virtual IList<UcpOrderShipment> MapShipments(IEnumerable<Shipment> shipments, string currency)
    {
        return (shipments ?? [])
            .Select(shipment => new UcpOrderShipment
            {
                Id = shipment.Id,
                Number = shipment.Number,
                Status = shipment.Status,
                Approved = shipment.IsApproved,
                ShipmentMethodCode = shipment.ShipmentMethodCode,
                ShipmentMethodOption = shipment.ShipmentMethodOption,
                TrackingNumber = shipment.TrackingNumber,
                TrackingUrl = shipment.TrackingUrl,
                DeliveryAt = shipment.DeliveryDate?.ToString("O"),
                Price = CreateMoney(shipment.Price, FirstNotEmpty(shipment.Currency, currency)),
                DiscountAmount = CreateMoney(shipment.DiscountAmount, FirstNotEmpty(shipment.Currency, currency)),
                DeliveryAddress = MapAddress(shipment.DeliveryAddress),
            })
            .ToList();
    }

    protected virtual IList<UcpOrderPayment> MapPayments(IEnumerable<PaymentIn> payments, string currency)
    {
        return (payments ?? [])
            .Select(payment => new UcpOrderPayment
            {
                Id = payment.Id,
                Number = payment.Number,
                GatewayCode = payment.GatewayCode,
                Status = payment.Status,
                Approved = payment.IsApproved,
                MethodCode = payment.PaymentMethod?.Code,
                MethodName = payment.PaymentMethod?.Name,
                BillingAddress = MapAddress(payment.BillingAddress),
            })
            .ToList();
    }

    protected virtual UcpOrderAddress MapAddress(Address address)
    {
        if (address == null)
        {
            return null;
        }

        return new UcpOrderAddress
        {
            Id = address.Key,
            Name = address.Name,
            Organization = address.Organization,
            FirstName = address.FirstName,
            LastName = address.LastName,
            Line1 = address.Line1,
            Line2 = address.Line2,
            City = address.City,
            Region = address.RegionName,
            RegionId = address.RegionId,
            PostalCode = FirstNotEmpty(address.PostalCode, address.Zip),
            CountryCode = address.CountryCode,
            CountryName = address.CountryName,
            Phone = address.Phone,
            Email = address.Email,
        };
    }

    protected virtual IList<UcpMessage> CreateOrderMessages(UcpOrder order)
    {
        var messages = new List<UcpMessage>();

        if (order.Shipments.Any(shipment => !string.IsNullOrWhiteSpace(shipment.TrackingNumber) || !string.IsNullOrWhiteSpace(shipment.TrackingUrl)))
        {
            messages.Add(new UcpMessage
            {
                Type = "info",
                Code = "shipment_tracking_available",
                Content = "Shipment tracking information is available on this order.",
                Severity = "info",
            });
        }

        return messages;
    }

    protected virtual UcpMoney CreateMoney(decimal amount, string currency)
    {
        return new UcpMoney
        {
            Amount = ToMinorUnits(amount),
            Currency = currency,
        };
    }

}
