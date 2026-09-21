using System;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.StoreModule.Core.Services;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Diagnostics;
using VirtoCommerce.UCP.Core.Services;
using VirtoCommerce.UCP.Web.Diagnostics;

namespace VirtoCommerce.UCP.Web.Mcp;

public sealed class UcpMcpCallToolFilter
{
    private static readonly EventId McpOperationExceptionEvent = new(2002, "UcpMcpOperationException");

    private readonly UcpOperationTelemetry _operationTelemetry;
    private readonly ILogger<UcpMcpCallToolFilter> _logger;

    public UcpMcpCallToolFilter(
        UcpOperationTelemetry operationTelemetry,
        ILogger<UcpMcpCallToolFilter> logger)
    {
        _operationTelemetry = operationTelemetry;
        _logger = logger;
    }

    public async ValueTask<CallToolResult> InvokeAsync(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next,
        RequestContext<CallToolRequestParams> context,
        CancellationToken cancellationToken)
    {
        var toolName = context.Params?.Name;
        if (!ModuleConstants.McpTools.IsUcpTool(toolName))
        {
            return await next(context, cancellationToken);
        }

        return await InvokeUcpToolAsync(next, context, toolName, cancellationToken);
    }

    private async ValueTask<CallToolResult> InvokeUcpToolAsync(
        McpRequestHandler<CallToolRequestParams, CallToolResult> next,
        RequestContext<CallToolRequestParams> context,
        string toolName,
        CancellationToken cancellationToken)
    {
        var parentActivity = Activity.Current?.Source.Name == UcpDiagnostics.McpActivitySourceName
            ? Activity.Current
            : null;
        var telemetryStarted = _operationTelemetry.TryBegin(toolName, "mcp", parentActivity?.Context);
        if (telemetryStarted)
        {
            _operationTelemetry.CaptureMcpArguments(context.Params?.Arguments);
        }
        var traceId = telemetryStarted
            ? _operationTelemetry.TraceId
            : parentActivity?.TraceId.ToString();
        try
        {
            ValidateRequiredArguments(context);
            await ValidateStore(context);
            var result = await next(context, cancellationToken);
            MarkRejectedResult(result, telemetryStarted);

            return AddTraceMetadata(result, traceId);
        }
        catch (XApiResponseException exception)
        {
            return HandleXApiException(exception, telemetryStarted, traceId);
        }
        catch (UcpException exception)
        {
            return HandleUcpException(exception, toolName, telemetryStarted, traceId);
        }
        catch (OperationCanceledException)
        {
            if (telemetryStarted)
            {
                _operationTelemetry.MarkCanceled();
            }
            throw;
        }
        catch (Exception exception)
        {
            return HandleUnexpectedException(exception, toolName, telemetryStarted, traceId);
        }
        finally
        {
            if (telemetryStarted)
            {
                _operationTelemetry.Complete();
            }
        }
    }

    private static void ValidateRequiredArguments(RequestContext<CallToolRequestParams> context)
    {
        if (context.MatchedPrimitive is not McpServerTool tool
            || !tool.ProtocolTool.InputSchema.TryGetProperty("required", out var required))
        {
            return;
        }

        foreach (var property in required.EnumerateArray())
        {
            var name = property.GetString();
            if (context.Params?.Arguments == null
                || !context.Params.Arguments.TryGetValue(name, out var value)
                || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                throw new UcpException(ModuleConstants.ErrorCodes.InvalidRequest, $"{name} is required.");
            }
        }
    }

    private static async Task ValidateStore(RequestContext<CallToolRequestParams> context)
    {
        if (context.Params?.Arguments?.TryGetValue("store_id", out var storeId) != true
            || storeId.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(storeId.GetString()))
        {
            return;
        }

        var storeService = context.Services.GetRequiredService<IStoreService>();
        var store = await UcpDiagnostics.ExecuteDependency("stores", "GetStore", () => storeService.GetNoCloneAsync(storeId.GetString()));
        if (store == null)
        {
            throw new UcpException(ModuleConstants.ErrorCodes.InvalidRequest, "store_id does not identify an existing store.");
        }
    }

    private void MarkRejectedResult(CallToolResult result, bool telemetryStarted)
    {
        if (telemetryStarted && result.IsError == true)
        {
            _operationTelemetry.MarkRejected("mcp_tool_error");
        }
    }

    private CallToolResult HandleXApiException(
        XApiResponseException exception,
        bool telemetryStarted,
        string traceId)
    {
        if (telemetryStarted)
        {
            _operationTelemetry.MarkError(nameof(XApiResponseException), "xapi_graphql_error");
        }

        return AddTraceMetadata(UcpMcpErrorResultFactory.FromXApi(exception), traceId);
    }

    private CallToolResult HandleUcpException(
        UcpException exception,
        string toolName,
        bool telemetryStarted,
        string traceId)
    {
        if (exception.StatusCode >= StatusCodes.Status500InternalServerError)
        {
            MarkAndLogServerError(exception, toolName, telemetryStarted, traceId);
        }
        else if (telemetryStarted)
        {
            _operationTelemetry.MarkRejected(exception.Code);
        }

        return AddTraceMetadata(UcpMcpErrorResultFactory.FromUcp(exception), traceId);
    }

    private void MarkAndLogServerError(
        UcpException exception,
        string toolName,
        bool telemetryStarted,
        string traceId)
    {
        if (telemetryStarted)
        {
            _operationTelemetry.MarkError(exception, exception.Code);
        }

        _logger.LogError(
            McpOperationExceptionEvent,
            exception,
            "event:{EventName} tool:{UcpTool} error_code:{UcpErrorCode} trace_id:{TraceId}",
            "ucp.mcp.operation.exception",
            toolName,
            exception.Code,
            traceId);
    }

    private CallToolResult HandleUnexpectedException(
        Exception exception,
        string toolName,
        bool telemetryStarted,
        string traceId)
    {
        if (telemetryStarted)
        {
            _operationTelemetry.MarkError(exception);
        }

        _logger.LogError(
            McpOperationExceptionEvent,
            exception,
            "event:{EventName} tool:{UcpTool} trace_id:{TraceId}",
            "ucp.mcp.operation.exception",
            toolName,
            traceId);
        return AddTraceMetadata(UcpMcpErrorResultFactory.FromUnexpected(), traceId);
    }

    private static CallToolResult AddTraceMetadata(CallToolResult result, string traceId)
    {
        if (!string.IsNullOrEmpty(traceId))
        {
            result.Meta ??= new JsonObject();
            result.Meta["trace_id"] = traceId;
            result.Content ??= [];
            result.Content.Add(new TextContentBlock { Text = $"Trace ID: {traceId}" });
        }

        return result;
    }
}
