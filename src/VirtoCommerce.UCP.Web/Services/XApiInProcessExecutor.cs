using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using GraphQL;
using GraphQL.Types;
using GraphQLParser;
using GraphQLParser.AST;
using GraphQLParser.Exceptions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using VirtoCommerce.UCP.Core.Diagnostics;
using VirtoCommerce.UCP.Core.Models;
using VirtoCommerce.UCP.Core.Services;
using VirtoCommerce.UCP.Web.Diagnostics;
using VirtoCommerce.Xapi.Core.Infrastructure;

namespace VirtoCommerce.UCP.Web.Services;

public class XApiInProcessExecutor : IXApiInProcessExecutor
{
    private const int MaxLoggedGraphQlExceptions = 5;
    private const int MaxGraphQlErrorItems = 5;
    private const int MaxGraphQlErrorValueLength = 128;
    private const int MaxGraphQlErrorMessageLength = 256;
    private const int MaxGraphQlPathSegments = 10;
    private const int MaxGraphQlPathLength = 256;
    internal const int MaxCachedOperationTypes = 128;
    private static readonly EventId GraphQlResolverExceptionEvent = new(2001, "XApiGraphQlResolverException");
    private static readonly ConcurrentDictionary<(string Query, string OperationName), string> OperationTypes = new();
    private static readonly object OperationTypesLock = new();
    private static readonly ConcurrentDictionary<Type, string> SchemaVersions = new();

    internal static int CachedOperationTypeCount => OperationTypes.Count;

    private readonly XApiDocumentExecuters _documentExecuters;
    private readonly IGraphQLTextSerializer _graphQlSerializer;
    private readonly IServiceProvider _serviceProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly UcpOperationTelemetry _operationTelemetry;
    private readonly ILogger<XApiInProcessExecutor> _logger;

    public XApiInProcessExecutor(
        XApiDocumentExecuters documentExecuters,
        IGraphQLTextSerializer graphQlSerializer,
        IServiceProvider serviceProvider,
        IHttpContextAccessor httpContextAccessor,
        UcpOperationTelemetry operationTelemetry,
        ILogger<XApiInProcessExecutor> logger)
    {
        _documentExecuters = documentExecuters;
        _graphQlSerializer = graphQlSerializer;
        _serviceProvider = serviceProvider;
        _httpContextAccessor = httpContextAccessor;
        _operationTelemetry = operationTelemetry;
        _logger = logger;
    }

    public virtual Task<XApiExecutionResult> Execute(XApiExecutionRequest request, CancellationToken cancellationToken = default)
    {
        return Execute(_documentExecuters.Catalog, "XCatalog", request, cancellationToken);
    }

    public virtual Task<XApiExecutionResult> ExecuteCart(XApiExecutionRequest request, CancellationToken cancellationToken = default)
    {
        return Execute(_documentExecuters.Cart, "XCart", request, cancellationToken);
    }

    public virtual Task<XApiExecutionResult> ExecuteOrder(XApiExecutionRequest request, CancellationToken cancellationToken = default)
    {
        return Execute(_documentExecuters.Order, "XOrder", request, cancellationToken);
    }

