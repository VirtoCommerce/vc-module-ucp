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
        if (!await IsEnabled())
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;

            return;
        }

        await _next(context);
    }

    // SettingsExtension.GetValueAsync ignores a configured DefaultValue override (it falls back to the descriptor default),
    // which would leave the gate open; the manager applies that override to the entry's DefaultValue.
    private async Task<bool> IsEnabled()
    {
        var descriptor = ModuleConstants.Settings.General.UcpEnabled;
        var entry = await _settingsManager.GetObjectSettingAsync(descriptor.Name);
        var rawValue = entry?.Value ?? entry?.DefaultValue;

        return SettingValueConverter.TryConvert<bool>(rawValue, out var enabled)
            ? enabled
            : descriptor.DefaultValue is true;
    }
}
