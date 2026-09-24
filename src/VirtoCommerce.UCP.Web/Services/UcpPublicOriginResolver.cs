using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using VirtoCommerce.UCP.Core.Options;
using VirtoCommerce.UCP.Core.Services;
using VirtoCommerce.Xapi.Core.Models;
using VirtoCommerce.Xapi.Core.Services;

namespace VirtoCommerce.UCP.Web.Services;

public class UcpPublicOriginResolver : IUcpPublicOriginResolver
{
    private readonly UcpOptions _options;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IStoreDomainResolverService _storeDomainResolver;

    public UcpPublicOriginResolver(
        IOptions<UcpOptions> options,
        IHttpContextAccessor httpContextAccessor,
        IStoreDomainResolverService storeDomainResolver)
    {
        _options = options.Value;
        _httpContextAccessor = httpContextAccessor;
        _storeDomainResolver = storeDomainResolver;
    }

    public virtual async Task<string> GetOriginAsync()
    {
        if (!string.IsNullOrWhiteSpace(_options.PublicOrigin))
        {
            return _options.PublicOrigin.TrimEnd('/');
        }

        var request = _httpContextAccessor.HttpContext?.Request;
        var store = await _storeDomainResolver.GetStoreAsync(new StoreDomainRequest
        {
            StoreId = _options.DefaultStoreId,
            Domain = request?.Host.Host,
        });

        if (!string.IsNullOrWhiteSpace(store?.SecureUrl))
        {
            return store.SecureUrl.TrimEnd('/');
        }

        if (!string.IsNullOrWhiteSpace(store?.Url))
        {
            return store.Url.TrimEnd('/');
        }

        return request == null ? null : $"{request.Scheme}://{request.Host}{request.PathBase}".TrimEnd('/');
    }
}
