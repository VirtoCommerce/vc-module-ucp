using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Attributes;
using VirtoCommerce.UCP.Core.Models;
using VirtoCommerce.UCP.Core.Services;
using VirtoCommerce.UCP.Web.Models;

namespace VirtoCommerce.UCP.Web.Controllers.Api;

[ApiController]
[Route("ucp/v1/orders")]
public class UcpOrderController : ControllerBase
{
    private readonly IUcpOrderService _orderService;

    public UcpOrderController(IUcpOrderService orderService)
    {
        _orderService = orderService;
    }

    [HttpGet]
    [UcpOperation(ModuleConstants.Operations.TrackOrder)]
    [ProducesResponseType(typeof(UcpOrderResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(UcpError), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(UcpError), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UcpOrderResponse>> TrackOrderByCart(
        [FromQuery] UcpOrderTrackingQuery query,
        CancellationToken cancellationToken)
    {
        return Ok(await _orderService.TrackOrder((query ?? new UcpOrderTrackingQuery()).ToRequest(), cancellationToken));
    }

    [HttpGet("{orderId}")]
    [UcpOperation(ModuleConstants.Operations.TrackOrder)]
    [ProducesResponseType(typeof(UcpOrderResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(UcpError), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(UcpError), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UcpOrderResponse>> TrackOrder(
        string orderId,
        [FromQuery] UcpOrderTrackingQuery query,
        CancellationToken cancellationToken)
    {
        var request = (query ?? new UcpOrderTrackingQuery()).ToRequest();
        request.OrderId = orderId;

        return Ok(await _orderService.TrackOrder(request, cancellationToken));
    }
}
