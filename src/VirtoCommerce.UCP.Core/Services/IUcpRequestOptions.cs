using VirtoCommerce.UCP.Core.Options;

namespace VirtoCommerce.UCP.Core.Services;

// The UCP options of the current request: what the request's store provides (its id, currency, language and storefront
// URL, and the UCP settings of the store) in place of the configuration, which stays the fallback.
public interface IUcpRequestOptions
{
    UcpOptions Current { get; }
}
