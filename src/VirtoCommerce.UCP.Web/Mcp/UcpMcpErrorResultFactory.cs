using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Protocol;
using VirtoCommerce.UCP.Core.Services;
using VirtoCommerce.UCP.Web.Mcp.Models;

namespace VirtoCommerce.UCP.Web.Mcp;

internal static partial class UcpMcpErrorResultFactory
{
    public static CallToolResult FromXApi(XApiResponseException exception)
    {
        using var document = JsonDocument.Parse(exception.Result.Json);
        var structuredContent = document.RootElement.Clone();
        return new CallToolResult
        {
            IsError = true,
            StructuredContent = structuredContent,
            Content =
            [
                new TextContentBlock { Text = exception.Result.Json },
            ],
            Meta = new JsonObject
            {
                ["source"] = exception.Schema,
                ["error_count"] = exception.ErrorCount,
            },
        };
    }

    public static CallToolResult FromUcp(UcpException exception)
    {
        var safeCode = SafeErrorCodePattern().IsMatch(exception.Code ?? string.Empty) ? exception.Code : "ucp_error";
        var error = new UcpMcpToolError
        {
            IsError = true,
            Code = safeCode,
            StatusCode = exception.StatusCode,
            Message = exception.Message,
            Details = exception.Error?.Details,
        };
        var structuredContent = JsonSerializer.SerializeToElement(error, UcpMcpSerialization.Options);
        return new CallToolResult
        {
            IsError = true,
            StructuredContent = structuredContent,
            Content =
            [
                new TextContentBlock { Text = structuredContent.GetRawText() },
            ],
            Meta = new JsonObject
            {
                ["source"] = "ucp",
                ["error_code"] = safeCode,
                ["status_code"] = exception.StatusCode,
            },
        };
    }

    public static CallToolResult FromUnexpected()
    {
        var error = new UcpMcpToolError
        {
            IsError = true,
            Code = "internal_error",
            StatusCode = StatusCodes.Status500InternalServerError,
            Message = "An unexpected UCP error occurred.",
        };

        return new CallToolResult
        {
            IsError = true,
            StructuredContent = JsonSerializer.SerializeToElement(error, UcpMcpSerialization.Options),
            Content =
            [
                new TextContentBlock { Text = "UCP operation failed unexpectedly." },
            ],
            Meta = new JsonObject
            {
                ["source"] = "ucp",
                ["error_code"] = error.Code,
                ["status_code"] = error.StatusCode,
            },
        };
    }

    [GeneratedRegex("^[a-z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeErrorCodePattern();
}
