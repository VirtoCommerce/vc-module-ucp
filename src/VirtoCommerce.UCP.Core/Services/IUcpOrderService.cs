using System.Threading;
using System.Threading.Tasks;
using VirtoCommerce.UCP.Core.Models;

namespace VirtoCommerce.UCP.Core.Services;

public interface IUcpOrderService
{
    Task<UcpOrderResponse> TrackOrder(UcpOrderTrackingRequest request, CancellationToken cancellationToken = default);

    Task<UcpOrderListResponse> ListOrders(UcpOrderListRequest request, CancellationToken cancellationToken = default);
}
