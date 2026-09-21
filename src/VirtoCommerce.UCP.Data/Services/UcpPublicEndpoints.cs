using Microsoft.AspNetCore.Http;
using VirtoCommerce.UCP.Core.Options;

namespace VirtoCommerce.UCP.Data.Services;

public static class UcpPublicEndpoints
{
    public static string GetOrigin(UcpOptions options, HttpRequest request)
    {
        if (!string.IsNullOrWhiteSpace(options?.PublicOrigin))
        {
            return options.PublicOrigin.TrimEnd('/');
        }

        return request == null ? null : $"{request.Scheme}://{request.Host}{request.PathBase}".TrimEnd('/');
    }
}