    protected virtual Task<XApiExecutionResult> Execute<TSchemaFactory>(
        IDocumentExecuter<TSchemaFactory> documentExecuter,
        string schema,
        XApiExecutionRequest request,
        CancellationToken cancellationToken)
        where TSchemaFactory : ISchema
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Query);

        return ExecuteCore(documentExecuter, schema, request, cancellationToken);
    }

    private async Task<XApiExecutionResult> ExecuteCore<TSchemaFactory>(
        IDocumentExecuter<TSchemaFactory> documentExecuter,
        string schema,
        XApiExecutionRequest request,
        CancellationToken cancellationToken)
        where TSchemaFactory : ISchema
    {
        var call = CreateCallContext<TSchemaFactory>(schema, request);
        using var activity = call.Activity;
        var contentType = SetJsonContentType();

        try
        {
            var executionResult = await ExecuteDocument(documentExecuter, schema, request, call, cancellationToken);
            return CreateExecutionResult(executionResult, call);
        }
        catch (OperationCanceledException)
        {
            MarkCanceled(call);
            throw;
        }
        catch (Exception exception)
        {
            RecordExecutionException(call.Activity, exception);
            throw;
        }
        finally
        {
            RestoreContentType(contentType);
            CompleteCallTelemetry(call);
        }
    }

    private XApiCallTelemetryContext CreateCallContext<TSchemaFactory>(string schema, XApiExecutionRequest request)
        where TSchemaFactory : ISchema
    {
        var operationName = string.IsNullOrWhiteSpace(request.OperationName) ? "anonymous" : request.OperationName;
        var operationType = GetOperationType(request.Query, request.OperationName);
        var isMutation = operationType == "mutation";
        var callIndex = _operationTelemetry.BeginXApiCall(isMutation);
        var requestSnapshot = XApiRequestTelemetrySnapshot.Create(request.Variables, _operationTelemetry.IsInputCaptureEnabled);
        var schemaVersion = GetSchemaVersion<TSchemaFactory>();
        _operationTelemetry.CaptureXApiRequest(requestSnapshot);
        var activity = UcpDiagnostics.StartXApi(schema, operationName, operationType, callIndex);
        requestSnapshot.Enrich(activity);
        activity?.SetTag("vc.xapi.schema.version", schemaVersion);

        return new XApiCallTelemetryContext(
            schema,
            operationName,
            isMutation,
            callIndex,
            requestSnapshot,
            schemaVersion,
            activity);
    }

    private Task<ExecutionResult> ExecuteDocument<TSchemaFactory>(
        IDocumentExecuter<TSchemaFactory> documentExecuter,
        string schema,
        XApiExecutionRequest request,
        XApiCallTelemetryContext call,
        CancellationToken cancellationToken)
        where TSchemaFactory : ISchema
    {
        var exceptionTelemetry = new GraphQlExceptionTelemetryContext(
            call.Activity,
            schema,
            call.OperationName,
            call.CallIndex,
            call.RequestSnapshot,
            call.SchemaVersion,
            call.ExceptionLogState);

        return documentExecuter.ExecuteAsync(options =>
        {
            options.Query = request.Query;
            options.OperationName = request.OperationName;
            options.UserContext = new GraphQLUserContext(request.User);
            options.Variables = new Inputs(request.Variables ?? new Dictionary<string, object>());
            options.RequestServices = _serviceProvider;
            options.CancellationToken = cancellationToken;
            var existingHandler = options.UnhandledExceptionDelegate;
            options.UnhandledExceptionDelegate = context => HandleUnhandledGraphQlException(
                context,
                exceptionTelemetry,
                existingHandler);
        });
    }

    private XApiExecutionResult CreateExecutionResult(ExecutionResult executionResult, XApiCallTelemetryContext call)
    {
        call.ErrorCount = executionResult.Errors == null ? 0 : executionResult.Errors.Count;
        if (call.ErrorCount > 0)
        {
            SetGraphQlErrorData(call.Activity, executionResult);
            RecordUnhandledGraphQlExceptions(
                executionResult,
                CreateExceptionTelemetryContext(call));
        }

        var result = new XApiExecutionResult
        {
            Succeeded = call.ErrorCount == 0,
            Json = _graphQlSerializer.Serialize(executionResult),
        };
        call.Completed = true;

        return result;
    }

    private static GraphQlExceptionTelemetryContext CreateExceptionTelemetryContext(XApiCallTelemetryContext call)
    {
        return new GraphQlExceptionTelemetryContext(
            call.Activity,
            call.Schema,
            call.OperationName,
            call.CallIndex,
            call.RequestSnapshot,
            call.SchemaVersion,
            call.ExceptionLogState);
    }

    private ContentTypeState SetJsonContentType()
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext == null)
        {
            return new ContentTypeState(null, null);
        }

        var originalContentType = httpContext.Request.ContentType;
        // XAPI validates the internal GraphQL media type independently of the outer REST/MCP transport.
        httpContext.Request.ContentType = "application/json";

        return new ContentTypeState(httpContext, originalContentType);
    }

    private static void RestoreContentType(ContentTypeState state)
    {
        if (state.HttpContext != null)
        {
            state.HttpContext.Request.ContentType = state.OriginalContentType;
        }
    }

    private static void MarkCanceled(XApiCallTelemetryContext call)
    {
        call.Canceled = true;
        call.Activity?.SetTag("vc.xapi.outcome", "canceled");
    }

    private static void RecordExecutionException(Activity activity, Exception exception)
    {
        if (activity == null)
        {
            return;
        }

        UcpActivityExceptionRecorder.Record(activity, exception);
        activity.SetTag("error.type", exception.GetType().FullName);
        activity.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
    }

    private void CompleteCallTelemetry(XApiCallTelemetryContext call)
    {
        var failed = !call.Canceled && (call.ErrorCount > 0 || !call.Completed);
        if (_operationTelemetry.ShouldWriteXApiInput(failed))
        {
            call.RequestSnapshot.EnrichInput(call.Activity);
        }

        _operationTelemetry.CompleteXApiCall(
            call.IsMutation,
            call.ErrorCount,
            call.Completed,
            call.Canceled);
    }

    protected static string GetOperationType(string query, string operationName)
    {
        var key = (query, operationName);
        if (OperationTypes.TryGetValue(key, out var operationType))
        {
            return operationType;
        }

        operationType = ParseOperationType(query, operationName);
        lock (OperationTypesLock)
        {
            if (OperationTypes.TryGetValue(key, out var cachedOperationType))
            {
                return cachedOperationType;
            }

            if (OperationTypes.Count < MaxCachedOperationTypes)
            {
                OperationTypes.TryAdd(key, operationType);
            }
        }

        return operationType;
    }

    private static string ParseOperationType(string query, string operationName)
    {
        try
        {
            var document = Parser.Parse(query.TrimStart('\uFEFF'));
            var operations = document.Definitions
                .OfType<GraphQLOperationDefinition>();

            var candidates = string.IsNullOrWhiteSpace(operationName)
                ? operations.Take(2).ToList()
                : operations
                    .Where(operation => string.Equals(operation.Name?.StringValue, operationName, StringComparison.Ordinal))
                    .Take(2)
                    .ToList();

            if (candidates.Count != 1)
            {
                return "unknown";
            }

            return candidates[0].Operation switch
            {
                OperationType.Query => "query",
                OperationType.Mutation => "mutation",
                OperationType.Subscription => "subscription",
                _ => "unknown",
            };
        }
        catch (GraphQLParserException)
        {
            return "unknown";
        }
    }

    private static void SetGraphQlErrorData(Activity activity, ExecutionResult executionResult)
    {
        if (activity == null)
        {
            return;
        }

        activity.SetTag("vc.xapi.error.count", executionResult.Errors.Count);
        activity.SetTag("vc.xapi.error.codes", JoinBounded(executionResult.Errors.Select(error => error.Code)));
        activity.SetTag("vc.xapi.error.paths", JoinBounded(executionResult.Errors.Select(error => error.Path == null ? null : string.Join('.', error.Path))));
        activity.SetTag(
            "vc.xapi.error.messages",
            JoinBounded(executionResult.Errors.Select(error => error.Message), MaxGraphQlErrorItems, MaxGraphQlErrorMessageLength));
        activity.SetStatus(ActivityStatusCode.Error, "GraphQL errors");
    }

    protected virtual async Task HandleUnhandledGraphQlException(
        GraphQL.Execution.UnhandledExceptionContext context,
        GraphQlExceptionTelemetryContext telemetry,
        Func<GraphQL.Execution.UnhandledExceptionContext, Task> existingHandler)
    {
        var exception = context.OriginalException;
        if (exception != null && telemetry.LogState.ShouldLog(exception))
        {
            LogUnhandledGraphQlException(GetErrorPath(context), telemetry, exception);
        }

        if (existingHandler != null)
        {
            await existingHandler(context);
        }
    }

    protected virtual void RecordUnhandledGraphQlExceptions(
        ExecutionResult executionResult,
        GraphQlExceptionTelemetryContext telemetry)
    {
        if (executionResult.Errors == null)
        {
            return;
        }

        foreach (var error in executionResult.Errors)
        {
            if (error is not GraphQL.Execution.UnhandledError { InnerException: { } exception } ||
                !telemetry.LogState.ShouldLog(exception))
            {
                continue;
            }

            var errorPath = error.Path == null ? null : GetBoundedPath(error.Path);
            LogUnhandledGraphQlException(errorPath, telemetry, exception);
        }
    }

    private void LogUnhandledGraphQlException(
        string errorPath,
        GraphQlExceptionTelemetryContext telemetry,
        Exception exception)
    {
        var requestSnapshot = telemetry.RequestSnapshot;
        var inputJson = (_operationTelemetry?.ShouldWriteXApiInput(failed: true) ?? true)
            ? requestSnapshot.SafeInputJson
            : null;
        var (traceId, spanId) = GetTraceIdentifiers(telemetry.Activity);

        if (telemetry.Activity != null)
        {
            telemetry.Activity.SetTag("error.type", exception.GetType().FullName);
            UcpActivityExceptionRecorder.Record(telemetry.Activity, exception);
        }

        _logger.LogError(
            GraphQlResolverExceptionEvent,
            exception,
            "event:{EventName} schema:{XApiSchema} operation:{XApiOperation} error_type:{XApiErrorType} " +
            "error_path:{XApiErrorPath} call_index:{XApiCallIndex} schema_version:{XApiSchemaVersion} " +
            "xapi_variables:{XApiVariableNames} store_id:{StoreId} currency:{CurrencyCode} culture:{CultureName} " +
            "page_size:{PageSize} filter_present:{FilterPresent} xapi_input_json:{XApiInputJson} " +
            "reference_type:{RequestReferenceType} reference_value:{RequestReferenceValue} " +
            "trace_id:{TraceId} span_id:{SpanId}",
            "XApiGraphQlException",
            telemetry.Schema,
            telemetry.OperationName,
            exception.GetType().FullName,
            errorPath,
            telemetry.CallIndex,
            telemetry.SchemaVersion,
            requestSnapshot.VariableNames,
            requestSnapshot.StoreId,
            requestSnapshot.CurrencyCode,
            requestSnapshot.CultureName,
            requestSnapshot.PageSize,
            requestSnapshot.FilterPresent,
            inputJson,
            requestSnapshot.ReferenceType,
            requestSnapshot.ReferenceValue,
            traceId,
            spanId);
    }

    private static string GetErrorPath(GraphQL.Execution.UnhandledExceptionContext context)
    {
        if (context.FieldContext == null)
        {
            return null;
        }

        var path = context.FieldContext.ResponsePath ?? context.FieldContext.Path;
        return path == null ? null : GetBoundedPath(path);
    }

    private static (string TraceId, string SpanId) GetTraceIdentifiers(Activity activity)
    {
        var traceActivity = activity ?? Activity.Current;
        if (traceActivity == null)
        {
            return (null, null);
        }

        return (traceActivity.TraceId.ToString(), traceActivity.SpanId.ToString());
    }

    private static string JoinBounded(IEnumerable<string> values)
    {
        return JoinBounded(values, MaxGraphQlErrorItems, MaxGraphQlErrorValueLength);
    }

    private static string JoinBounded(IEnumerable<string> values, int maxItems, int maxValueLength)
    {
        return string.Join(',', values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .Take(maxItems)
            .Select(value => value.Length > maxValueLength ? value[..maxValueLength] : value));
    }

    private static string GetBoundedPath(IEnumerable<object> path)
    {
        var value = string.Join('.', path.Take(MaxGraphQlPathSegments));
        return value.Length > MaxGraphQlPathLength ? value[..MaxGraphQlPathLength] : value;
    }

    private static string GetSchemaVersion<TSchemaFactory>()
        where TSchemaFactory : ISchema
    {
        return SchemaVersions.GetOrAdd(typeof(TSchemaFactory), GetSchemaVersion);
    }

    private static string GetSchemaVersion(Type schemaFactoryType)
    {
        var markerType = schemaFactoryType.IsGenericType
            ? schemaFactoryType.GetGenericArguments().FirstOrDefault()
            : null;
        var assembly = markerType?.Assembly;

        return assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly?.GetName().Version?.ToString();
    }

    private sealed class XApiCallTelemetryContext
    {
        public XApiCallTelemetryContext(
            string schema,
            string operationName,
            bool isMutation,
            int callIndex,
            XApiRequestTelemetrySnapshot requestSnapshot,
            string schemaVersion,
            Activity activity)
        {
            Schema = schema;
            OperationName = operationName;
            IsMutation = isMutation;
            CallIndex = callIndex;
            RequestSnapshot = requestSnapshot;
            SchemaVersion = schemaVersion;
            Activity = activity;
        }

        public string Schema { get; }
        public string OperationName { get; }
        public bool IsMutation { get; }
        public int CallIndex { get; }
        public XApiRequestTelemetrySnapshot RequestSnapshot { get; }
        public string SchemaVersion { get; }
        public Activity Activity { get; }
        public GraphQlExceptionLogState ExceptionLogState { get; } = new();
        public int ErrorCount { get; set; }
        public bool Completed { get; set; }
        public bool Canceled { get; set; }
    }

    private sealed class ContentTypeState
    {
        public ContentTypeState(HttpContext httpContext, string originalContentType)
        {
            HttpContext = httpContext;
            OriginalContentType = originalContentType;
        }

        public HttpContext HttpContext { get; }
        public string OriginalContentType { get; }
    }

    protected sealed class GraphQlExceptionTelemetryContext
    {
        public GraphQlExceptionTelemetryContext(
            Activity activity,
            string schema,
            string operationName,
            int callIndex,
            IDictionary<string, object> requestVariables,
            string schemaVersion,
            GraphQlExceptionLogState logState)
            : this(
                activity,
                schema,
                operationName,
                callIndex,
                XApiRequestTelemetrySnapshot.Create(requestVariables),
                schemaVersion,
                logState)
        {
            RequestVariables = requestVariables;
        }

        internal GraphQlExceptionTelemetryContext(
            Activity activity,
            string schema,
            string operationName,
            int callIndex,
            XApiRequestTelemetrySnapshot requestSnapshot,
            string schemaVersion,
            GraphQlExceptionLogState logState)
        {
            Activity = activity;
            Schema = schema;
            OperationName = operationName;
            CallIndex = callIndex;
            RequestSnapshot = requestSnapshot;
            SchemaVersion = schemaVersion;
            LogState = logState;
        }

        public Activity Activity { get; }
        public string Schema { get; }
        public string OperationName { get; }
        public int CallIndex { get; }
        public IDictionary<string, object> RequestVariables { get; }
        internal XApiRequestTelemetrySnapshot RequestSnapshot { get; }
        public string SchemaVersion { get; }
        public GraphQlExceptionLogState LogState { get; }
    }

    protected sealed class GraphQlExceptionLogState
    {
        private readonly ConcurrentDictionary<Exception, byte> _loggedExceptions = new(ReferenceEqualityComparer.Instance);
        private int _loggedCount;

        public bool ShouldLog(Exception exception)
        {
            return _loggedExceptions.TryAdd(exception, 0)
                && Interlocked.Increment(ref _loggedCount) <= MaxLoggedGraphQlExceptions;
        }
    }

}
