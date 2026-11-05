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
[Route("ucp/v1/catalog")]
public class UcpCatalogController : ControllerBase
{
    private readonly IUcpCatalogService _catalogService;

    public UcpCatalogController(IUcpCatalogService catalogService)
    {
        _catalogService = catalogService;
    }

    [HttpPost("search")]
    [UcpOperation(ModuleConstants.Operations.SearchProducts, IsXApiBacked = true)]
    [ProducesResponseType(typeof(UcpCatalogSearchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(UcpError), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<UcpCatalogSearchResponse>> SearchProducts(
        [FromBody] UcpCatalogSearchRequest request,
        CancellationToken cancellationToken)
    {
        return Ok(await _catalogService.SearchProducts(request, cancellationToken));
    }

    [HttpGet("products/{id}")]
    [UcpOperation(ModuleConstants.Operations.GetProduct, IsXApiBacked = true)]
    [ProducesResponseType(typeof(UcpProductResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(UcpError), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(UcpError), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<UcpProductResponse>> GetProduct(
        string id,
        [FromQuery] UcpCatalogProductQuery query,
        CancellationToken cancellationToken)
    {
        return Ok(await _catalogService.GetProduct(id, (query ?? new UcpCatalogProductQuery()).ToRequest(), cancellationToken));
    }
}
