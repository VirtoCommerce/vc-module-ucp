using VirtoCommerce.UCP.Core.Models;

namespace VirtoCommerce.UCP.Core.Services;

public interface IUcpBuyerContextAccessor
{
    UcpBuyerContext Resolve(UcpBuyerContextRequest request = null);
}
