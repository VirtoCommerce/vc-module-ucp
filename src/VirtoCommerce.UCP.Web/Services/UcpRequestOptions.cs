using System.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using VirtoCommerce.Platform.Core.Settings;
using VirtoCommerce.StoreModule.Core.Model;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Options;
using VirtoCommerce.UCP.Core.Services;
using StoreSettings = VirtoCommerce.UCP.Core.ModuleConstants.Settings.Store;

namespace VirtoCommerce.UCP.Web.Services;

// The options of the request's store, set by UcpStoreContextMiddleware; the configuration when no store was resolved,
// for example outside an HTTP request.
public class UcpRequestOptions(IHttpContextAccessor httpContextAccessor, IOptions<UcpOptions> options) : IUcpRequestOptions
{
    internal const string StoreItemKey = "VirtoCommerce.UCP.Store";
    internal const string OptionsItemKey = "VirtoCommerce.UCP.Options";

    public UcpOptions Current => httpContextAccessor.HttpContext?.Items[OptionsItemKey] as UcpOptions ?? options.Value;

    public static Store GetStore(HttpContext context)
    {
        return context?.Items[StoreItemKey] as Store;
    }

    internal static void SetStore(HttpContext context, Store store, UcpOptions configured)
    {
        context.Items[StoreItemKey] = store;
        context.Items[OptionsItemKey] = Build(configured, store);
    }

    // The store's values win over the configuration: its id, currency, language and URL, and each UCP setting that is
    // set on the store. A setting left unset on the store keeps the configured value.
    public static UcpOptions Build(UcpOptions configured, Store store)
    {
        if (store == null)
        {
            return configured;
        }

        var storeUrl = FirstNotEmpty(store.SecureUrl, store.Url)?.TrimEnd('/');

        return new UcpOptions
        {
            DefaultStoreId = store.Id,
            DefaultCurrency = FirstNotEmpty(store.DefaultCurrency, configured.DefaultCurrency),
            DefaultCultureName = FirstNotEmpty(store.DefaultLanguage, configured.DefaultCultureName),
            StorefrontOrigin = FirstNotEmpty(storeUrl, configured.StorefrontOrigin?.TrimEnd('/')),
            UcpBaseUrl = storeUrl == null ? configured.UcpBaseUrl : storeUrl + "/ucp/v1",
            PublicOrigin = configured.PublicOrigin,
            HandoffUrlTemplate = GetStoreValue(store, StoreSettings.CheckoutUrlTemplate, configured.HandoffUrlTemplate),
            HandoffTokenTtlMinutes = GetStoreValue(store, StoreSettings.CheckoutLinkLifetimeMinutes, configured.HandoffTokenTtlMinutes),
            AnonymousCatalog = configured.AnonymousCatalog,
            StoreManagedAddresses = GetStoreValue(store, StoreSettings.StoreManagedAddresses, configured.StoreManagedAddresses),
            Observability = configured.Observability,
        };
    }

    private static T GetStoreValue<T>(Store store, SettingDescriptor descriptor, T configured)
    {
        var entry = store.Settings?.FirstOrDefault(x => x.Name == descriptor.Name);
        if (entry?.Value == null || (entry.Value is string text && string.IsNullOrWhiteSpace(text)))
        {
            return configured;
        }

        return SettingValueConverter.TryConvert<T>(entry.Value, out var value) ? value : configured;
    }

    private static string FirstNotEmpty(params string[] values)
    {
        return values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
    }
}
