using System.Collections.Generic;
using Newtonsoft.Json;

namespace VirtoCommerce.UCP.Core.Models;

public class UcpProfile
{
    [JsonProperty("ucp")]
    public UcpDiscoveryProfile Ucp { get; set; }

    [JsonProperty("ucp_version")]
    public string UcpVersion { get; set; }

    [JsonProperty("platform")]
    public string Platform { get; set; }

    [JsonProperty("storefront_origin")]
    public string StorefrontOrigin { get; set; }

    [JsonProperty("default_store_id")]
    public string DefaultStoreId { get; set; }

    [JsonProperty("store")]
    public UcpStoreProfile Store { get; set; }

    [JsonProperty("stores")]
    public IList<UcpStoreProfile> Stores { get; set; } = new List<UcpStoreProfile>();

    [JsonProperty("endpoints")]
    public UcpEndpointProfile Endpoints { get; set; }

    [JsonProperty("capabilities")]
    public IList<string> Capabilities { get; set; } = new List<string>();

    [JsonProperty("payment_handlers")]
    public IList<UcpPaymentHandlerProfile> PaymentHandlers { get; set; } = new List<UcpPaymentHandlerProfile>();

    [JsonProperty("auth")]
    public UcpProfileAuth Auth { get; set; }

    [JsonProperty("headers")]
    public UcpHeaderProfile Headers { get; set; }

    [JsonProperty("mcp_tools")]
    public IList<string> McpTools { get; set; } = new List<string>();

    [JsonProperty("agent_guidance")]
    public IList<string> AgentGuidance { get; set; } = new List<string>();

    [JsonProperty("store_managed_addresses")]
    public bool StoreManagedAddresses { get; set; }

    [JsonProperty("errors")]
    public UcpErrorProfile Errors { get; set; }
}
