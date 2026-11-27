using System;
using System.Linq;
using VirtoCommerce.OrdersModule.Core.Model;
using VirtoCommerce.UCP.Core.Models;
using VirtoCommerce.UCP.Core.Services;

namespace VirtoCommerce.UCP.Data.Services;

// The default rule: approved, not cancelled incoming payments count as paid; a cancelled order owes nothing.
public class UcpOrderPaymentStateService : IUcpOrderPaymentStateService
{
    public virtual UcpOrderPaymentState GetPaymentState(CustomerOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);

        var isCancelled = IsCancelled(order);
        var paidTotal = GetPaidTotal(order);

        return new UcpOrderPaymentState
        {
            IsCancelled = isCancelled,
            PaidTotal = paidTotal,
            AmountDue = isCancelled ? 0m : Math.Max(0m, order.Total - paidTotal),
        };
    }

    protected virtual decimal GetPaidTotal(CustomerOrder order)
    {
        return (order.InPayments ?? [])
            .Where(IsPaidPayment)
            .Sum(payment => payment.Sum);
    }

    protected virtual bool IsPaidPayment(PaymentIn payment)
    {
        return payment != null && payment.IsApproved && !payment.IsCancelled;
    }

    protected virtual bool IsCancelled(CustomerOrder order)
    {
        return order.IsCancelled || string.Equals(order.Status, "Cancelled", StringComparison.OrdinalIgnoreCase);
    }
}
