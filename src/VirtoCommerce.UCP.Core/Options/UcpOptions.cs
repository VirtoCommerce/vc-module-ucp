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
    public int HandoffTokenTtlMinutes { get; set; } = 15;
    public bool AnonymousCatalog { get; set; } = true;
    public UcpObservabilityOptions Observability { get; set; } = new();
}
