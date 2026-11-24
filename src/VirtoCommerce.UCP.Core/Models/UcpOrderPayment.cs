using Newtonsoft.Json;

namespace VirtoCommerce.UCP.Core.Models;

public class UcpOrderPayment
{
    [JsonProperty("id")]
    public string Id { get; set; }

    [JsonProperty("number")]
    public string Number { get; set; }

    [JsonProperty("gateway_code")]
    public string GatewayCode { get; set; }

    [JsonProperty("status")]
    public string Status { get; set; }

    [JsonProperty("method_code")]
    public string MethodCode { get; set; }

    [JsonProperty("method_name")]
    public string MethodName { get; set; }

    [JsonProperty("approved")]
    public bool Approved { get; set; }

    [JsonProperty("amount")]
    public UcpMoney Amount { get; set; }

    [JsonProperty("billing_address")]
    public UcpOrderAddress BillingAddress { get; set; }
}
