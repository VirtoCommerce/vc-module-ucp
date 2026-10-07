using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using VirtoCommerce.Platform.Core.Settings;
using VirtoCommerce.UCP.Core;

namespace VirtoCommerce.UCP.Web;

/// <summary>
/// Answers 404 on every UCP surface while the <c>UCP.Enabled</c> platform setting is off.
/// </summary>
internal sealed class UcpEnabledMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ISettingsManager _settingsManager;

    public UcpEnabledMiddleware(RequestDelegate next, ISettingsManager settingsManager)
    {
        _next = next;
        _settingsManager = settingsManager;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var enabled = await _settingsManager.GetValueAsync<bool>(ModuleConstants.Settings.General.UcpEnabled);
        if (!enabled)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;

            return;
        }

        await _next(context);
    }
}
