using System;

namespace VirtoCommerce.UCP.Web.Mcp.Models.Standard;

/// <summary>
/// An optional collection of a standard UCP payload that is left out of the JSON when it is empty, as when it is null
/// (UcpMcpSerialization.StandardPayloadOptions). Required collections, such as links or line_items, are not marked.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class UcpOmitWhenEmptyAttribute : Attribute
{
}
