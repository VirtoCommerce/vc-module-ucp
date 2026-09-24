using System.Security.Claims;

namespace VirtoCommerce.UCP.Data.Models;

internal sealed class CartExecutionRequest
{
    public string CartId { get; set; }
    public string StoreId { get; set; }
    public string Currency { get; set; }
    public string CultureName { get; set; }
    public string CartName { get; set; }
    public string CartType { get; set; }
    public string UserId { get; set; }
    public string SourceAnonymousBuyerId { get; set; }
    public string OrganizationId { get; set; }
    public ClaimsPrincipal Principal { get; set; }
    public bool IsAuthenticated { get; set; }
}
