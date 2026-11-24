using System.Collections.Generic;
using Newtonsoft.Json;

namespace VirtoCommerce.UCP.Core.Models;

public class UcpOrderListResponse
{
    [JsonProperty("ucp")]
    public UcpResponseMetadata Ucp { get; set; }

    // Newest first.
    [JsonProperty("orders")]
    public IList<UcpOrder> Orders { get; set; } = new List<UcpOrder>();

    [JsonProperty("pagination")]
    public UcpPaginationResponse Pagination { get; set; }

    [JsonProperty("messages")]
    public IList<UcpMessage> Messages { get; set; } = new List<UcpMessage>();
}
