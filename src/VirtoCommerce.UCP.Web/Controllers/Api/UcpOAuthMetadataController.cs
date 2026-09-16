using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Web.Models;

namespace VirtoCommerce.UCP.Web.Controllers.Api;

[ApiController]
[AllowAnonymous]
public sealed class UcpOAuthMetadataController : ControllerBase
{
    [HttpGet(ModuleConstants.Endpoints.McpProtectedResourceMetadata)]
    [ProducesResponseType(typeof(UcpProtectedResourceMetadata), StatusCodes.Status200OK)]
    public ActionResult<UcpProtectedResourceMetadata> GetProtectedResourceMetadata()
    {
        var origin = $"{Request.Scheme}://{Request.Host}{Request.PathBase}".TrimEnd('/');
        return Ok(new UcpProtectedResourceMetadata
        {
            Resource = origin + ModuleConstants.Endpoints.Mcp,
            AuthorizationServers = [origin + "/"],
            ScopesSupported = ["openid", "profile", "offline_access"],
            ResourceName = "Virto Commerce UCP MCP",
        });
    }
}
