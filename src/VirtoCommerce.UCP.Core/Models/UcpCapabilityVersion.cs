using Newtonsoft.Json;

namespace VirtoCommerce.UCP.Core.Models;

public class UcpCapabilityVersion
{
    [JsonProperty("version")]
    public string Version { get; set; }

    // A capability of a business profile names its spec and schema; a response's capability list carries the version only.
    [JsonProperty("spec", NullValueHandling = NullValueHandling.Ignore)]
    public string Spec { get; set; }

    [JsonProperty("schema", NullValueHandling = NullValueHandling.Ignore)]
    public string Schema { get; set; }

    [JsonProperty("config", NullValueHandling = NullValueHandling.Ignore)]
    public object Config { get; set; }
}
