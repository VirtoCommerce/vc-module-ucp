using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Newtonsoft.Json;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Attributes;
using VirtoCommerce.UCP.Core.Models;
using VirtoCommerce.UCP.Core.Services;

namespace VirtoCommerce.UCP.Web.Controllers.Api;

[ApiController]
public class UcpProfileController : ControllerBase
{
    // UCP hosting rules: a published profile MUST be served with Cache-Control public and a max-age of at least 60
    // seconds, never private, no-store or no-cache, and SHOULD carry a validator (ETag or Last-Modified).
    private static readonly TimeSpan ProfileMaxAge = TimeSpan.FromMinutes(5);

    // The validator is the first 128 bits of the SHA-256 of the profile.
    private const int ETagHashBytes = 16;

    private readonly IUcpProfileService _ucpProfileService;

    public UcpProfileController(IUcpProfileService ucpProfileService)
    {
        _ucpProfileService = ucpProfileService;
    }

    [HttpGet]
    [Route("/.well-known/ucp")]
    [UcpOperation(ModuleConstants.Operations.GetStoreCapabilities)]
    [ProducesResponseType(typeof(UcpDiscoveryDocument), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status304NotModified)]
    public async Task<ActionResult<UcpDiscoveryDocument>> GetProfile(CancellationToken cancellationToken)
    {
        var profile = await _ucpProfileService.GetProfile(cancellationToken);
        var document = new UcpDiscoveryDocument
        {
            Ucp = profile.Ucp,
        };

        // A weak validator: it changes whenever the profile does, though the bytes are written by the MVC formatter.
        var etag = new EntityTagHeaderValue($"\"{ComputeHash(document)}\"", isWeak: true);
        var responseHeaders = Response.GetTypedHeaders();
        responseHeaders.CacheControl = new CacheControlHeaderValue { Public = true, MaxAge = ProfileMaxAge };
        responseHeaders.ETag = etag;

        var ifNoneMatch = Request.GetTypedHeaders().IfNoneMatch;
        if (ifNoneMatch?.Any(x => x.Equals(EntityTagHeaderValue.Any) || x.Compare(etag, useStrongComparison: false)) == true)
        {
            return StatusCode(StatusCodes.Status304NotModified);
        }

        return Ok(document);
    }

    private static string ComputeHash(UcpDiscoveryDocument document)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(document)));
        return Convert.ToHexString(hash, 0, ETagHashBytes).ToLowerInvariant();
    }
}
