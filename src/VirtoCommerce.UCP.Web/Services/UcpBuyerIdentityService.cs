using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Abstractions;
using VirtoCommerce.CustomerModule.Core.Model;
using VirtoCommerce.CustomerModule.Core.Services;
using VirtoCommerce.Platform.Core.Security;
using VirtoCommerce.UCP.Core.Models;

namespace VirtoCommerce.UCP.Web.Services;

// The names of the signed-in buyer and their organization, so the assistant can tell the user which account it acts
// for. The names need the profile scope and the email the email scope the buyer consented to.
public class UcpBuyerIdentityService(Func<UserManager<ApplicationUser>> userManagerFactory, IMemberService memberService)
{
    public virtual Task<UcpBuyerIdentity> GetIdentityAsync(UcpBuyerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return GetIdentityCoreAsync(context);
    }

    protected virtual async Task<UcpBuyerIdentity> GetIdentityCoreAsync(UcpBuyerContext context)
    {
        var identity = new UcpBuyerIdentity
        {
            BuyerId = context.PublicBuyerId,
            OrganizationId = context.OrganizationId,
        };

        var withProfile = context.Principal?.HasScope(OpenIddictConstants.Scopes.Profile) == true;
        var withEmail = context.Principal?.HasScope(OpenIddictConstants.Scopes.Email) == true;
        if (!context.IsAuthenticated || (!withProfile && !withEmail))
        {
            return identity;
        }

        using var userManager = userManagerFactory();
        var user = await userManager.FindByIdAsync(context.UserId);
        if (user == null)
        {
            return identity;
        }

        if (withEmail)
        {
            identity.Email = user.Email;
        }

        if (withProfile)
        {
            identity.UserName = user.UserName;
            await SetNamesAsync(identity, user.MemberId, context.OrganizationId);
        }

        return identity;
    }

    protected virtual async Task SetNamesAsync(UcpBuyerIdentity identity, string contactId, string organizationId)
    {
        var memberIds = new[] { contactId, organizationId }
            .Where(x => !string.IsNullOrEmpty(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (memberIds.Length == 0)
        {
            return;
        }

        var members = await memberService.GetByIdsAsync(memberIds) ?? [];
        var contact = members.FirstOrDefault(x => string.Equals(x.Id, contactId, StringComparison.OrdinalIgnoreCase));
        identity.BuyerName = (contact as Contact)?.FullName ?? contact?.Name;
        identity.OrganizationName = members
            .OfType<Organization>()
            .FirstOrDefault(x => string.Equals(x.Id, organizationId, StringComparison.OrdinalIgnoreCase))?.Name;
    }
}
