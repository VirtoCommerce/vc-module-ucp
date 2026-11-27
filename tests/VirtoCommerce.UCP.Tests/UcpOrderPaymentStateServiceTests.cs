using VirtoCommerce.OrdersModule.Core.Model;
using VirtoCommerce.UCP.Data.Services;
using Xunit;

namespace VirtoCommerce.UCP.Tests;

[Trait("Category", "Unit")]
public class UcpOrderPaymentStateServiceTests
{
    private readonly UcpOrderPaymentStateService _service = new();

    [Fact]
    public void GetPaymentState_CountsApprovedNotCancelledPaymentsOnly()
    {
        var order = new CustomerOrder
        {
            Total = 100m,
            InPayments =
            [
                new PaymentIn { IsApproved = true, Sum = 30m },
                new PaymentIn { IsApproved = false, Sum = 50m },
                new PaymentIn { IsApproved = true, IsCancelled = true, Sum = 50m },
                null,
            ],
        };

        var state = _service.GetPaymentState(order);

        Assert.Equal(30m, state.PaidTotal);
        Assert.Equal(70m, state.AmountDue);
        Assert.True(state.IsUnpaid);
        Assert.False(state.IsPaid);
    }

    [Fact]
    public void GetPaymentState_OverpaidOrderOwesNothingAndIsPaid()
    {
        var order = new CustomerOrder { Total = 100m, InPayments = [new PaymentIn { IsApproved = true, Sum = 120m }] };

        var state = _service.GetPaymentState(order);

        Assert.Equal(0m, state.AmountDue);
        Assert.True(state.IsPaid);
    }

    [Fact]
    public void GetPaymentState_CancelledOrderOwesNothingAndIsNeitherPaidNorUnpaid()
    {
        var order = new CustomerOrder { Total = 100m, Status = "Cancelled", InPayments = [] };

        var state = _service.GetPaymentState(order);

        Assert.True(state.IsCancelled);
        Assert.Equal(0m, state.AmountDue);
        Assert.False(state.IsUnpaid);
        Assert.False(state.IsPaid);
    }

    [Fact]
    public void GetPaymentState_FreeOrderIsNotPaid()
    {
        var state = _service.GetPaymentState(new CustomerOrder { Total = 0m });

        Assert.Equal(0m, state.AmountDue);
        Assert.False(state.IsPaid);
        Assert.False(state.IsUnpaid);
    }
}
