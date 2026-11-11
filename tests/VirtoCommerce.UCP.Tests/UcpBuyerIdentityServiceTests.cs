using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Abstractions;
using VirtoCommerce.CustomerModule.Core.Model;
using VirtoCommerce.CustomerModule.Core.Services;
using VirtoCommerce.Platform.Core.Security;
using VirtoCommerce.UCP.Core.Models;
using VirtoCommerce.UCP.Core.Services;
using VirtoCommerce.UCP.Web.Mcp;
using VirtoCommerce.UCP.Web.Services;
using Xunit;

namespace VirtoCommerce.UCP.Tests;

[Trait("Category", "Unit")]
public class UcpBuyerIdentityServiceTests
{
    private const string UserId = "user-1";
    private const string ContactId = "contact-1";
    private const string OrganizationId = "org-1";

    private readonly FakeMemberService _memberService = new(
        new Contact { Id = ContactId, FullName = "Ann Buyer", Name = "Ann" },
        new Organization { Id = OrganizationId, Name = "Acme Corp" });

    [Fact]
    public async Task GetIdentity_Should_ReturnTheNames_When_TheProfileScopeIsGranted()
    {
        // Act
        var identity = await CreateService().GetIdentityAsync(CreateContext(OpenIddictConstants.Scopes.Profile));

        // Assert
        Assert.Equal(UserId, identity.BuyerId);
        Assert.Equal(OrganizationId, identity.OrganizationId);
        Assert.Equal("ann@example.com", identity.UserName);
        Assert.Equal("Ann Buyer", identity.BuyerName);
        Assert.Equal("Acme Corp", identity.OrganizationName);
        Assert.Null(identity.Email);
    }

    [Fact]
    public async Task GetIdentity_Should_ReturnTheEmail_When_TheEmailScopeIsGranted()
    {
        // Act
        var identity = await CreateService().GetIdentityAsync(CreateContext(OpenIddictConstants.Scopes.Profile, OpenIddictConstants.Scopes.Email));

        // Assert
        Assert.Equal("ann.work@example.com", identity.Email);
        Assert.Equal("Ann Buyer", identity.BuyerName);
    }

    [Fact]
    public async Task GetIdentity_Should_ReturnTheIdsOnly_When_NeitherScopeIsGranted()
    {
        // Act
        var identity = await CreateService().GetIdentityAsync(CreateContext(OpenIddictConstants.Scopes.OpenId));

        // Assert
        Assert.Equal(UserId, identity.BuyerId);
        Assert.Equal(OrganizationId, identity.OrganizationId);
        Assert.Null(identity.UserName);
        Assert.Null(identity.BuyerName);
        Assert.Null(identity.OrganizationName);
        Assert.Null(identity.Email);
        Assert.Equal(0, _memberService.Calls);
    }

    [Fact]
    public async Task GetIdentity_Should_ReturnTheIdsOnly_When_TheUserNoLongerExists()
    {
        // Arrange
        var context = CreateContext(OpenIddictConstants.Scopes.Profile, OpenIddictConstants.Scopes.Email);

        // Act
        var identity = await CreateService(userExists: false).GetIdentityAsync(context);

        // Assert
        Assert.Equal(UserId, identity.BuyerId);
        Assert.Null(identity.UserName);
        Assert.Null(identity.Email);
    }

    [Fact]
    public async Task GetIdentity_Should_LeaveTheOrganizationNameEmpty_When_TheBuyerHasNoOrganization()
    {
        // Arrange
        var context = CreateContext([OpenIddictConstants.Scopes.Profile], organizationId: null);

        // Act
        var identity = await CreateService().GetIdentityAsync(context);

        // Assert
        Assert.Equal("Ann Buyer", identity.BuyerName);
        Assert.Null(identity.OrganizationId);
        Assert.Null(identity.OrganizationName);
    }

    [Fact]
    public async Task LinkBuyerIdentity_Should_LeaveOutTheValuesTheBuyerDidNotShare()
    {
        // Arrange
        var accessor = new FixedBuyerContextAccessor(CreateContext(OpenIddictConstants.Scopes.Profile));

        // Act
        var result = (IDictionary<string, object>)await UcpMcpIdentityTools.LinkBuyerIdentity(accessor, CreateService(), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(true, result["linked"]);
        Assert.Equal(UserId, result["buyer_id"]);
        Assert.Equal("Ann Buyer", result["buyer_name"]);
        Assert.Equal("Acme Corp", result["organization_name"]);
        Assert.Equal("ann@example.com", result["user_name"]);
        Assert.False(result.ContainsKey("email"));
    }

    private UcpBuyerIdentityService CreateService(bool userExists = true)
    {
        var user = userExists ? new ApplicationUser { Id = UserId, UserName = "ann@example.com", Email = "ann.work@example.com", MemberId = ContactId } : null;
        return new UcpBuyerIdentityService(() => new UserManager<ApplicationUser>(new FakeUserStore(user), null, null, null, null, null, null, null, null), _memberService);
    }

    private static UcpBuyerContext CreateContext(params string[] scopes)
    {
        return CreateContext(scopes, OrganizationId);
    }

    private static UcpBuyerContext CreateContext(string[] scopes, string organizationId)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", UserId)], "Bearer"));
        principal.SetScopes(scopes);

        return new UcpBuyerContext
        {
            UserId = UserId,
            PublicBuyerId = UserId,
            OrganizationId = organizationId,
            Principal = principal,
            IsAuthenticated = true,
        };
    }

    private sealed class FixedBuyerContextAccessor(UcpBuyerContext context) : IUcpBuyerContextAccessor
    {
        public UcpBuyerContext Resolve(UcpBuyerContextRequest request = null) => context;
    }

    private sealed class FakeMemberService(params Member[] members) : IMemberService
    {
        public int Calls { get; private set; }

        public Task<Member[]> GetByIdsAsync(string[] memberIds, string responseGroup = null, string[] memberTypes = null)
        {
            Calls++;
            return Task.FromResult(members.Where(x => memberIds.Contains(x.Id)).ToArray());
        }

        public Task<Member> GetByIdAsync(string memberId, string responseGroup = null, string memberType = null) => throw new NotSupportedException();

        public Task SaveChangesAsync(Member[] members) => throw new NotSupportedException();

        public Task DeleteAsync(string[] ids, string[] memberTypes = null) => throw new NotSupportedException();
    }

    private sealed class FakeUserStore(ApplicationUser user) : IUserStore<ApplicationUser>
    {
        public Task<ApplicationUser> FindByIdAsync(string userId, CancellationToken cancellationToken) =>
            Task.FromResult(user != null && user.Id == userId ? user : null);

        public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken cancellationToken) => Task.FromResult(user.Id);

        public Task<string> GetUserNameAsync(ApplicationUser user, CancellationToken cancellationToken) => Task.FromResult(user.UserName);

        public Task SetUserNameAsync(ApplicationUser user, string userName, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task SetNormalizedUserNameAsync(ApplicationUser user, string normalizedName, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ApplicationUser> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) => throw new NotSupportedException();

        public void Dispose()
        {
            // nothing to release
        }
    }
}
