using System.Collections.Generic;
using Newtonsoft.Json;

namespace VirtoCommerce.UCP.Web.Models;

public sealed class UcpProtectedResourceMetadata
{
    [JsonProperty("resource")]
    public string Resource { get; set; }

    [JsonProperty("authorization_servers")]
    public IList<string> AuthorizationServers { get; set; } = [];

    [JsonProperty("bearer_methods_supported")]
    public IList<string> BearerMethodsSupported { get; set; } = ["header"];

    [JsonProperty("scopes_supported")]
    public IList<string> ScopesSupported { get; set; } = [];

    [JsonProperty("resource_name")]
    public string ResourceName { get; set; }
}
