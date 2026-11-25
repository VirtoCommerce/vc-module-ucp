using VirtoCommerce.UCP.Core;

namespace VirtoCommerce.UCP.Core.Options;

public class UcpOptions
{
    public string DefaultStoreId { get; set; }
    public string DefaultCurrency { get; set; }
    public string DefaultCultureName { get; set; }
    public string StorefrontOrigin { get; set; }
    public string UcpBaseUrl { get; set; }
    public string PublicOrigin { get; set; }
    public string HandoffUrlTemplate { get; set; }
    public int HandoffTokenTtlMinutes { get; set; } = ModuleConstants.Settings.Store.DefaultCheckoutLinkLifetimeMinutes;
    public bool AnonymousCatalog { get; set; } = true;
    public bool StoreManagedAddresses { get; set; }
    // list_orders: the page size when no limit is passed, the largest page, and how many orders one call reads at most
    // while unpaid_only or paid_only filters them.
    public int OrderListDefaultLimit { get; set; } = ModuleConstants.Settings.Store.DefaultOrderListLimit;
    public int OrderListMaxLimit { get; set; } = ModuleConstants.Settings.Store.DefaultOrderListMaxLimit;
    public int OrderListMaxScan { get; set; } = ModuleConstants.Settings.Store.DefaultOrderListMaxScan;
    public UcpObservabilityOptions Observability { get; set; } = new();
}
