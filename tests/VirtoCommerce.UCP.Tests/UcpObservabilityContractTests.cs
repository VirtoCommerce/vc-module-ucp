using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Diagnostics;
using VirtoCommerce.UCP.Core.Models;
using VirtoCommerce.UCP.Core.Options;
using VirtoCommerce.UCP.Core.Services;
using VirtoCommerce.UCP.Web.Diagnostics;
using VirtoCommerce.UCP.Web.Filters;
using VirtoCommerce.UCP.Web.Mcp;
using Xunit;

namespace VirtoCommerce.UCP.Tests;

[Trait("Category", "Unit")]
public class UcpObservabilityContractTests
{
    private const string RawCanary = "RAW-CANARY-VCST-5544-XAPI-DETAIL";

    private const string RawXApiJson = """
        {"data":{"product":{"id":"partial-product"}},"errors":[{"message":"internal catalog detail","locations":[{"line":2,"column":7}],"path":["product","price"],"extensions":{"code":"CATALOG_FAILURE","diagnostic":"RAW-CANARY-VCST-5544-XAPI-DETAIL"}},{"message":"second error","locations":[{"line":4,"column":3}],"path":["product","availability"],"extensions":{"code":"INVENTORY_FAILURE","retryable":true}}],"extensions":{"requestId":"xapi-request-42"}}
        """;

    [Fact]
    public void UcpExceptionFilter_ReturnsXApiGraphQlEnvelopeLosslessly()
    {
        var logger = new CapturingTelemetryLogger();
        var filter = new UcpExceptionFilter(new UcpOperationTelemetry(logger), NullLogger<UcpExceptionFilter>.Instance);
        var exception = CreateXApiException();
        var context = new ExceptionContext(
            new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor
            {
                EndpointMetadata = [new UcpOperationAttribute(ModuleConstants.Operations.SearchProducts)],
            }),
            new List<IFilterMetadata>())
        {
            Exception = exception,
        };

        filter.OnException(context);

