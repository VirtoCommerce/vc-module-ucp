using System;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using VirtoCommerce.UCP.Core;

namespace VirtoCommerce.UCP.Web;

internal static class UcpEnabledGateExtensions
{
    // Interim path-prefix gate: the module owns these prefixes, the platform pipeline has no per-module gate yet.
    private static readonly PathString[] _gatedPrefixes =
    [
        new("/ucp"),
        new(ModuleConstants.Endpoints.Discovery),
        new("/.well-known/oauth-protected-resource/ucp"),
    ];

    public static IApplicationBuilder UseUcpEnabledGate(this IApplicationBuilder app)
    {
        return app.UseWhen(
            context => IsGatedPath(context.Request.Path),
            branch => branch.UseMiddleware<UcpEnabledMiddleware>());
    }

    internal static bool IsGatedPath(PathString path)
    {
        return _gatedPrefixes.Any(prefix => path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));
    }
}
