using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Options;
using VirtoCommerce.UCP.Data.Services;
using VirtoCommerce.UCP.Web.Models;

namespace VirtoCommerce.UCP.Web.Controllers.Api;

[ApiController]
[AllowAnonymous]
public sealed class UcpOAuthMetadataController : ControllerBase
{
    private readonly UcpOptions _options;

    public UcpOAuthMetadataController(IOptions<UcpOptions> options = null)
    {
        _options = options?.Value;
    }

    [HttpGet(ModuleConstants.Endpoints.McpProtectedResourceMetadata)]
    [ProducesResponseType(typeof(UcpProtectedResourceMetadata), StatusCodes.Status200OK)]
    public ActionResult<UcpProtectedResourceMetadata> GetProtectedResourceMetadata()
    {
        var origin = UcpPublicEndpoints.GetOrigin(_options, Request);
        return Ok(new UcpProtectedResourceMetadata
        {
            Resource = origin + ModuleConstants.Endpoints.Mcp,
            AuthorizationServers = [origin + "/"],
            ScopesSupported = ["openid", "profile", "offline_access"],
            ResourceName = "Virto Commerce UCP MCP",
        });
    }
}