        Assert.True(context.ExceptionHandled);
        var result = Assert.IsType<ContentResult>(context.Result);
        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        Assert.Equal("application/json", result.ContentType);
        Assert.Equal(RawXApiJson, result.Content);
    }

    [Fact]
    public void UcpExceptionFilter_IgnoresNonUcpActions()
    {
        var logger = new CapturingTelemetryLogger();
        var filter = new UcpExceptionFilter(new UcpOperationTelemetry(logger), NullLogger<UcpExceptionFilter>.Instance);
        var exception = CreateXApiException();
        var context = new ExceptionContext(
            new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()),
            new List<IFilterMetadata>())
        {
            Exception = exception,
        };

        filter.OnException(context);

        Assert.False(context.ExceptionHandled);
        Assert.Null(context.Result);
        Assert.Same(exception, context.Exception);
        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void UcpExceptionFilter_LogsHandledServerExceptionWithOriginalException()
    {
        using var parent = new Activity("POST ucp/v1/catalog/search").Start();
        var telemetryLogger = new CapturingTelemetryLogger();
        var exceptionLogger = new CapturingExceptionLogger<UcpExceptionFilter>();
        var telemetry = new UcpOperationTelemetry(telemetryLogger);
        telemetry.Begin(ModuleConstants.Operations.SearchProducts, "rest", parent.Context);
        var filter = new UcpExceptionFilter(telemetry, exceptionLogger);
        var exception = new UcpException("internal_error", RawCanary, StatusCodes.Status500InternalServerError);
        var context = new ExceptionContext(
            new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor
            {
                EndpointMetadata = [new UcpOperationAttribute(ModuleConstants.Operations.SearchProducts)],
            }),
            new List<IFilterMetadata>())
        {
            Exception = exception,
        };

        filter.OnException(context);
        telemetry.Complete();

        Assert.True(context.ExceptionHandled);
        var entry = Assert.Single(exceptionLogger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(exception, entry.Exception);
        Assert.Equal("internal_error", entry.Properties["UcpErrorCode"]);
        Assert.False(string.IsNullOrEmpty(entry.Properties["TraceId"]?.ToString()));
    }

    [Fact]
    public async Task UcpOperationResourceFilter_ExposesTheActiveTraceIdInRestResponseHeaders()
    {
        const string parentSourceName = "VCST5544.Tests.AspNetCore";
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = CreateActivityListener(parentSourceName, stopped);
        using var parentSource = new ActivitySource(parentSourceName);
        using var parent = parentSource.StartActivity("POST ucp/v1/catalog/search", ActivityKind.Server);
        Assert.NotNull(parent);

        var httpContext = new DefaultHttpContext();
        httpContext.Features.Set<IHttpActivityFeature>(new TestHttpActivityFeature(parent));
        var actionDescriptor = new ControllerActionDescriptor
        {
            MethodInfo = typeof(UcpObservabilityContractTests)
                .GetMethod(nameof(AnnotatedRestAction), BindingFlags.NonPublic | BindingFlags.Static),
            EndpointMetadata = [new UcpOperationAttribute(ModuleConstants.Operations.SearchProducts)],
        };
        var actionContext = new ActionContext(httpContext, new RouteData(), actionDescriptor);
        var filters = new List<IFilterMetadata>();
        var context = new ResourceExecutingContext(actionContext, filters, new List<IValueProviderFactory>());
        var filter = new UcpOperationResourceFilter(new UcpOperationTelemetry(new CapturingTelemetryLogger()));

        await filter.OnResourceExecutionAsync(
            context,
            () => Task.FromResult(new ResourceExecutedContext(actionContext, filters)));

        Assert.Equal(parent.TraceId.ToString(), httpContext.Response.Headers[ModuleConstants.Headers.TraceId]);
    }

    [Fact]
    public async Task UcpMcpCallToolFilter_ReturnsExactXApiEnvelopeInStructuredContentAndModelVisibleText()
    {
        const string parentSourceName = UcpDiagnostics.McpActivitySourceName;
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = CreateActivityListener(parentSourceName, stopped);
        using var parentSource = new ActivitySource(parentSourceName);
        using var parent = parentSource.StartActivity(
            $"tools/call {ModuleConstants.McpTools.SearchProducts}",
            ActivityKind.Server);
        Assert.NotNull(parent);

        var logger = new CapturingTelemetryLogger();
        var filter = new UcpMcpCallToolFilter(
            new UcpOperationTelemetry(logger),
            NullLogger<UcpMcpCallToolFilter>.Instance);
        var context = CreateCallToolContext(ModuleConstants.McpTools.SearchProducts);
        var exception = CreateXApiException();
        McpRequestHandler<CallToolRequestParams, CallToolResult> next =
            (_, _) => ValueTask.FromException<CallToolResult>(exception);

        var result = await filter.InvokeAsync(next, context, TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.True(result.StructuredContent.HasValue);
        Assert.Equal(RawXApiJson, result.StructuredContent.Value.GetRawText());

        Assert.Collection(
            result.Content,
            content =>
            {
                var text = Assert.IsType<TextContentBlock>(content).Text;
                Assert.Equal(RawXApiJson, text);
            },
            content => Assert.Equal($"Trace ID: {parent.TraceId}", Assert.IsType<TextContentBlock>(content).Text));
        Assert.Equal("catalog", result.Meta?["source"]?.GetValue<string>());
        Assert.Equal(2, result.Meta?["error_count"]?.GetValue<int>());
        Assert.Equal(parent.TraceId.ToString(), result.Meta?["trace_id"]?.GetValue<string>());
    }

    [Fact]
    public async Task UcpMcpCallToolFilter_ReturnsUcpDetailAndTraceMetadata()
    {
        const string parentSourceName = UcpDiagnostics.McpActivitySourceName;
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = CreateActivityListener(parentSourceName, stopped);
        using var parentSource = new ActivitySource(parentSourceName);
        using var parent = parentSource.StartActivity(
            $"tools/call {ModuleConstants.McpTools.GetProduct}",
            ActivityKind.Server);
        Assert.NotNull(parent);

        var logger = new CapturingTelemetryLogger();
        var exceptionLogger = new CapturingExceptionLogger<UcpMcpCallToolFilter>();
        var filter = new UcpMcpCallToolFilter(
            new UcpOperationTelemetry(logger),
            exceptionLogger);
        var context = CreateCallToolContext(ModuleConstants.McpTools.GetProduct);
        var exception = new UcpException("invalid_request", RawCanary, StatusCodes.Status400BadRequest);
        McpRequestHandler<CallToolRequestParams, CallToolResult> next =
            (_, _) => ValueTask.FromException<CallToolResult>(exception);

        var result = await filter.InvokeAsync(next, context, TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.True(result.StructuredContent.HasValue);
        Assert.Equal(RawCanary, result.StructuredContent.Value.GetProperty("message").GetString());
        Assert.Collection(
            result.Content,
            content =>
            {
                var text = Assert.IsType<TextContentBlock>(content).Text;
                Assert.Equal(result.StructuredContent.Value.GetRawText(), text);
                Assert.Contains(RawCanary, text, StringComparison.Ordinal);
            },
            content => Assert.Equal($"Trace ID: {parent.TraceId}", Assert.IsType<TextContentBlock>(content).Text));
        Assert.Equal("ucp", result.Meta?["source"]?.GetValue<string>());
        Assert.Equal("invalid_request", result.Meta?["error_code"]?.GetValue<string>());
        Assert.Equal(StatusCodes.Status400BadRequest, result.Meta?["status_code"]?.GetValue<int>());
        Assert.Equal(parent.TraceId.ToString(), result.Meta?["trace_id"]?.GetValue<string>());
        Assert.Empty(exceptionLogger.Entries);
    }

    [Fact]
    public async Task UcpMcpCallToolFilter_LogsHandledServerExceptionWithOriginalException()
    {
        const string parentSourceName = UcpDiagnostics.McpActivitySourceName;
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = CreateActivityListener(parentSourceName, stopped);
        using var parentSource = new ActivitySource(parentSourceName);
        using var parent = parentSource.StartActivity(
            $"tools/call {ModuleConstants.McpTools.SearchProducts}",
            ActivityKind.Server);
        Assert.NotNull(parent);

        var telemetryLogger = new CapturingTelemetryLogger();
        var exceptionLogger = new CapturingExceptionLogger<UcpMcpCallToolFilter>();
        var filter = new UcpMcpCallToolFilter(
            new UcpOperationTelemetry(telemetryLogger),
            exceptionLogger);
        var context = CreateCallToolContext(ModuleConstants.McpTools.SearchProducts);
        var exception = new UcpException("internal_error", RawCanary, StatusCodes.Status500InternalServerError);
        McpRequestHandler<CallToolRequestParams, CallToolResult> next =
            (_, _) => ValueTask.FromException<CallToolResult>(exception);

        var result = await filter.InvokeAsync(next, context, TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        var entry = Assert.Single(exceptionLogger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(exception, entry.Exception);
        Assert.Equal(ModuleConstants.McpTools.SearchProducts, entry.Properties["UcpTool"]);
        Assert.Equal("internal_error", entry.Properties["UcpErrorCode"]);
        Assert.Equal(parent.TraceId.ToString(), entry.Properties["TraceId"]);
    }

    [Fact]
    public async Task UcpMcpCallToolFilter_CreatesInternalChildOfMcpAndLogsBeforeUcpSpanStops()
    {
        const string parentSourceName = UcpDiagnostics.McpActivitySourceName;
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = CreateActivityListener(parentSourceName, stopped);
        using var parentSource = new ActivitySource(parentSourceName);
        using var parent = parentSource.StartActivity(
            $"tools/call {ModuleConstants.McpTools.SearchProducts}",
            ActivityKind.Server);
        Assert.NotNull(parent);

        var logger = new CapturingTelemetryLogger();
        var filter = new UcpMcpCallToolFilter(
            new UcpOperationTelemetry(logger),
            NullLogger<UcpMcpCallToolFilter>.Instance);
        var context = CreateCallToolContext(ModuleConstants.McpTools.SearchProducts);
        ActivitySpanId spanSeenByHandler = default;
        McpRequestHandler<CallToolRequestParams, CallToolResult> next = (_, _) =>
        {
            spanSeenByHandler = Activity.Current?.SpanId ?? default;
            return ValueTask.FromResult(new CallToolResult { Content = [] });
        };

        var result = await filter.InvokeAsync(next, context, TestContext.Current.CancellationToken);

        Assert.Equal($"Trace ID: {parent.TraceId}", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.Equal(parent.TraceId.ToString(), result.Meta?["trace_id"]?.GetValue<string>());

        var ucpActivity = Assert.Single(stopped, activity =>
            activity.Source.Name == UcpDiagnostics.ActivitySourceName &&
            activity.TraceId == parent.TraceId);
        Assert.Equal(ActivityKind.Internal, ucpActivity.Kind);
        Assert.Equal(parent.SpanId, ucpActivity.ParentSpanId);
        Assert.Equal($"UCP {ModuleConstants.McpTools.SearchProducts}", ucpActivity.DisplayName);
        Assert.Equal(ucpActivity.SpanId, spanSeenByHandler);
        Assert.Equal(ModuleConstants.McpTools.SearchProducts, ucpActivity.GetTagItem("vc.ucp.operation"));
        Assert.Equal("mcp", ucpActivity.GetTagItem("vc.ucp.transport"));

        var terminalLog = Assert.Single(logger.Entries);
        Assert.Equal(2000, terminalLog.EventId.Id);
        Assert.Equal("UcpOperationCompleted", terminalLog.EventId.Name);
        Assert.Equal(UcpDiagnostics.ActivitySourceName, terminalLog.ActivitySourceName);
        Assert.True(terminalLog.SpanId.HasValue);
        Assert.Equal(ucpActivity.SpanId, terminalLog.SpanId.Value);
        Assert.Equal(ucpActivity.TraceId.ToString(), terminalLog.StructuredTraceId);
        Assert.Equal(ucpActivity.SpanId.ToString(), terminalLog.StructuredSpanId);
    }

    [Fact]
    public void UcpOperationTelemetry_RecordsMutationFactsWithoutInferredRetryStateOrDuplicateEvent()
    {
        const string parentSourceName = "VCST5544.Tests.TerminalFacts";
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = CreateActivityListener(parentSourceName, stopped);
        using var parentSource = new ActivitySource(parentSourceName);
        using var parent = parentSource.StartActivity("tools/call update_cart", ActivityKind.Server);
        Assert.NotNull(parent);

        var logger = new CapturingTelemetryLogger();
        var telemetry = new UcpOperationTelemetry(logger);
        telemetry.Begin(ModuleConstants.Operations.UpdateCart, "mcp");
        telemetry.BeginXApiCall(isMutation: true);
        telemetry.CompleteXApiCall(isMutation: true, errorCount: 0, completed: true);
        telemetry.BeginXApiCall(isMutation: true);
        telemetry.CompleteXApiCall(isMutation: true, errorCount: 1, completed: true);
        telemetry.Complete();

        var activity = Assert.Single(stopped, item => item.DisplayName == $"UCP {ModuleConstants.Operations.UpdateCart}");
        Assert.Equal(2, activity.GetTagItem("vc.xapi.call.count"));
        Assert.Equal(1, activity.GetTagItem("vc.xapi.failed_call.count"));
        Assert.Equal(1, activity.GetTagItem("vc.xapi.graphql.error.count"));
        Assert.Equal(2, activity.GetTagItem("vc.xapi.mutation.call.count"));
        Assert.Equal(1, activity.GetTagItem("vc.xapi.mutation.failed_call.count"));
        Assert.Null(activity.GetTagItem("vc.side_effect.state"));
        Assert.Null(activity.GetTagItem("vc.retry.safe"));
        Assert.Empty(activity.Events);

        var terminalLog = Assert.Single(logger.Entries);
        Assert.Equal(2, terminalLog.Properties["XApiCallCount"]);
        Assert.Equal(1, terminalLog.Properties["XApiFailedCallCount"]);
        Assert.Equal(1, terminalLog.Properties["XApiGraphQlErrorCount"]);
        Assert.Equal(2, terminalLog.Properties["XApiMutationCallCount"]);
        Assert.Equal(1, terminalLog.Properties["XApiMutationFailedCallCount"]);
        Assert.DoesNotContain("SideEffectState", terminalLog.Properties.Keys);
        Assert.DoesNotContain("RetrySafe", terminalLog.Properties.Keys);
    }

    [Fact]
    public void UcpOperationTelemetry_SeparatesCanceledXApiCallsFromFailures()
    {
        var logger = new CapturingTelemetryLogger();
        var telemetry = new UcpOperationTelemetry(logger);
        telemetry.Begin(ModuleConstants.Operations.SearchProducts, "rest");
        telemetry.BeginXApiCall(isMutation: false);
        telemetry.CompleteXApiCall(isMutation: false, errorCount: 0, completed: false, canceled: true);
        telemetry.MarkCanceled();
        telemetry.Complete();

        var terminalLog = Assert.Single(logger.Entries);
        Assert.Equal("canceled", terminalLog.Properties["UcpOutcome"]);
        Assert.Equal(0, terminalLog.Properties["XApiFailedCallCount"]);
        Assert.Equal(1, terminalLog.Properties["XApiCanceledCallCount"]);
    }

    [Fact]
    public void UcpOperationTelemetry_UsesDegradedOutcomeForToleratedGraphQlErrors()
    {
        var logger = new CapturingTelemetryLogger();
        var telemetry = new UcpOperationTelemetry(logger);
        telemetry.Begin(ModuleConstants.Operations.SearchProducts, "rest");
        telemetry.BeginXApiCall(isMutation: false);
        telemetry.CompleteXApiCall(isMutation: false, errorCount: 1, completed: true);

        telemetry.MarkDegraded(nameof(XApiResponseException), "xapi_recoverable_graphql_error");
        telemetry.Complete();

        var terminalLog = Assert.Single(logger.Entries);
        Assert.Equal("degraded", terminalLog.Properties["UcpOutcome"]);
        Assert.Equal(nameof(XApiResponseException), terminalLog.Properties["ErrorType"]);
        Assert.Equal("xapi_recoverable_graphql_error", terminalLog.Properties["ErrorCode"]);
    }

    [Fact]
    public void UcpOperationTelemetry_ReentryIsNonThrowingAndKeepsTheActiveOperation()
    {
        var logger = new CapturingTelemetryLogger();
        var telemetry = new UcpOperationTelemetry(logger);

        Assert.True(telemetry.TryBegin(ModuleConstants.Operations.SearchProducts, "mcp"));
        Assert.False(telemetry.TryBegin(ModuleConstants.Operations.GetProduct, "mcp"));
        telemetry.Complete();

        var terminalLog = Assert.Single(logger.Entries, entry => entry.EventId.Id == 2000);
        Assert.Equal(ModuleConstants.Operations.SearchProducts, terminalLog.Properties["UcpOperation"]);
    }

    [Fact]
    public void UcpOperationTelemetry_RecordsMetricsThatSurviveTraceSampling()
    {
        var measurements = new ConcurrentQueue<(string Name, long Value)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == UcpDiagnostics.MeterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
            measurements.Enqueue((instrument.Name, value)));
        listener.Start();

        var telemetry = new UcpOperationTelemetry(new CapturingTelemetryLogger());
        telemetry.Begin(ModuleConstants.Operations.UpdateCart, "mcp");
        telemetry.BeginXApiCall(isMutation: true);
        telemetry.CompleteXApiCall(isMutation: true, errorCount: 1, completed: true);
        telemetry.Complete();

        Assert.Contains(measurements, measurement => measurement == ("vc.ucp.operation.count", 1));
        Assert.Contains(measurements, measurement => measurement == ("vc.xapi.call.count", 1));
        Assert.Contains(measurements, measurement => measurement == ("vc.xapi.failed_call.count", 1));
        Assert.Contains(measurements, measurement => measurement == ("vc.xapi.graphql.error.count", 1));
        Assert.Contains(measurements, measurement => measurement == ("vc.xapi.mutation.call.count", 1));
        Assert.Contains(measurements, measurement => measurement == ("vc.xapi.mutation.failed_call.count", 1));
    }

    [Theory]
    [InlineData("rejected")]
    [InlineData("canceled")]
    public void UcpOperationTelemetry_DoesNotMarkExpectedNonSuccessOutcomesAsSpanErrors(string outcome)
    {
        const string parentSourceName = "VCST5544.Tests.ExpectedOutcome";
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = CreateActivityListener(parentSourceName, stopped);
        using var parentSource = new ActivitySource(parentSourceName);
        using var parent = parentSource.StartActivity("tools/call get_product", ActivityKind.Server);
        Assert.NotNull(parent);

        var logger = new CapturingTelemetryLogger();
        var telemetry = new UcpOperationTelemetry(logger);
        telemetry.Begin(ModuleConstants.Operations.GetProduct, "mcp");
        if (outcome == "rejected")
        {
            telemetry.MarkRejected(ModuleConstants.ErrorCodes.InvalidRequest);
        }
        else
        {
            telemetry.MarkCanceled();
        }
        telemetry.Complete();

        var activity = Assert.Single(stopped, item =>
            item.DisplayName == $"UCP {ModuleConstants.Operations.GetProduct}" &&
            item.TraceId == parent.TraceId);
        Assert.Equal(outcome, activity.GetTagItem("vc.ucp.outcome"));
        Assert.Equal(ActivityStatusCode.Unset, activity.Status);

        var terminalLog = Assert.Single(logger.Entries);
        Assert.Equal(outcome, terminalLog.Properties["UcpOutcome"]);
    }

    [Fact]
    public void UcpOperationTelemetry_CorrelatesMcpInputWithEffectiveXApiContextWithoutLeakingSensitiveValues()
    {
        const string parentSourceName = "VCST5544.Tests.RequestContext";
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = CreateActivityListener(parentSourceName, stopped);
        using var parentSource = new ActivitySource(parentSourceName);
        using var parent = parentSource.StartActivity("tools/call search_products", ActivityKind.Server);
        Assert.NotNull(parent);

        var logger = new CapturingTelemetryLogger();
        var telemetry = new UcpOperationTelemetry(logger);
        telemetry.Begin(ModuleConstants.Operations.SearchProducts, "mcp");
        telemetry.CaptureMcpArguments(new Dictionary<string, JsonElement>
        {
            ["query"] = JsonSerializer.SerializeToElement("микро\rволновка"),
            ["limit"] = JsonSerializer.SerializeToElement(10),
            ["buyer_email"] = JsonSerializer.SerializeToElement("buyer-secret@example.com"),
        });
        telemetry.CaptureXApiVariables(new Dictionary<string, object>
        {
            ["storeId"] = "B2B-store",
            ["userId"] = "buyer-secret@example.com",
            ["currencyCode"] = "USD",
            ["cultureName"] = "ru-RU",
            ["query"] = "микро\rволновка",
            ["filter"] = null,
            ["first"] = 10,
        });
        telemetry.MarkError(nameof(XApiResponseException), "xapi_graphql_error");
        telemetry.Complete();

        var activity = Assert.Single(stopped, item =>
            item.DisplayName == $"UCP {ModuleConstants.Operations.SearchProducts}" &&
            item.TraceId == parent.TraceId);
        Assert.Equal("B2B-store", activity.GetTagItem("vc.store.id"));
        Assert.Equal("defaulted", activity.GetTagItem("vc.store.source"));
        Assert.Equal("USD", activity.GetTagItem("vc.currency.code"));
        Assert.Equal("ru-RU", activity.GetTagItem("vc.culture.name"));
        Assert.Equal(14, activity.GetTagItem("vc.catalog.search.query.length"));
        Assert.NotNull(activity.GetTagItem("vc.catalog.search.query.hash"));
        Assert.Equal("микро волновка", activity.GetTagItem("vc.catalog.search.query"));
        using var activityInput = JsonDocument.Parse(Assert.IsType<string>(activity.GetTagItem("vc.ucp.input_json")));
        Assert.Equal("микро волновка", activityInput.RootElement.GetProperty("query").GetString());
        Assert.DoesNotContain("buyer-secret@example.com", activityInput.RootElement.GetRawText(), StringComparison.Ordinal);

        var terminalLog = Assert.Single(logger.Entries);
        using var input = JsonDocument.Parse(Assert.IsType<string>(terminalLog.Properties["InputJson"]));
        Assert.Equal("микро волновка", input.RootElement.GetProperty("query").GetString());
        Assert.Equal("B2B-store", input.RootElement.GetProperty("store").GetProperty("effective").GetString());
        Assert.Equal(14, terminalLog.Properties["SearchQueryLength"]);
        Assert.Equal("B2B-store", terminalLog.Properties["EffectiveStoreId"]);
        Assert.Equal("defaulted", terminalLog.Properties["StoreSource"]);
        Assert.Equal("cultureName,currencyCode,filter,first,query,storeId,userId", terminalLog.Properties["XApiVariableNames"]);
        Assert.DoesNotContain(terminalLog.Properties.Values, value => value?.ToString() == "buyer-secret@example.com");
    }

    [Fact]
    public void UcpOperationTelemetry_WritesBoundedAllowlistedInputForSuccessfulOperation()
    {
        var logger = new CapturingTelemetryLogger();
        var options = Options.Create(new UcpOptions
        {
            Observability = new UcpObservabilityOptions { InputCaptureMode = UcpInputCaptureMode.Always },
        });
        var telemetry = new UcpOperationTelemetry(logger, options);
        telemetry.Begin(ModuleConstants.Operations.SearchProducts, "rest");
        telemetry.CaptureRestArguments(new Dictionary<string, object>
        {
            ["request"] = new UcpCatalogSearchRequest
            {
                Query = "микроволновка",
                StoreId = "B2B-store",
                Currency = "USD",
                Language = "ru-RU",
                Limit = 10,
            },
        });
        telemetry.Complete();

        var terminalLog = Assert.Single(logger.Entries);
        using var input = JsonDocument.Parse(Assert.IsType<string>(terminalLog.Properties["InputJson"]));
        Assert.Equal("микроволновка", input.RootElement.GetProperty("query").GetString());
        Assert.Equal("B2B-store", input.RootElement.GetProperty("store").GetProperty("requested").GetString());
        Assert.Equal(10, input.RootElement.GetProperty("limit").GetInt64());
        Assert.Equal("B2B-store", terminalLog.Properties["RequestedStoreId"]);
    }

    [Fact]
    public async Task UcpMcpCallToolFilter_UnknownToolBypassesTranslationTelemetryAndLogging()
    {
        const string parentSourceName = "VCST5544.Tests.ForeignMcp";
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = CreateActivityListener(parentSourceName, stopped);
        using var parentSource = new ActivitySource(parentSourceName);
        using var parent = parentSource.StartActivity("tools/call foreign_tool", ActivityKind.Server);
        Assert.NotNull(parent);

        var logger = new CapturingTelemetryLogger();
        var filter = new UcpMcpCallToolFilter(
            new UcpOperationTelemetry(logger),
            NullLogger<UcpMcpCallToolFilter>.Instance);
        var context = CreateCallToolContext("foreign_tool");
        var expectedException = CreateXApiException();
        var invocationCount = 0;
        McpRequestHandler<CallToolRequestParams, CallToolResult> next = (_, _) =>
        {
            Interlocked.Increment(ref invocationCount);
            return ValueTask.FromException<CallToolResult>(expectedException);
        };

        var actualException = await Assert.ThrowsAsync<XApiResponseException>(async () =>
            await filter.InvokeAsync(next, context, TestContext.Current.CancellationToken));

        Assert.Same(expectedException, actualException);
        Assert.Equal(1, invocationCount);
        Assert.Empty(logger.Entries);
        Assert.DoesNotContain(stopped, activity =>
            activity.Source.Name == UcpDiagnostics.ActivitySourceName &&
            activity.TraceId == parent.TraceId);
    }

    [Fact]
    public async Task UcpMcpActivityStatusFilter_ReplacesOnlyUcpToolErrorPayload()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = CreateActivityListener(UcpDiagnostics.McpActivitySourceName, stopped);
        using var source = new ActivitySource(UcpDiagnostics.McpActivitySourceName);
        using var activity = source.StartActivity(
            $"tools/call {ModuleConstants.McpTools.SearchProducts}",
            ActivityKind.Server);
        Assert.NotNull(activity);

        var context = CreateMessageContext(ModuleConstants.McpTools.SearchProducts);
        McpMessageHandler sdkHandler = (_, _) =>
        {
            Activity.Current.SetStatus(ActivityStatusCode.Error, RawXApiJson);
            return Task.CompletedTask;
        };

        await UcpMcpActivityStatusFilter.Create()(sdkHandler)(
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal(UcpMcpActivityStatusFilter.ErrorStatusDescription, activity.StatusDescription);
        Assert.DoesNotContain(RawCanary, activity.StatusDescription, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UcpMcpActivityStatusFilter_DoesNotChangeForeignToolErrorPayload()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = CreateActivityListener(UcpDiagnostics.McpActivitySourceName, stopped);
        using var source = new ActivitySource(UcpDiagnostics.McpActivitySourceName);
        using var activity = source.StartActivity("tools/call foreign_tool", ActivityKind.Server);
        Assert.NotNull(activity);

        var context = CreateMessageContext("foreign_tool");
        McpMessageHandler sdkHandler = (_, _) =>
        {
            Activity.Current.SetStatus(ActivityStatusCode.Error, RawCanary);
            return Task.CompletedTask;
        };

        await UcpMcpActivityStatusFilter.Create()(sdkHandler)(
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal(RawCanary, activity.StatusDescription);
    }

    [Fact]
    public async Task UcpMcpActivityStatusFilter_DoesNotChangeSuccessfulUcpToolSpan()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = CreateActivityListener(UcpDiagnostics.McpActivitySourceName, stopped);
        using var source = new ActivitySource(UcpDiagnostics.McpActivitySourceName);
        using var activity = source.StartActivity(
            $"tools/call {ModuleConstants.McpTools.SearchProducts}",
            ActivityKind.Server);
        Assert.NotNull(activity);

        var context = CreateMessageContext(ModuleConstants.McpTools.SearchProducts);
        McpMessageHandler sdkHandler = (_, _) => Task.CompletedTask;

        await UcpMcpActivityStatusFilter.Create()(sdkHandler)(
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal(ActivityStatusCode.Unset, activity.Status);
        Assert.Null(activity.StatusDescription);
    }

    [Fact]
    public async Task ExecuteDependency_CreatesCorrelatedPlatformDependencySpan()
    {
        const string parentSourceName = "VCST5544.Tests.DirectDependency";
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = CreateActivityListener(parentSourceName, stopped);
        using var parentSource = new ActivitySource(parentSourceName);
        using var parent = parentSource.StartActivity("UCP track_order", ActivityKind.Internal);
        Assert.NotNull(parent);

        var value = await UcpDiagnostics.ExecuteDependency(
            "orders",
            "SearchOrders",
            () => Task.FromResult(42));

        Assert.Equal(42, value);
        var dependency = Assert.Single(stopped, activity => activity.DisplayName == "VC orders SearchOrders");
        Assert.Equal(ActivityKind.Internal, dependency.Kind);
        Assert.Equal(parent.TraceId, dependency.TraceId);
        Assert.Equal(parent.SpanId, dependency.ParentSpanId);
        Assert.Equal("platform", dependency.GetTagItem("vc.dependency.system"));
        Assert.Equal("orders", dependency.GetTagItem("vc.dependency.component"));
        Assert.Equal("SearchOrders", dependency.GetTagItem("vc.dependency.operation"));
        Assert.Equal("success", dependency.GetTagItem("vc.dependency.outcome"));
        Assert.Equal(ActivityStatusCode.Unset, dependency.Status);
    }

    [Fact]
    public async Task ExecuteDependency_MarksFailureAndRethrowsOriginalException()
    {
        const string parentSourceName = "VCST5544.Tests.DirectDependencyFailure";
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = CreateActivityListener(parentSourceName, stopped);
        using var parentSource = new ActivitySource(parentSourceName);
        using var parent = parentSource.StartActivity("UCP list_countries", ActivityKind.Internal);
        Assert.NotNull(parent);
        var expected = new InvalidOperationException(RawCanary);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            UcpDiagnostics.ExecuteDependency<int>(
                "countries",
                "GetCountries",
                () => Task.FromException<int>(expected)));

        Assert.Same(expected, actual);
        var dependency = Assert.Single(stopped, activity => activity.DisplayName == "VC countries GetCountries");
        Assert.Equal(ActivityStatusCode.Error, dependency.Status);
        Assert.Equal("error", dependency.GetTagItem("vc.dependency.outcome"));
        Assert.Equal(nameof(InvalidOperationException), dependency.StatusDescription);
        Assert.Equal(typeof(InvalidOperationException).FullName, dependency.GetTagItem("error.type"));
    }

    [Fact]
    public void UcpApplicationInsightsActivityBridge_MapsXApiActivityWithoutBreakingW3CCorrelation()
    {
        const string parentSourceName = "VCST5544.Tests.ApplicationInsightsParent";
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = CreateActivityListener(parentSourceName, stopped);
        using var parentSource = new ActivitySource(parentSourceName);
        using var parent = parentSource.StartActivity("UCP search_products", ActivityKind.Internal);
        Assert.NotNull(parent);

        using (var xApiActivity = UcpDiagnostics.StartXApi("XCatalog", "UcpSearchProducts", "query", 1))
        {
            Assert.NotNull(xApiActivity);
            xApiActivity.SetTag("vc.xapi.error.codes", "NULL_REFERENCE");
            xApiActivity.SetTag("vc.catalog.search.query", "микроволновка");
            xApiActivity.SetStatus(ActivityStatusCode.Error, "GraphQL errors");
        }

        var activity = Assert.Single(stopped, item => item.DisplayName == "XAPI XCatalog UcpSearchProducts");
        var dependency = UcpApplicationInsightsActivityBridge.CreateDependencyTelemetry(activity);

        Assert.Equal("XAPI XCatalog UcpSearchProducts", dependency.Name);
        Assert.Equal("XAPI", dependency.Type);
        Assert.Equal("XCatalog", dependency.Target);
        Assert.False(dependency.Success);
        Assert.Equal("NULL_REFERENCE", dependency.ResultCode);
        Assert.Equal(activity.TraceId.ToString(), dependency.Context.Operation.Id);
        Assert.Equal(activity.ParentSpanId.ToString(), dependency.Context.Operation.ParentId);
        Assert.Equal(activity.SpanId.ToString(), dependency.Id);
        Assert.Equal("микроволновка", dependency.Properties["vc.catalog.search.query"]);
        Assert.Equal("UcpSearchProducts", dependency.Properties["graphql.operation.name"]);
    }

    [Fact]
    public void UcpApplicationInsightsActivityBridge_RequestsDataWithoutForcingRecordedFlag()
    {
        Assert.Equal(
            ActivitySamplingResult.AllData,
            UcpApplicationInsightsActivityBridge.GetSamplingResult(
                UcpDiagnostics.ActivitySourceName,
                $"UCP {ModuleConstants.Operations.SearchProducts}"));
        Assert.Equal(
            ActivitySamplingResult.AllData,
            UcpApplicationInsightsActivityBridge.GetSamplingResult(
                UcpDiagnostics.McpActivitySourceName,
                $"tools/call {ModuleConstants.McpTools.SearchProducts}"));
        Assert.Equal(
            ActivitySamplingResult.None,
            UcpApplicationInsightsActivityBridge.GetSamplingResult(
                UcpDiagnostics.McpActivitySourceName,
                "tools/list"));
    }

    [Fact]
    public async Task UcpApplicationInsightsActivityBridge_IsNoOpWithoutOfficialModule()
    {
        await using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        using var listener = new UcpApplicationInsightsActivityBridge(
            serviceProvider,
            NullLogger<UcpApplicationInsightsActivityBridge>.Instance);

        var exception = await Record.ExceptionAsync(async () =>
        {
            await listener.StartAsync(TestContext.Current.CancellationToken);
            await listener.StopAsync(TestContext.Current.CancellationToken);
        });

        Assert.Null(exception);
    }

    private static ActivityListener CreateActivityListener(
        string parentSourceName,
        ConcurrentQueue<Activity> stopped)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source =>
                source.Name == UcpDiagnostics.ActivitySourceName || source.Name == parentSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => stopped.Enqueue(activity),
        };
        ActivitySource.AddActivityListener(listener);

        return listener;
    }

    private static RequestContext<CallToolRequestParams> CreateCallToolContext(string toolName)
    {
        // The filter contract only consumes Params. Avoid coupling this unit test to an MCP transport/session.
        var context = (RequestContext<CallToolRequestParams>)RuntimeHelpers.GetUninitializedObject(
            typeof(RequestContext<CallToolRequestParams>));
        context.Params = new CallToolRequestParams { Name = toolName };

        return context;
    }

    [Theory]
    [InlineData(nameof(UcpMcpCommerceTools.CreateCart), "line_items")]
    [InlineData(nameof(UcpMcpCommerceTools.GetPaymentHandlers), "checkout_id")]
    [InlineData(nameof(UcpMcpCommerceTools.ListRegions), "country_id")]
    public async Task McpMissingRequiredArgument_ReturnsActionableErrorBeforeInvokingTool(string methodName, string requiredArgument)
    {
        using var services = new ServiceCollection()
            .AddSingleton<IUcpProfileService>(_ => null)
            .AddSingleton<IUcpCartService>(_ => null)
            .AddSingleton<IUcpCheckoutService>(_ => null)
            .AddSingleton<IUcpGeographyService>(_ => null)
            .BuildServiceProvider();
        var tool = McpServerTool.Create(typeof(UcpMcpCommerceTools).GetMethod(methodName), target: null,
            new McpServerToolCreateOptions { Services = services, SerializerOptions = UcpMcpSerialization.Options });
        var context = CreateCallToolContext(tool.ProtocolTool.Name);
        context.MatchedPrimitive = tool;
        var filter = new UcpMcpCallToolFilter(new UcpOperationTelemetry(new CapturingTelemetryLogger()), NullLogger<UcpMcpCallToolFilter>.Instance);
        var invoked = false;
        var result = await filter.InvokeAsync((_, _) =>
        {
            invoked = true;
            return ValueTask.FromResult(new CallToolResult());
        }, context, TestContext.Current.CancellationToken);

        Assert.False(invoked);
        Assert.True(result.IsError);
        Assert.Equal("invalid_request", result.StructuredContent.Value.GetProperty("code").GetString());
        Assert.Equal($"{requiredArgument} is required.", result.StructuredContent.Value.GetProperty("message").GetString());
    }

    private static MessageContext CreateMessageContext(string toolName)
    {
        // The message filter contract only consumes JsonRpcMessage. Avoid transport/session setup.
        var context = (MessageContext)RuntimeHelpers.GetUninitializedObject(typeof(MessageContext));
        context.JsonRpcMessage = new JsonRpcRequest
        {
            Method = RequestMethods.ToolsCall,
            Params = new JsonObject
            {
                ["name"] = toolName,
            },
        };

        return context;
    }

    private static XApiResponseException CreateXApiException()
    {
        return new XApiResponseException(
            "catalog",
            new XApiExecutionResult
            {
                Succeeded = false,
                Json = RawXApiJson,
            },
            errorCount: 2);
    }

    [Fact]
    public async Task UcpMcpCallToolFilter_ReturnsSafeTraceableResultForUnexpectedExceptions()
    {
        const string parentSourceName = UcpDiagnostics.McpActivitySourceName;
        var stopped = new ConcurrentQueue<Activity>();
        using var listener = CreateActivityListener(parentSourceName, stopped);
        using var parentSource = new ActivitySource(parentSourceName);
        using var parent = parentSource.StartActivity(
            $"tools/call {ModuleConstants.McpTools.SearchProducts}",
            ActivityKind.Server);
        Assert.NotNull(parent);

        var logger = new CapturingTelemetryLogger();
        var filter = new UcpMcpCallToolFilter(
            new UcpOperationTelemetry(logger),
            NullLogger<UcpMcpCallToolFilter>.Instance);
        var context = CreateCallToolContext(ModuleConstants.McpTools.SearchProducts);
        McpRequestHandler<CallToolRequestParams, CallToolResult> next =
            (_, _) => ValueTask.FromException<CallToolResult>(new InvalidOperationException(RawCanary));

        var result = await filter.InvokeAsync(next, context, TestContext.Current.CancellationToken);

        Assert.True(result.IsError);
        Assert.Equal("internal_error", result.StructuredContent.Value.GetProperty("code").GetString());
        Assert.DoesNotContain(RawCanary, result.StructuredContent.Value.GetRawText(), StringComparison.Ordinal);
        Assert.Collection(
            result.Content,
            content => Assert.Equal("UCP operation failed unexpectedly.", Assert.IsType<TextContentBlock>(content).Text),
            content => Assert.Equal($"Trace ID: {parent.TraceId}", Assert.IsType<TextContentBlock>(content).Text));
        Assert.Equal(parent.TraceId.ToString(), result.Meta?["trace_id"]?.GetValue<string>());
        Assert.Equal("internal_error", result.Meta?["error_code"]?.GetValue<string>());
        Assert.Single(logger.Entries);
    }

    [Fact]
    public void UcpMcpErrorResultFactory_SanitizesTheCodeInEveryOutputChannel()
    {
        var result = UcpMcpErrorResultFactory.FromUcp(
            new UcpException("unsafe code with spaces", "safe message", StatusCodes.Status400BadRequest));

        Assert.Equal("ucp_error", result.StructuredContent.Value.GetProperty("code").GetString());
        Assert.Equal("ucp_error", result.Meta?["error_code"]?.GetValue<string>());
        Assert.Contains("ucp_error", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe code with spaces", result.StructuredContent.Value.GetRawText(), StringComparison.Ordinal);
    }

    [UcpOperation(ModuleConstants.Operations.SearchProducts)]
    private static void AnnotatedRestAction()
    {
    }

    private sealed class CapturingTelemetryLogger : ILogger<UcpOperationTelemetry>
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
        {
            return NoopScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception exception,
            Func<TState, Exception, string> formatter)
        {
            var properties = state as IEnumerable<KeyValuePair<string, object>>;
            Entries.Enqueue(new LogEntry
            {
                EventId = eventId,
                ActivitySourceName = Activity.Current?.Source.Name,
                SpanId = Activity.Current?.SpanId,
                StructuredTraceId = properties?.FirstOrDefault(property => property.Key == "TraceId").Value?.ToString(),
                StructuredSpanId = properties?.FirstOrDefault(property => property.Key == "SpanId").Value?.ToString(),
                Properties = properties?.ToDictionary(property => property.Key, property => property.Value)
                    ?? new Dictionary<string, object>(),
            });
        }
    }

    private sealed class CapturingExceptionLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<ExceptionLogEntry> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
        {
            return NoopScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception exception,
            Func<TState, Exception, string> formatter)
        {
            var properties = (state as IEnumerable<KeyValuePair<string, object>>)?.ToDictionary(x => x.Key, x => x.Value)
                ?? new Dictionary<string, object>();
            Entries.Enqueue(new ExceptionLogEntry(logLevel, exception, properties));
        }
    }

    private sealed record ExceptionLogEntry(LogLevel Level, Exception Exception, IReadOnlyDictionary<string, object> Properties);

    private sealed class LogEntry
    {
        public EventId EventId { get; set; }
        public string ActivitySourceName { get; set; }
        public ActivitySpanId? SpanId { get; set; }
        public string StructuredTraceId { get; set; }
        public string StructuredSpanId { get; set; }
        public IReadOnlyDictionary<string, object> Properties { get; set; }
    }

    private sealed class TestHttpActivityFeature : IHttpActivityFeature
    {
        public TestHttpActivityFeature(Activity activity)
        {
            Activity = activity;
        }

        public Activity Activity { get; set; }
    }

    private sealed class NoopScope : IDisposable
    {
        public static NoopScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
