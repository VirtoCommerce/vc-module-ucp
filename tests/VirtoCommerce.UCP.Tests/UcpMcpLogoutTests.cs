using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using VirtoCommerce.MCP.Core.Services;
using VirtoCommerce.UCP.Web.Mcp;
using Xunit;

namespace VirtoCommerce.UCP.Tests;

[Trait("Category", "Unit")]
public class UcpMcpLogoutTests
{
    [Fact]
    public async Task LogoutBuyer_SignsOutTheValidatedPrincipal()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity("Bearer"));
        var context = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal } };
        var sessions = new RecordingSessionService();

        var result = await UcpMcpIdentityTools.LogoutBuyer(context, sessions, TestContext.Current.CancellationToken);

        Assert.Same(principal, sessions.SignedOut);
        Assert.True((bool)result.GetType().GetProperty("logged_out").GetValue(result));
    }

    [Fact]
    public async Task LogoutBuyer_AlreadyAnonymousDoesNotRequireAuthorization()
    {
        var context = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var sessions = new RecordingSessionService();

        var result = await UcpMcpIdentityTools.LogoutBuyer(context, sessions, TestContext.Current.CancellationToken);

        Assert.Null(sessions.SignedOut);
        Assert.True((bool)result.GetType().GetProperty("logged_out").GetValue(result));
    }

    [Fact]
    public async Task LogoutBuyer_CanceledRequestDoesNotSignOut()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity("Bearer"));
        var context = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal } };
        var sessions = new RecordingSessionService();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            UcpMcpIdentityTools.LogoutBuyer(context, sessions, new CancellationToken(true)));

        Assert.Null(sessions.SignedOut);
    }

    private sealed class RecordingSessionService : IMcpSessionService
    {
        public ClaimsPrincipal SignedOut { get; private set; }

        public Task<bool> IsActiveAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task SignOutAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
        {
            SignedOut = principal;
            return Task.CompletedTask;
        }
    }
}
