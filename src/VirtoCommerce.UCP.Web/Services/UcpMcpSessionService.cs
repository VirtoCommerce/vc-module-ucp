using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using OpenIddict.Abstractions;
using VirtoCommerce.Platform.Core.Security;
using VirtoCommerce.Platform.Security.Model.OpenIddict;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Services;

namespace VirtoCommerce.UCP.Web.Services;

public class UcpMcpSessionService
{
    private readonly IOpenIddictTokenManager _tokenManager;
    private readonly IUserSessionsService _userSessionsService;

    public UcpMcpSessionService(IOpenIddictTokenManager tokenManager, IUserSessionsService userSessionsService)
    {
        _tokenManager = tokenManager;
        _userSessionsService = userSessionsService;
    }

    public virtual async Task<bool> IsActive(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        var tokenId = principal.GetTokenId();
        if (string.IsNullOrEmpty(tokenId))
        {
            return true;
        }

        // Query the existing Platform token store directly: another replica may have revoked
        // the grant while OpenIddict's entity cache on this replica still contains it.
        return await _tokenManager.GetAsync(query => query
            .Cast<VirtoOpenIddictEntityFrameworkCoreToken>()
            .Where(token => token.Id == tokenId && token.Status == OpenIddictConstants.Statuses.Valid &&
                (token.Authorization == null || token.Authorization.Status == OpenIddictConstants.Statuses.Valid))
            .Select(token => true), cancellationToken);
    }

    public virtual async Task Logout(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var authorizationId = principal.GetAuthorizationId();
        if (string.IsNullOrEmpty(authorizationId))
        {
            throw new UcpException(ModuleConstants.ErrorCodes.InvalidRequest,
                "This token has no Platform OAuth authorization to sign out. Disconnect this MCP connection in your client.");
        }

        await _userSessionsService.TerminateUserSessionGroup(authorizationId);
    }
}
