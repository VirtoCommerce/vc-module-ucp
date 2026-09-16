using Newtonsoft.Json;

namespace VirtoCommerce.UCP.Core.Models;

public class UcpHandoffRestoreResponse
{
    [JsonProperty("ucp")]
    public UcpResponseMetadata Ucp { get; set; }

    [JsonProperty("checkout")]
    public UcpCheckout Checkout { get; set; }

    [JsonProperty("anonymous_buyer_id")]
    public string AnonymousBuyerId { get; set; }
}
