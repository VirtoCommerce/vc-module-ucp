using Newtonsoft.Json;

namespace VirtoCommerce.UCP.Core.Models;

public class UcpDiscoveryServiceProfile
{
    [JsonProperty("version")]
    public string Version { get; set; }

    [JsonProperty("spec", NullValueHandling = NullValueHandling.Ignore)]
    public string Spec { get; set; }

    [JsonProperty("transport")]
    public string Transport { get; set; }

    [JsonProperty("schema", NullValueHandling = NullValueHandling.Ignore)]
    public string Schema { get; set; }

    [JsonProperty("endpoint")]
    public string Endpoint { get; set; }
}
