using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Models;
using VirtoCommerce.UCP.Core.Services;

namespace VirtoCommerce.UCP.Web.Mcp;

[McpServerToolType]
public static class UcpMcpIdentityTools
{
    [McpServerTool(Name = ModuleConstants.McpTools.LinkBuyerIdentity, ReadOnly = true, Destructive = false)]
    [Description(
        "REQUIRED first step for an authenticated buyer request. Call this before search_products, get_product, create_cart, " +
        "cart, checkout, or order tools whenever the user says 'my account', 'my cart', 'on my behalf', 'from my name', " +
        "'for my organization', or asks for personalized prices, saved data, or orders. It starts standard Virto Commerce Platform OAuth; " +
        "after it succeeds, repeat the requested commerce operation. Do not call it when the user explicitly requests anonymous shopping.")]
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
}
