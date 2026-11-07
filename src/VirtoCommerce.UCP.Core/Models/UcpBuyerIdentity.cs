namespace VirtoCommerce.UCP.Core.Models;

// The signed-in buyer as link_buyer_identity reports them. The names are null without the profile scope, the email
// without the email scope.
public class UcpBuyerIdentity
{
    public string BuyerId { get; set; }

    public string OrganizationId { get; set; }

    public string UserName { get; set; }

    public string BuyerName { get; set; }

    public string OrganizationName { get; set; }

    public string Email { get; set; }
}
