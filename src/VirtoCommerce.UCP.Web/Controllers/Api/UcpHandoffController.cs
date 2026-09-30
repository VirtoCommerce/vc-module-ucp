using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Models;
using VirtoCommerce.UCP.Core.Services;
using VirtoCommerce.UCP.Web.Filters;

namespace VirtoCommerce.UCP.Web.Controllers.Api;

[ApiController]
[Route("ucp/v1/internal/handoff")]
public class UcpHandoffController : ControllerBase
{
    private readonly IUcpCheckoutService _checkoutService;

    public UcpHandoffController(IUcpCheckoutService checkoutService)
    {
        _checkoutService = checkoutService;
    }

    [HttpPost("restore")]
    [UcpOperation(ModuleConstants.Operations.RestoreHandoff, IsXApiBacked = true)]
    [ProducesResponseType(typeof(UcpHandoffRestoreResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(UcpError), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(UcpError), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<UcpHandoffRestoreResponse>> Restore([FromBody] UcpHandoffRestoreRequest request, CancellationToken cancellationToken)
    {
        return Ok(await _checkoutService.RestoreHandoff(request, cancellationToken));
    }
}
