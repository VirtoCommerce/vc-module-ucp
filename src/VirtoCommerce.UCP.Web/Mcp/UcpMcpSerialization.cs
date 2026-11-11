using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using VirtoCommerce.MCP.Core;
using VirtoCommerce.UCP.Web.Mcp.Models.Standard;

namespace VirtoCommerce.UCP.Web.Mcp;

internal static class UcpMcpSerialization
{
    // The options the MCP module serializes every tool with, also for the payloads UCP builds itself (structured content
    // of errors), so a result reads the same whichever way it is written.
    public static JsonSerializerOptions Options => McpSerialization.Options;

    // The payloads of the standard UCP tools: as Options, and a collection marked [UcpOmitWhenEmpty] is left out when it
    // is empty, as when it is null.
    public static JsonSerializerOptions StandardPayloadOptions { get; } = new(McpSerialization.Options)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers = { OmitEmptyCollections },
        },
    };

    private static void OmitEmptyCollections(JsonTypeInfo typeInfo)
    {
        foreach (var property in typeInfo.Properties)
        {
            if (property.AttributeProvider?.IsDefined(typeof(UcpOmitWhenEmptyAttribute), inherit: true) == true)
            {
                property.ShouldSerialize = static (_, value) => value is not null and not ICollection { Count: 0 };
            }
        }
    }
}
