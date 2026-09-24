using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using OpenIddict.Abstractions;
using VirtoCommerce.Platform.Core.Security;
using VirtoCommerce.UCP.Core.Services;
using VirtoCommerce.UCP.Web.Mcp;
using VirtoCommerce.UCP.Web.Services;
using Xunit;

namespace VirtoCommerce.UCP.Tests;

[Trait("Category", "Unit")]
public class UcpMcpLogoutTests
{
    [Fact]
    public async Task Logout_RevokesOnlyTheAuthorizationFromTheValidatedPrincipal()
    {
        var sessions = new RecordingSessionsService();
        var service = new UcpMcpSessionService(null, sessions);
        var principal = new ClaimsPrincipal(new ClaimsIdentity("Bearer"));
        principal.SetAuthorizationId("current-authorization");

        await service.Logout(principal, TestContext.Current.CancellationToken);

        Assert.Equal("current-authorization", sessions.RevokedGroupId);
    }

    [Fact]
    public async Task Logout_WithoutAuthorizationDoesNotRevokeAllUserSessions()
    {
        var sessions = new RecordingSessionsService();
        var service = new UcpMcpSessionService(null, sessions);

        await Assert.ThrowsAsync<UcpException>(() => service.Logout(
            new ClaimsPrincipal(new ClaimsIdentity("Bearer")), TestContext.Current.CancellationToken));

        Assert.Null(sessions.RevokedGroupId);
    }

    [Fact]
    public async Task Logout_CanceledRequestDoesNotRevokeAuthorization()
    {
        var sessions = new RecordingSessionsService();
        var service = new UcpMcpSessionService(null, sessions);
        var principal = new ClaimsPrincipal(new ClaimsIdentity("Bearer"));
        principal.SetAuthorizationId("current-authorization");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.Logout(principal, new CancellationToken(true)));

        Assert.Null(sessions.RevokedGroupId);
    }

    [Fact]
    public async Task LogoutBuyer_AlreadyAnonymousDoesNotRequireAuthorization()
    {
        var context = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };

        var result = await UcpMcpIdentityTools.LogoutBuyer(context, null, TestContext.Current.CancellationToken);

        Assert.True((bool)result.GetType().GetProperty("logged_out").GetValue(result));
    }

    private sealed class RecordingSessionsService : IUserSessionsService
    {
        public string RevokedGroupId { get; private set; }

        public Task TerminateUserSessionGroup(string sessionGroupId)
        {
            RevokedGroupId = sessionGroupId;
            return Task.CompletedTask;
        }

        public Task TerminateUserSession(string sessionId) => throw new NotSupportedException();
        public Task TerminateAllUserSessions(string userId) => throw new NotSupportedException();
        public Task TerminateUserSessions(TerminateUserSessionsRequest request) => throw new NotSupportedException();
    }
}
