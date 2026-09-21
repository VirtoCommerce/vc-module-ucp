using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using VirtoCommerce.UCP.Core.Options;
using VirtoCommerce.UCP.Web.Controllers.Api;
using VirtoCommerce.UCP.Web.Models;
using Xunit;

namespace VirtoCommerce.UCP.Tests;

[Trait("Category", "Unit")]
public class UcpOAuthMetadataControllerTests
{
    [Fact]
    public void GetProtectedResourceMetadata_UsesCanonicalPublicOriginOnBackendHost()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("backend.example");
        var controller = new UcpOAuthMetadataController(Options.Create(new UcpOptions { PublicOrigin = "https://shop.example/" }))
        {
            ControllerContext = new ControllerContext { HttpContext = context },
        };
        var metadata = Assert.IsType<UcpProtectedResourceMetadata>(Assert.IsType<OkObjectResult>(controller.GetProtectedResourceMetadata().Result).Value);
        Assert.Equal("https://shop.example/ucp/mcp", metadata.Resource);
        Assert.Equal(["https://shop.example/"], metadata.AuthorizationServers);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/platform")]
    public void GetProtectedResourceMetadata_PointsToPlatformAuthorizationServer(string pathBase)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("store.example");
        httpContext.Request.PathBase = pathBase;
        var controller = new UcpOAuthMetadataController
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };

        var action = controller.GetProtectedResourceMetadata();
        var result = Assert.IsType<OkObjectResult>(action.Result);
        var metadata = Assert.IsType<UcpProtectedResourceMetadata>(result.Value);

        Assert.Equal($"https://store.example{pathBase}/ucp/mcp", metadata.Resource);
        Assert.Equal([$"https://store.example{pathBase}/"], metadata.AuthorizationServers);
        Assert.Equal(["openid", "profile", "offline_access"], metadata.ScopesSupported);
        Assert.Equal(["header"], metadata.BearerMethodsSupported);
    }
}
