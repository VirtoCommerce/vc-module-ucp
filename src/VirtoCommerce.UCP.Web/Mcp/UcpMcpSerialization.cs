using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using VirtoCommerce.UCP.Web.Mcp.Models.Standard;

namespace VirtoCommerce.UCP.Web.Mcp;

internal static class UcpMcpSerialization
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    // The payloads of the standard UCP tools: as Options, and a collection marked [UcpOmitWhenEmpty] is left out when it
    // is empty, as when it is null.
    public static JsonSerializerOptions StandardPayloadOptions { get; } = new(Options)
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
