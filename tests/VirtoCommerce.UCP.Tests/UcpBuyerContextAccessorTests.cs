using System.Linq;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Models;
using VirtoCommerce.UCP.Core.Services;
using VirtoCommerce.UCP.Data.Services;
using Xunit;

namespace VirtoCommerce.UCP.Tests;

[Trait("Category", "Unit")]
public class UcpBuyerContextAccessorTests
{
    [Fact]
    public void Resolve_WithoutPlatformTokenCreatesAnonymousBuyer()
    {
        var accessor = CreateAccessor();

        var context = accessor.Resolve(new UcpBuyerContextRequest { CreateAnonymousBuyer = true });

        Assert.False(context.IsAuthenticated);
        Assert.StartsWith("ucp-anonymous-", context.PublicBuyerId);
        Assert.False(context.Principal.Identity?.IsAuthenticated);
        Assert.Null(context.OrganizationId);
    }

    [Fact]
    public void Resolve_UsesAuthenticatedBuyerWheneverPlatformTokenIsPresent()
    {
        var accessor = CreateAccessor(CreateAuthenticatedPrincipal());

        var context = accessor.Resolve(new UcpBuyerContextRequest { CreateAnonymousBuyer = true });

        Assert.True(context.IsAuthenticated);
        Assert.Equal("user-1", context.PublicBuyerId);
        Assert.Equal("org-1", context.OrganizationId);
    }

    [Fact]
    public void Resolve_AuthenticatedRequestUsesOnlyPlatformClaims()
    {
        var accessor = CreateAccessor(CreateAuthenticatedPrincipal());

        var context = accessor.Resolve(new UcpBuyerContextRequest
        {
            RequestedBuyerIds = ["user-1"],
            RequestedOrganizationIds = ["org-1"],
        });

        Assert.True(context.IsAuthenticated);
        Assert.Equal("user-1", context.UserId);
        Assert.Equal("org-1", context.OrganizationId);
        Assert.Equal("desktop-client", context.AgentId);
        Assert.Equal("trace-buyer", context.CorrelationId);
        Assert.Same(context.Principal.Identity, context.Principal.Identities.Single());
    }

    [Fact]
    public void Resolve_RequiredIdentityWithoutTokenReturnsIdentityRequired()
    {
        var accessor = CreateAccessor();

        var exception = Assert.Throws<UcpException>(() => accessor.Resolve(new UcpBuyerContextRequest
        {
            RequireAuthenticatedBuyer = true,
        }));

        Assert.Equal(ModuleConstants.ErrorCodes.IdentityRequired, exception.Code);
        Assert.Equal(StatusCodes.Status401Unauthorized, exception.StatusCode);
    }

    [Fact]
    public void Resolve_RequiredIdentityRejectsAgentOnlyPlatformTokenAsBuyer()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "desktop-client"),
            new Claim("client_id", "desktop-client"),
        ], "Bearer"));
        var accessor = CreateAccessor(principal);

        var exception = Assert.Throws<UcpException>(() => accessor.Resolve(new UcpBuyerContextRequest
        {
            RequireAuthenticatedBuyer = true,
        }));

        Assert.Equal(ModuleConstants.ErrorCodes.IdentityRequired, exception.Code);
        Assert.Equal(StatusCodes.Status401Unauthorized, exception.StatusCode);
    }

    [Theory]
    [InlineData("other-user", "org-1")]
    [InlineData("user-1", "other-org")]
    public void Resolve_AuthenticatedRequestRejectsBuyerOrOrganizationMismatch(string buyerId, string organizationId)
    {
        var accessor = CreateAccessor(CreateAuthenticatedPrincipal());

        var exception = Assert.Throws<UcpException>(() => accessor.Resolve(new UcpBuyerContextRequest
        {
            RequestedBuyerIds = [buyerId],
            RequestedOrganizationIds = [organizationId],
        }));

        Assert.Equal(ModuleConstants.ErrorCodes.BuyerContextMismatch, exception.Code);
        Assert.Equal(StatusCodes.Status403Forbidden, exception.StatusCode);
    }

    [Fact]
    public void Resolve_RejectsLegacyBuyerIdentityHeaders()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers[ModuleConstants.Headers.BuyerUserId] = "forged-user";
        var accessor = new UcpBuyerContextAccessor(new HttpContextAccessor { HttpContext = httpContext });

        var exception = Assert.Throws<UcpException>(() => accessor.Resolve());

        Assert.Equal(ModuleConstants.ErrorCodes.BuyerContextMismatch, exception.Code);
        Assert.Equal(StatusCodes.Status403Forbidden, exception.StatusCode);
    }

    [Fact]
    public void Resolve_RejectsArbitraryAnonymousBuyerIdentifier()
    {
        var accessor = CreateAccessor();

        var exception = Assert.Throws<UcpException>(() => accessor.Resolve(new UcpBuyerContextRequest
        {
            RequestedBuyerIds = ["user-1"],
        }));

        Assert.Equal(ModuleConstants.ErrorCodes.BuyerContextMismatch, exception.Code);
    }

    [Fact]
    public void Resolve_UsesTokenIdentityWithoutAClientSelectedMode()
    {
        var accessor = CreateAccessor(CreateAuthenticatedPrincipal());

        var context = accessor.Resolve(new UcpBuyerContextRequest
        {
            RequestedBuyerIds = ["user-1"],
        });

        Assert.True(context.IsAuthenticated);
    }

    [Fact]
    public void Resolve_AuthenticatedTokenCarriesAnonymousSourceForExplicitUpgrade()
    {
        const string anonymousBuyerId = "ucp-anonymous-33333333333333333333333333333333";
        var accessor = CreateAccessor(CreateAuthenticatedPrincipal());

        var context = accessor.Resolve(new UcpBuyerContextRequest
        {
            RequestedBuyerIds = [anonymousBuyerId],
            RequestedOrganizationIds = ["org-1"],
        });

        Assert.Equal("user-1", context.UserId);
        Assert.Equal(anonymousBuyerId, context.SourceAnonymousBuyerId);
        Assert.True(context.IsAuthenticated);
    }

    private static UcpBuyerContextAccessor CreateAccessor(ClaimsPrincipal principal = null)
    {
        return new UcpBuyerContextAccessor(new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = principal ?? new ClaimsPrincipal(new ClaimsIdentity()),
                TraceIdentifier = "trace-buyer",
            },
        });
    }

    private static ClaimsPrincipal CreateAuthenticatedPrincipal()
    {
        return new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "user-1"),
            new Claim(ClaimTypes.NameIdentifier, "user-1"),
            new Claim("organization_id", "org-1"),
            new Claim("client_id", "desktop-client"),
        ], "Bearer"));
    }
}
