using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Server;
using OpenIddict.Abstractions;
using VirtoCommerce.MCP.Core.Attributes;
using VirtoCommerce.MCP.Core.Services;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Models;
using VirtoCommerce.UCP.Core.Services;

namespace VirtoCommerce.UCP.Web.Mcp;

[McpServerToolType]
[McpScope(ModuleConstants.McpScopeName)]
public static class UcpMcpIdentityTools
{
    // [McpRequiresUser] lists the tool to anonymous buyers and challenges their call, so the client signs the buyer in;
    // [McpAnyUser] declares that every signed-in buyer may call it.
    [McpServerTool(Name = ModuleConstants.McpTools.LinkBuyerIdentity, ReadOnly = true, Destructive = false)]
    [McpRequiresUser]
    [McpAnyUser]
    [Description(
        "REQUIRED first step for an authenticated buyer request. Call this before search_products, get_product, create_cart, " +
        "cart, checkout, or order tools whenever the user says 'my account', 'my cart', 'on my behalf', 'from my name', " +
        "'for my organization', or asks for personalized prices, saved data, or orders. Without a valid bearer token, the MCP client " +
        "starts standard Virto Commerce Platform OAuth and retries this call. With a valid token, this tool returns its current buyer " +
        "and organization; it does not sign out, switch accounts, or force a new browser login. To switch buyers, reconnect the MCP " +
        "client with a new OAuth authorization, then verify the returned identity before continuing. Signing out of the storefront " +
        "does not switch this MCP connection. After it succeeds, repeat the requested commerce operation. " +
        "Do not call it when the user explicitly requests anonymous shopping.")]
    public static Task<object> LinkBuyerIdentity(
        IUcpBuyerContextAccessor buyerContextAccessor,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = buyerContextAccessor.Resolve(new UcpBuyerContextRequest
        {
            RequireBuyer = true,
            RequireAuthenticatedBuyer = true,
        });

        return Task.FromResult<object>(new
        {
            linked = true,
            buyer_id = context.PublicBuyerId,
            organization_id = context.OrganizationId,
            identity_source = "platform_oauth",
        });
    }

    // Callable without a token, so an assistant that has none can still finish a sign-out.
    [McpServerTool(Name = ModuleConstants.McpTools.LogoutBuyer, ReadOnly = false, Destructive = true, Idempotent = true)]
    [AllowAnonymous]
    [Description(
        "Sign out the current buyer from this MCP OAuth application. Call when the user asks to log out. " +
        "Revokes the current Platform OAuth authorization and its access/refresh tokens, including other connections sharing that authorization. " +
        "Does not sign out other applications or clear browser cookies. After success, stop using saved buyer, organization, cart, and checkout identifiers. " +
        "Do not call link_buyer_identity until the user explicitly asks to sign in again. Never ask for passwords or tokens in chat.")]
    public static async Task<object> LogoutBuyer(
        IHttpContextAccessor httpContextAccessor,
        IMcpSessionService sessionService,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var principal = httpContextAccessor.HttpContext.User;
        if (principal.Identity?.IsAuthenticated == true)
        {
            // The session service revokes the token's authorization, or the token itself; a token that names neither
            // cannot be revoked, and the client keeps using it until it expires.
            if (string.IsNullOrEmpty(principal.GetAuthorizationId()) && string.IsNullOrEmpty(principal.GetTokenId()))
            {
                return new
                {
                    logged_out = false,
                    instructions = "The current sign-in cannot be revoked from this connection. To sign out, disconnect the MCP client " +
                        "or reconnect it without signing in. Stop using the previous buyer, organization, cart, and checkout identifiers.",
                };
            }

            await sessionService.SignOutAsync(principal, cancellationToken);
        }

        return new
        {
            logged_out = true,
            instructions = "Stop using the previous buyer, organization, cart, and checkout identifiers. " +
                "Do not start a new login until the user explicitly asks to sign in again.",
        };
    }
}
