using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace VirtoCommerce.UCP.Web.Mcp.Models;

public sealed class UcpMcpToolError
{
    [JsonPropertyName("is_error")]
    public bool IsError { get; set; }

    [JsonPropertyName("code")]
    public string Code { get; set; }

    [JsonPropertyName("status_code")]
    public int StatusCode { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; }

    [JsonPropertyName("details")]
    public IDictionary<string, object> Details { get; set; }
}
