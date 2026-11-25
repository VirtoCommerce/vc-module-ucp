namespace VirtoCommerce.UCP.Core.Models;

// What a buyer has paid and still owes on one order, in the order's currency (major units, as the order stores them).
public class UcpOrderPaymentState
{
    public decimal PaidTotal { get; set; }

    public decimal AmountDue { get; set; }

    public bool IsCancelled { get; set; }

    // Not cancelled and something is still due: what list_orders unpaid_only keeps.
    public bool IsUnpaid => !IsCancelled && AmountDue > 0m;

    // Not cancelled, nothing due and something was paid, so a free order is not "paid": what list_orders paid_only keeps.
    public bool IsPaid => !IsCancelled && AmountDue == 0m && PaidTotal > 0m;
}
