using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Services;
using VirtoCommerce.UCP.Web.Models;

namespace VirtoCommerce.UCP.Web.Controllers.Api;

[ApiController]
[AllowAnonymous]
public sealed class UcpOAuthMetadataController : ControllerBase
{
    private readonly IUcpPublicOriginResolver _publicOriginResolver;

    public UcpOAuthMetadataController(IUcpPublicOriginResolver publicOriginResolver)
    {
        _publicOriginResolver = publicOriginResolver;
    }

    [HttpGet(ModuleConstants.Endpoints.McpProtectedResourceMetadata)]
    [ProducesResponseType(typeof(UcpProtectedResourceMetadata), StatusCodes.Status200OK)]
    public async Task<ActionResult<UcpProtectedResourceMetadata>> GetProtectedResourceMetadata()
    {
        var origin = await _publicOriginResolver.GetOriginAsync();
        return Ok(new UcpProtectedResourceMetadata
        {
            Resource = origin + ModuleConstants.Endpoints.Mcp,
            AuthorizationServers = [origin + "/"],
            ScopesSupported = ["openid", "profile", "offline_access"],
            ResourceName = "Virto Commerce UCP MCP",
        });
    }
}
