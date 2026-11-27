using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Language = VirtoCommerce.CoreModule.Core.Common.Language;
using VirtoCommerce.CoreModule.Core.Currency;
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
    private readonly IUcpRequestOptions _requestOptions;
    private readonly ICurrencyService _currencyService;
    private readonly IUcpOrderPaymentStateService _paymentStateService;
    // The platform currencies of the current call, by code, so formatted_amount is formatted the way XAPI formats it.
    private readonly AsyncLocal<IDictionary<string, Currency>> _currencies = new();

    public UcpOrderService(
        ICustomerOrderService customerOrderService,
        ICustomerOrderSearchService customerOrderSearchService,
        IHttpContextAccessor httpContextAccessor,
        IOptions<UcpOptions> options,
        IUcpBuyerContextAccessor buyerContextAccessor = null,
        IUcpRequestOptions requestOptions = null,
        ICurrencyService currencyService = null,
        IUcpOrderPaymentStateService paymentStateService = null)
        : base(httpContextAccessor, buyerContextAccessor)
    {
        _currencyService = currencyService;
        _paymentStateService = paymentStateService ?? new UcpOrderPaymentStateService();
        _customerOrderService = customerOrderService;
        _customerOrderSearchService = customerOrderSearchService;
        _options = options.Value;
        _requestOptions = requestOptions;
    }

    // The options of the request's store, with the configuration as the fallback.
    protected UcpOptions Options => _requestOptions?.Current ?? _options;

    public virtual async Task<UcpOrderResponse> TrackOrder(UcpOrderTrackingRequest request, CancellationToken cancellationToken = default)
    {
        request ??= new UcpOrderTrackingRequest();
        var orderRequest = BuildOrderExecutionRequest(request);

        IList<CustomerOrder> orderModels;
        if (!string.IsNullOrWhiteSpace(orderRequest.CartId))
        {
            orderModels = await FindOrdersByCartId(orderRequest, cancellationToken);
        }
        else
        {
            var orderModel = await FindOrderByIdOrNumber(orderRequest);
            orderModels = orderModel == null ? [] : [orderModel];
        }

        if (orderModels.Count == 0)
        {
            throw CreateException(ModuleConstants.ErrorCodes.OrderNotFound, CreateOrderNotFoundMessage(orderRequest), StatusCodes.Status404NotFound);
        }

        _currencies.Value = await LoadCurrencies(orderRequest.CultureName);

        var orders = orderModels
            .OrderBy(x => x.CreatedDate)
            .ThenBy(x => !string.IsNullOrEmpty(x.ParentOperationId))
            .Select(MapOrder)
            .ToList();
        var order = orders.FindLast(x => string.IsNullOrEmpty(x.ParentOrderId)) ?? orders[^1];

        return new UcpOrderResponse
        {
            Ucp = CreateMetadata("success", "dev.ucp.shopping.order.track"),
            Order = order,
            Orders = orders,
            Messages = order.Messages,
        };
    }

    public virtual async Task<UcpOrderListResponse> ListOrders(UcpOrderListRequest request, CancellationToken cancellationToken = default)
    {
        request ??= new UcpOrderListRequest();
        // Identity first: an anonymous caller gets identity_required, not a complaint about its arguments.
        var scope = ResolveOrderListScope(request);
        if (request.UnpaidOnly && request.PaidOnly)
        {
            throw CreateException(ModuleConstants.ErrorCodes.InvalidRequest, "Pass unpaid_only or paid_only, not both.");
        }

        _currencies.Value = await LoadCurrencies(FirstNotEmpty(request.Context?.Language, Options.DefaultCultureName));
        var limits = GetOrderListLimits();
        var limit = Math.Clamp(request.Pagination?.Limit ?? limits.DefaultLimit, 1, limits.MaxLimit);
        var criteria = CreateOrderListCriteria(request, scope.UserId, scope.OrganizationId, ParseOrderListCursor(request.Pagination?.Cursor), limit);
        var page = await ReadOrderListPage(request, scope, criteria, limit, limits.MaxScan, cancellationToken);

        return CreateOrderListResponse(request, page, limit);
    }

    // Listing orders reads the buyer's history, so it needs a signed-in buyer; an anonymous buyer_id is not enough.
    private OrderExecutionRequest ResolveOrderListScope(UcpOrderListRequest request)
    {
        var buyerContext = ResolveBuyerContext(
            requestedBuyerIds: [request.Context?.BuyerId],
            requestedOrganizationIds: [request.Context?.OrganizationId],
            requireBuyer: true,
            requireAuthenticatedBuyer: true);
        var scope = new OrderExecutionRequest
        {
            UserId = buyerContext.UserId,
            OrganizationId = buyerContext.OrganizationId,
        };

        if (!HasOrderScope(scope))
        {
            throw CreateException(ModuleConstants.ErrorCodes.IdentityRequired, "A buyer is required to list orders.", StatusCodes.Status401Unauthorized);
        }

        return scope;
    }

    // The search one list_orders page starts from. Without a payment filter every order found is returned, so it reads exactly
    // one page of the requested size; with unpaid_only or paid_only it reads larger batches, since some orders are filtered out.
    protected virtual CustomerOrderSearchCriteria CreateOrderListCriteria(UcpOrderListRequest request, string userId, string organizationId, int position, int limit)
    {
        var statuses = (request.Statuses ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToArray();
        // The same store rule as cart and catalog: the requested store, else the request's store (the default store).
        var storeId = FirstNotEmpty(request.Context?.StoreId, Options.DefaultStoreId);

        return new CustomerOrderSearchCriteria
        {
            CustomerId = userId,
            OrganizationId = organizationId,
            StoreIds = string.IsNullOrWhiteSpace(storeId) ? null : [storeId],
            Statuses = statuses.Length > 0 ? statuses : null,
            StartDate = request.PlacedAfter?.UtcDateTime,
            EndDate = request.PlacedBefore?.UtcDateTime,
            Sort = "CreatedDate:desc",
            Skip = position,
            Take = HasPaymentFilter(request) ? _orderLookupPageSize : limit,
            ResponseGroup = _orderResponseGroup,
        };
    }

    private static bool HasPaymentFilter(UcpOrderListRequest request)
    {
        return request.UnpaidOnly || request.PaidOnly;
    }

    // The store settings UCP.OrderListDefaultLimit, UCP.OrderListMaxLimit and UCP.OrderListMaxScan, made consistent: the
    // largest page is at least 1, the default page fits in it, and one call reads at least one full page.
    private (int DefaultLimit, int MaxLimit, int MaxScan) GetOrderListLimits()
    {
        var maxLimit = Math.Max(1, Options.OrderListMaxLimit);
        var defaultLimit = Math.Clamp(Options.OrderListDefaultLimit, 1, maxLimit);
        var maxScan = Math.Max(maxLimit, Options.OrderListMaxScan);

        return (defaultLimit, maxLimit, maxScan);
    }

    private async Task<OrderListPage> ReadOrderListPage(
        UcpOrderListRequest request,
        OrderExecutionRequest scope,
        CustomerOrderSearchCriteria criteria,
        int limit,
        int maxScan,
        CancellationToken cancellationToken)
    {
        var page = new OrderListPage { Position = criteria.Skip, MaxScan = maxScan };
        var seenOrderIds = new HashSet<string>(StringComparer.Ordinal);

        while (page.Orders.Count < limit && page.Scanned < page.MaxScan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await UcpDiagnostics.ExecuteDependency(
                "orders",
                "SearchOrdersOfBuyer",
                () => _customerOrderSearchService.SearchAsync(criteria, clone: false));
            page.TotalCount = result.TotalCount;
            if (result.Results.Count == 0)
            {
                break;
            }

            TakeListedOrders(result.Results, request, scope, seenOrderIds, page, limit);

            criteria.Skip = page.Position;
            if (page.Position >= page.TotalCount)
            {
                break;
            }
        }

        return page;
    }

    // Consumes the page up to the order that fills the limit, so the cursor resumes right after it.
    private void TakeListedOrders(
        IEnumerable<CustomerOrder> orderModels,
        UcpOrderListRequest request,
        OrderExecutionRequest scope,
        HashSet<string> seenOrderIds,
        OrderListPage page,
        int limit)
    {
        foreach (var orderModel in orderModels)
        {
            page.Position++;
            page.Scanned++;

            var order = ToListedOrder(orderModel, request, scope, seenOrderIds);
            if (order != null)
            {
                page.Orders.Add(order);
            }

            if (page.Orders.Count >= limit || page.Scanned >= page.MaxScan)
            {
                return;
            }
        }
    }

    private UcpOrder ToListedOrder(CustomerOrder orderModel, UcpOrderListRequest request, OrderExecutionRequest scope, HashSet<string> seenOrderIds)
    {
        if (!IsOrderInScope(orderModel, scope) || !seenOrderIds.Add(orderModel.Id))
        {
            return null;
        }

        // The payment filters read the order itself, not the mapped response, so an override of MapOrder cannot break them.
        var paymentState = HasPaymentFilter(request) ? GetPaymentState(orderModel) : null;
        if ((request.UnpaidOnly && !paymentState.IsUnpaid) || (request.PaidOnly && !paymentState.IsPaid))
        {
            return null;
        }

        return MapOrder(orderModel);
    }

    private UcpOrderListResponse CreateOrderListResponse(UcpOrderListRequest request, OrderListPage page, int limit)
    {
        var hasNextPage = page.Position < page.TotalCount;
        var response = new UcpOrderListResponse
        {
            Ucp = CreateMetadata("success", ModuleConstants.Discovery.Order),
            Orders = page.Orders,
            Pagination = new UcpPaginationResponse
            {
                Cursor = hasNextPage ? page.Position.ToString(CultureInfo.InvariantCulture) : null,
                HasNextPage = hasNextPage,
                TotalCount = page.TotalCount,
            },
        };

        if (HasPaymentFilter(request))
        {
            response.Messages.Add(new UcpMessage
            {
                Type = "info",
                Code = "total_count_before_payment_filter",
                Content = "pagination.total_count counts the buyer's orders that match the other filters, before unpaid_only or paid_only is applied.",
                Severity = "info",
            });
        }

        if (hasNextPage && page.Orders.Count < limit)
        {
            response.Messages.Add(new UcpMessage
            {
                Type = "info",
                Code = "scan_limit_reached",
                Content = $"This call read {page.Scanned} orders, the most one call reads; call list_orders again with the returned cursor to continue.",
                Severity = "info",
            });
        }

        return response;
    }

    private sealed class OrderListPage
    {
        public List<UcpOrder> Orders { get; } = [];
        public int Position { get; set; }
        public int TotalCount { get; set; }
        public int Scanned { get; set; }
        public int MaxScan { get; set; }
    }

    private int ParseOrderListCursor(string cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return 0;
        }

        if (!int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var position))
        {
            throw CreateException(ModuleConstants.ErrorCodes.InvalidRequest, "cursor must be the value list_orders returned in pagination.cursor.");
        }

        return position;
    }

    // What the buyer has paid and still owes, from IUcpOrderPaymentStateService.
    protected virtual UcpOrderPaymentState GetPaymentState(CustomerOrder orderModel)
    {
        return _paymentStateService.GetPaymentState(orderModel);
    }

    private static string CreateOrderNotFoundMessage(OrderExecutionRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.CartId) && request.PlacedAfter.HasValue)
        {
            return $"No order was placed from cart '{request.CartId}' at or after {request.PlacedAfter.Value.UtcDateTime:O}.";
        }

        var lookup = FirstNotEmpty(request.OrderId, request.OrderNumber, request.CartId);

        return $"Order '{lookup}' was not found.";
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
            PlacedAfter = request.PlacedAfter,
            CultureName = FirstNotEmpty(request.Context?.Language, Options.DefaultCultureName),
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

    private async Task<IList<CustomerOrder>> FindOrdersByCartId(OrderExecutionRequest request, CancellationToken cancellationToken)
    {
        if (!HasOrderScope(request))
        {
            return [];
        }

        if (request.PlacedAfter is not null)
        {
            return await SearchOrdersOfCart(request, CreateCartOrderCriteria(request), firstMatchOnly: false, cancellationToken);
        }

        var mainCriteria = CreateCartOrderCriteria(request);
        mainCriteria.HasParentOperation = false;
        var mainOrders = await SearchOrdersOfCart(request, mainCriteria, firstMatchOnly: true, cancellationToken);
        if (mainOrders.Count == 0)
        {
            return mainOrders;
        }

        var main = mainOrders[0];
        var childCriteria = CreateCartOrderCriteria(request);
        childCriteria.ParentOperationId = main.Id;
        var childOrders = await SearchOrdersOfCart(request, childCriteria, firstMatchOnly: false, cancellationToken);

        return childOrders.Append(main).ToList();
    }

    private static CustomerOrderSearchCriteria CreateCartOrderCriteria(OrderExecutionRequest request)
    {
        return new CustomerOrderSearchCriteria
        {
            CustomerId = request.UserId,
            OrganizationId = request.OrganizationId,
            StartDate = request.PlacedAfter?.UtcDateTime,
            Take = _orderLookupPageSize,
            Sort = "CreatedDate:desc",
            ResponseGroup = _orderResponseGroup,
        };
    }

    private async Task<List<CustomerOrder>> SearchOrdersOfCart(
        OrderExecutionRequest request,
        CustomerOrderSearchCriteria criteria,
        bool firstMatchOnly,
        CancellationToken cancellationToken)
    {
        var orders = new List<CustomerOrder>();

        // Offset paging can serve the same row twice when an order is created mid-scan.
        var seenOrderIds = new HashSet<string>(StringComparer.Ordinal);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await UcpDiagnostics.ExecuteDependency(
                "orders",
                "SearchOrdersByCartScoped",
                () => _customerOrderSearchService.SearchAsync(criteria, clone: false));
            orders.AddRange(result.Results.Where(x => IsOrderInScope(x, request) &&
                string.Equals(x.ShoppingCartId, request.CartId, StringComparison.OrdinalIgnoreCase) &&
                seenOrderIds.Add(x.Id)));

            if (orders.Count > 0 && firstMatchOnly)
            {
                return [orders[0]];
            }

            criteria.Skip += result.Results.Count;
            if (result.Results.Count == 0 || criteria.Skip >= result.TotalCount)
            {
                return orders;
            }
        }
    }

    protected virtual UcpOrder MapOrder(CustomerOrder orderModel)
    {
        var currency = orderModel.Currency;
        var paymentState = GetPaymentState(orderModel);
        var order = new UcpOrder
        {
            Id = orderModel.Id,
            Number = orderModel.Number,
            Status = orderModel.Status,
            StatusDisplayValue = orderModel.Status,
            CreatedAt = orderModel.CreatedDate.ToString("O"),
            CartId = orderModel.ShoppingCartId,
            ParentOrderId = orderModel.ParentOperationId,
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
                PaidTotal = CreateMoney(paymentState.PaidTotal, currency),
                AmountDue = CreateMoney(paymentState.AmountDue, currency),
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
                Amount = CreateMoney(payment.Sum, FirstNotEmpty(payment.Currency, currency)),
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
        var decimalDigits = GetDecimalDigits(currency);
        var minorUnits = ToMinorUnits(amount, decimalDigits);

        return new UcpMoney
        {
            Amount = minorUnits,
            Currency = currency,
            FormattedAmount = FormatAmount(amount, minorUnits, currency),
        };
    }

    // The currency's decimal digits as the platform configures them (Currency.DecimalDigits), else two.
    protected virtual int GetDecimalDigits(string currency)
    {
        return FindCurrency(currency)?.DecimalDigits ?? DefaultDecimalDigits;
    }

    private Currency FindCurrency(string currency)
    {
        var currencies = _currencies.Value;

        return !string.IsNullOrEmpty(currency) && currencies != null && currencies.TryGetValue(currency, out var platformCurrency)
            ? platformCurrency
            : null;
    }

    // The amount as an agent should say it (an agent reading only amount 2100 tends to say $2,100). A currency registered in the
    // platform is formatted as XAPI formats cart and catalog money ("$21.00"); any other falls back to "21.00 USD".
    protected virtual string FormatAmount(decimal amount, long minorUnits, string currency)
    {
        var platformCurrency = FindCurrency(currency);
        if (platformCurrency != null)
        {
            return new Money(amount, platformCurrency).FormattedAmount;
        }

        var majorUnits = FromMinorUnits(minorUnits, DefaultDecimalDigits).ToString("0.00", CultureInfo.InvariantCulture);

        return string.IsNullOrEmpty(currency) ? majorUnits : $"{majorUnits} {currency}";
    }

    // Set by the calling operation: a value set inside an awaited method does not flow back to its caller.
    private async Task<IDictionary<string, Currency>> LoadCurrencies(string cultureName)
    {
        if (_currencyService == null)
        {
            return null;
        }

        var currencies = await UcpDiagnostics.ExecuteDependency("currencies", "GetAllCurrencies", () => _currencyService.GetAllCurrenciesAsync());
        return (currencies ?? [])
            .Where(x => !string.IsNullOrEmpty(x?.Code))
            .GroupBy(x => x.Code, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => ForCulture(x.First(), cultureName), StringComparer.OrdinalIgnoreCase);
    }

    // The registered currency in the request's culture, the same clone XAPI makes for order money.
    private static Currency ForCulture(Currency currency, string cultureName)
    {
        var result = new Currency(
            string.IsNullOrEmpty(cultureName) ? Language.InvariantLanguage : new Language(cultureName),
            currency.Code,
            currency.Name,
            currency.Symbol,
            currency.ExchangeRate)
        {
            CustomFormatting = currency.CustomFormatting,
            RoundingPolicy = currency.RoundingPolicy ?? new DefaultMoneyRoundingPolicy(),
            RoundingType = currency.RoundingType,
            MidpointRounding = currency.MidpointRounding,
            DecimalDigits = currency.DecimalDigits,
        };
        // The constructor copied the default 2 digits into the number format; use the currency's own.
        result.NumberFormat.CurrencyDecimalDigits = currency.DecimalDigits;

        return result;
    }

}
