using System.Collections.Generic;
using Newtonsoft.Json;

namespace VirtoCommerce.UCP.Core.Models;

public class UcpProfileAuth
{
    [JsonProperty("agent")]
    public string Agent { get; set; }

    [JsonProperty("anonymous_catalog")]
    public bool AnonymousCatalog { get; set; }

    [JsonProperty("buyer_delegation")]
    public string BuyerDelegation { get; set; }

    [JsonProperty("buyer_identity_source")]
    public string BuyerIdentitySource { get; set; }

    [JsonProperty("protected_resource_metadata")]
    public string ProtectedResourceMetadata { get; set; }

    [JsonProperty("authorization_server")]
    public string AuthorizationServer { get; set; }

    [JsonProperty("scopes")]
    public IList<string> Scopes { get; set; } = new List<string>();
}
