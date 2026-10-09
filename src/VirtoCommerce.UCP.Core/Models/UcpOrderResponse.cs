using System.Collections.Generic;
using Newtonsoft.Json;

namespace VirtoCommerce.UCP.Core.Models;

public class UcpOrderResponse
{
    [JsonProperty("ucp")]
    public UcpResponseMetadata Ucp { get; set; }

    [JsonProperty("order")]
    public UcpOrder Order { get; set; }

    [JsonProperty("orders")]
    public IList<UcpOrder> Orders { get; set; } = new List<UcpOrder>();

    [JsonProperty("messages")]
    public IList<UcpMessage> Messages { get; set; } = new List<UcpMessage>();
}
