using System.Threading.Tasks;

namespace VirtoCommerce.UCP.Core.Services;

public interface IUcpPublicOriginResolver
{
    Task<string> GetOriginAsync();
}
