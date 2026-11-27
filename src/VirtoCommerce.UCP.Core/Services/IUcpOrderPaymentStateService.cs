using VirtoCommerce.OrdersModule.Core.Model;
using VirtoCommerce.UCP.Core.Models;

namespace VirtoCommerce.UCP.Core.Services;

// Decides what a buyer has paid and still owes on an order: totals.paid_total and totals.amount_due of track_order and
// list_orders, and the unpaid_only and paid_only filters. Register another implementation to change the rule, for example
// to count captured payments only or to subtract refunds.
public interface IUcpOrderPaymentStateService
{
    UcpOrderPaymentState GetPaymentState(CustomerOrder order);
}
