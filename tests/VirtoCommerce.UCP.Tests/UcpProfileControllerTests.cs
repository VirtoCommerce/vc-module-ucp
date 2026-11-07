using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using VirtoCommerce.UCP.Core.Models;
using VirtoCommerce.UCP.Core.Services;
using VirtoCommerce.UCP.Web.Controllers.Api;
using Xunit;

namespace VirtoCommerce.UCP.Tests;

[Trait("Category", "Unit")]
public class UcpProfileControllerTests
{
    [Fact]
    public async Task GetProfile_Should_BeCacheablePubliclyWithAValidator()
    {
        // Act
        var (result, response) = await GetProfile(new StubProfileService("2026-04-08"));

        // Assert
        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal("public, max-age=300", response.Headers.CacheControl.ToString());
        Assert.StartsWith("W/\"", response.Headers.ETag.ToString());
    }

    [Fact]
    public async Task GetProfile_Should_AnswerNotModified_When_TheClientHasTheSameProfile()
    {
        // Arrange
        var service = new StubProfileService("2026-04-08");
        var (_, first) = await GetProfile(service);

        // Act
        var (result, response) = await GetProfile(service, ifNoneMatch: first.Headers.ETag.ToString());

        // Assert
        Assert.Equal(StatusCodes.Status304NotModified, Assert.IsType<StatusCodeResult>(result.Result).StatusCode);
        Assert.Equal(first.Headers.ETag.ToString(), response.Headers.ETag.ToString());
        Assert.Equal("public, max-age=300", response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task GetProfile_Should_ChangeTheValidator_When_TheProfileChanges()
    {
        // Act
        var (_, before) = await GetProfile(new StubProfileService("2026-04-08"));
        var (result, after) = await GetProfile(new StubProfileService("2026-08-25"), ifNoneMatch: before.Headers.ETag.ToString());

        // Assert
        Assert.IsType<OkObjectResult>(result.Result);
        Assert.NotEqual(before.Headers.ETag.ToString(), after.Headers.ETag.ToString());
    }

    private static async Task<(ActionResult<UcpDiscoveryDocument> Result, HttpResponse Response)> GetProfile(IUcpProfileService service, string ifNoneMatch = null)
    {
        var httpContext = new DefaultHttpContext();
        if (ifNoneMatch != null)
        {
            httpContext.Request.Headers.IfNoneMatch = ifNoneMatch;
        }

        var controller = new UcpProfileController(service)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };

        var result = await controller.GetProfile(TestContext.Current.CancellationToken);
        return (result, httpContext.Response);
    }

    private sealed class StubProfileService(string version) : IUcpProfileService
    {
        public Task<UcpProfile> GetProfile(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new UcpProfile
            {
                Ucp = new UcpDiscoveryProfile
                {
                    Version = version,
                    Services = new Dictionary<string, IList<UcpDiscoveryServiceProfile>>(),
                },
            });
        }
    }
}
