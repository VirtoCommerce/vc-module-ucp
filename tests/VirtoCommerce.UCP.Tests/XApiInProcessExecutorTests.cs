using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using GraphQL;
using GraphQL.Execution;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Diagnostics;
using VirtoCommerce.UCP.Core.Options;
using VirtoCommerce.UCP.Web.Diagnostics;
using VirtoCommerce.UCP.Web.Services;
using Xunit;

namespace VirtoCommerce.UCP.Tests;

[Trait("Category", "Unit")]
public class XApiInProcessExecutorTests
{
    [Theory]
    [InlineData("{ viewer { id } }", "query")]
    [InlineData("query ReadViewer { viewer { id } }", "query")]
    [InlineData("mutation UpdateCart { updateCart { id } }", "mutation")]
    [InlineData("subscription WatchCart { cartUpdated { id } }", "subscription")]
    public void GetOperationType_ClassifiesSingleOperation(string query, string expected)
    {
        var actual = TestableXApiInProcessExecutor.ClassifyOperation(query);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void GetOperationType_IgnoresBomWhitespaceAndComments()
    {
        const string query = "\uFEFF  # mutation FakeOperation\r\n\tmutation UpdateCart { updateCart { id } }";

        var actual = TestableXApiInProcessExecutor.ClassifyOperation(query, "UpdateCart");

        Assert.Equal("mutation", actual);
    }

    [Theory]
    [InlineData("ReadViewer", "query")]
    [InlineData("UpdateCart", "mutation")]
    [InlineData("WatchCart", "subscription")]
    public void GetOperationType_UsesOperationNameForMultiOperationDocument(string operationName, string expected)
    {
        const string query = """
            query ReadViewer { viewer { id } }
            mutation UpdateCart { updateCart { id } }
            subscription WatchCart { cartUpdated { id } }
            """;

        var actual = TestableXApiInProcessExecutor.ClassifyOperation(query, operationName);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetOperationType_ReturnsUnknownForAmbiguousMultiOperationDocument(string operationName)
    {
        const string query = """
            query ReadViewer { viewer { id } }
            mutation UpdateCart { updateCart { id } }
            """;

        var actual = TestableXApiInProcessExecutor.ClassifyOperation(query, operationName);

        Assert.Equal("unknown", actual);
    }

    [Theory]
    [InlineData("MissingOperation")]
    [InlineData("updateCart")]
    public void GetOperationType_ReturnsUnknownWhenOperationNameDoesNotMatchExactly(string operationName)
    {
        const string query = "mutation UpdateCart { updateCart { id } }";

        var actual = TestableXApiInProcessExecutor.ClassifyOperation(query, operationName);

        Assert.Equal("unknown", actual);
    }

    [Theory]
    [InlineData("fragment ViewerFields on Viewer { id }")]
    [InlineData("query Broken {")]
    public void GetOperationType_ReturnsUnknownWhenNoExecutableOperationCanBeSelected(string query)
    {
        var actual = TestableXApiInProcessExecutor.ClassifyOperation(query);

        Assert.Equal("unknown", actual);
    }

    [Fact]
    public void GetOperationType_DoesNotCacheUnboundedDynamicQueries()
    {
        for (var index = 0; index < XApiInProcessExecutor.MaxCachedOperationTypes * 2; index++)
        {
            var query = $"query DynamicOperation{index} {{ __typename }}";

            Assert.Equal("query", TestableXApiInProcessExecutor.ClassifyOperation(query));
        }

        Assert.InRange(
            XApiInProcessExecutor.CachedOperationTypeCount,
            1,
            XApiInProcessExecutor.MaxCachedOperationTypes);
    }

    [Fact]
    public async Task UnhandledExceptionHandler_LogsOriginalTechnicalExceptionOnceWithCorrelationData()
    {
        var logger = new CapturingLogger();
        var executor = new TestableXApiInProcessExecutor(logger);
        var exception = CreateExceptionWithStackTrace();
        var context = new UnhandledExceptionContext(new ExecutionOptions(), exception);
        using var activity = new Activity("XAPI XCatalog UcpSearchProducts").Start();

        await executor.HandleUnhandledException(context, activity, "XCatalog", "UcpSearchProducts", 1);
        await executor.HandleUnhandledException(context, activity, "XCatalog", "UcpSearchProducts", 1);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal(2001, entry.EventId.Id);
        Assert.Equal("XApiGraphQlResolverException", entry.EventId.Name);
        Assert.Same(exception, entry.Exception);
        Assert.Equal("XApiGraphQlException", entry.Properties["EventName"]);
        Assert.Equal("XCatalog", entry.Properties["XApiSchema"]);
        Assert.Equal("UcpSearchProducts", entry.Properties["XApiOperation"]);
        Assert.Equal(typeof(NullReferenceException).FullName, entry.Properties["XApiErrorType"]);
        Assert.Null(entry.Properties["XApiErrorPath"]);
        Assert.Equal("1", entry.Properties["XApiCallIndex"]?.ToString());
        Assert.Equal(activity.TraceId.ToString(), entry.Properties["TraceId"]);
        Assert.Equal(activity.SpanId.ToString(), entry.Properties["SpanId"]);
        Assert.Equal(typeof(NullReferenceException).FullName, activity.GetTagItem("error.type"));

        var exceptionEvent = Assert.Single(activity.Events, value => value.Name == "exception");
        Assert.Equal(typeof(NullReferenceException).FullName, exceptionEvent.Tags.First(x => x.Key == "exception.type").Value);
        Assert.Equal("resolver canary", exceptionEvent.Tags.First(x => x.Key == "exception.message").Value);
        Assert.Contains(
            nameof(CreateExceptionWithStackTrace),
            exceptionEvent.Tags.First(x => x.Key == "exception.stacktrace").Value?.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnhandledExceptionHandler_LogsBoundedSafeRequestSnapshot()
    {
        var logger = new CapturingLogger();
        var executor = new TestableXApiInProcessExecutor(logger);
        var exception = new NullReferenceException("resolver canary");
        var context = new UnhandledExceptionContext(new ExecutionOptions(), exception);
        var variables = new Dictionary<string, object>
        {
            ["storeId"] = "B2B-store",
            ["userId"] = "buyer-secret@example.com",
            ["currencyCode"] = "USD",
            ["cultureName"] = "ru-RU",
            ["query"] = "микро\rволновка",
            ["filter"] = "price:[100 TO 500]",
            ["first"] = 10,
        };
        using var activity = new Activity("XAPI XCatalog UcpSearchProducts").Start();

        await executor.HandleUnhandledException(
            context,
            activity,
            "XCatalog",
            "UcpSearchProducts",
            1,
            variables: variables);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal("3.1015.0+test", entry.Properties["XApiSchemaVersion"]);
        Assert.Equal("cultureName,currencyCode,filter,first,query,storeId,userId", entry.Properties["XApiVariableNames"]);
        Assert.Equal("B2B-store", entry.Properties["StoreId"]);
        Assert.Equal("USD", entry.Properties["CurrencyCode"]);
        Assert.Equal("ru-RU", entry.Properties["CultureName"]);
        Assert.Equal(10, entry.Properties["PageSize"]);
        Assert.Equal(true, entry.Properties["FilterPresent"]);
        using var input = JsonDocument.Parse(Assert.IsType<string>(entry.Properties["XApiInputJson"]));
        Assert.Equal("B2B-store", input.RootElement.GetProperty("store_id").GetString());
        Assert.Equal("USD", input.RootElement.GetProperty("currency").GetString());
        Assert.Equal("ru-RU", input.RootElement.GetProperty("culture").GetString());
        Assert.Equal("микро волновка", input.RootElement.GetProperty("query").GetString());
        Assert.Equal("price:[100 TO 500]", input.RootElement.GetProperty("filter").GetString());
        Assert.DoesNotContain(entry.Properties.Values, value => value?.ToString() == "buyer-secret@example.com");
        Assert.DoesNotContain("UserId", entry.Properties.Keys);
    }

    [Fact]
    public async Task UnhandledExceptionHandler_OmitsInputJsonWhenCaptureModeIsNone()
    {
        var logger = new CapturingLogger();
        var telemetry = new UcpOperationTelemetry(
            NullLogger<UcpOperationTelemetry>.Instance,
            Options.Create(new UcpOptions
            {
                Observability = new UcpObservabilityOptions { InputCaptureMode = UcpInputCaptureMode.None },
            }));
        telemetry.Begin(ModuleConstants.Operations.SearchProducts, "rest");
        var executor = new TestableXApiInProcessExecutor(logger, telemetry);
        var exception = new NullReferenceException("resolver canary");
        var context = new UnhandledExceptionContext(new ExecutionOptions(), exception);
        using var activity = new Activity("XAPI XCatalog UcpSearchProducts").Start();

        await executor.HandleUnhandledException(
            context,
            activity,
            "XCatalog",
            "UcpSearchProducts",
            1,
            variables: new Dictionary<string, object> { ["query"] = "private diagnostic query" });
        telemetry.Complete();

        var entry = Assert.Single(logger.Entries);
        Assert.Null(entry.Properties["XApiInputJson"]);
        Assert.DoesNotContain("private diagnostic query", entry.Properties.Values);
    }

    [Fact]
    public void RequestSnapshot_EnrichesXApiSpanWithBoundedDiagnosticInputWithoutPii()
    {
        var snapshot = XApiRequestTelemetrySnapshot.Create(new Dictionary<string, object>
        {
            ["storeId"] = "B2B-store",
            ["userId"] = "buyer-secret@example.com",
            ["currencyCode"] = "USD",
            ["cultureName"] = "en-US",
            ["query"] = "Carriage Bolt secret@example.com +1 555 123 4567",
            ["filter"] = "price:[100 TO 500]",
            ["first"] = 3,
        });
        using var activity = new Activity("XAPI XCatalog UcpSearchProducts").Start();

        snapshot.Enrich(activity);
        snapshot.EnrichInput(activity);

        Assert.Equal("Carriage Bolt [redacted-email] [redacted-phone]", activity.GetTagItem("vc.catalog.search.query"));
        Assert.Equal("price:[100 TO 500]", activity.GetTagItem("vc.catalog.search.filter"));
        using var input = JsonDocument.Parse(Assert.IsType<string>(activity.GetTagItem("vc.xapi.input_json")));
        Assert.Equal("Carriage Bolt [redacted-email] [redacted-phone]", input.RootElement.GetProperty("query").GetString());
        Assert.Equal(3, input.RootElement.GetProperty("page_size").GetInt32());
        var inputJson = input.RootElement.GetRawText();
        Assert.DoesNotContain("secret@example.com", inputJson, StringComparison.Ordinal);
        Assert.DoesNotContain("555 123 4567", inputJson, StringComparison.Ordinal);
        Assert.DoesNotContain("buyer-secret@example.com", inputJson, StringComparison.Ordinal);
    }

    [Fact]
    public void RequestSnapshot_WithoutInputPayload_RetainsOnlySafeDerivedSearchTags()
    {
        var snapshot = XApiRequestTelemetrySnapshot.Create(
            new Dictionary<string, object>
            {
                ["query"] = "private@example.com +1 555 123 4567",
            },
            captureInputValues: false);
        using var activity = new Activity("XAPI XCatalog UcpSearchProducts").Start();

        snapshot.Enrich(activity);

        Assert.Null(snapshot.SafeInputJson);
        Assert.Equal(35, activity.GetTagItem("vc.catalog.search.query.length"));
        Assert.NotNull(activity.GetTagItem("vc.catalog.search.query.hash"));
        Assert.Null(activity.GetTagItem("vc.catalog.search.query"));
    }

    [Fact]
    public async Task UnhandledExceptionHandler_CapturesNestedXCartCommandWithoutAddressOrCouponSecrets()
    {
        var logger = new CapturingLogger();
        var executor = new TestableXApiInProcessExecutor(logger);
        var exception = new InvalidOperationException("mutation failed");
        var context = new UnhandledExceptionContext(new ExecutionOptions(), exception);
        var variables = new Dictionary<string, object>
        {
            ["command"] = new Dictionary<string, object>
            {
                ["storeId"] = "B2B-store",
                ["userId"] = "buyer-private@example.com",
                ["cartName"] = "Ada private shopping list",
                ["cartId"] = "cart-marker",
                ["productId"] = "product-marker",
                ["lineItemId"] = "line-marker",
                ["quantity"] = 3,
                ["couponCode"] = "SECRET-COUPON",
                ["address"] = new Dictionary<string, object>
                {
                    ["firstName"] = "Secret",
                    ["lastName"] = "Buyer",
                    ["line1"] = "123 Secret Street",
                    ["postalCode"] = "12345",
                    ["email"] = "secret@example.com",
                    ["countryCode"] = "USA",
                    ["regionId"] = "CA",
                },
            },
        };
        using var activity = new Activity("XAPI XCart UcpAddCartItem").Start();

        await executor.HandleUnhandledException(context, activity, "XCart", "UcpAddCartItem", 4, variables: variables);

        var entry = Assert.Single(logger.Entries);
        using var input = JsonDocument.Parse(Assert.IsType<string>(entry.Properties["XApiInputJson"]));
        Assert.Equal("cart-marker", input.RootElement.GetProperty("cart_id").GetString());
        Assert.Equal("product-marker", input.RootElement.GetProperty("product_id").GetString());
        Assert.Equal("line-marker", input.RootElement.GetProperty("line_item_id").GetString());
        Assert.Equal(3, input.RootElement.GetProperty("quantity").GetInt32());
        Assert.True(input.RootElement.GetProperty("coupon_present").GetBoolean());
        Assert.Equal("USA", input.RootElement.GetProperty("address").GetProperty("country_code").GetString());
        Assert.True(input.RootElement.GetProperty("address").GetProperty("postal_code_present").GetBoolean());
        var json = input.RootElement.GetRawText();
        Assert.DoesNotContain("SECRET-COUPON", json, StringComparison.Ordinal);
        Assert.DoesNotContain("123 Secret Street", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret@example.com", json, StringComparison.Ordinal);
        Assert.DoesNotContain("buyer-private@example.com", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Ada private shopping list", json, StringComparison.Ordinal);
        Assert.DoesNotContain("12345", json, StringComparison.Ordinal);
        Assert.True(input.RootElement.GetProperty("user_id_present").GetBoolean());
        Assert.True(input.RootElement.GetProperty("cart_name_present").GetBoolean());
    }

    [Fact]
    public async Task UnhandledExceptionHandler_PreservesExistingHandler()
    {
        var logger = new CapturingLogger();
        var executor = new TestableXApiInProcessExecutor(logger);
        var exception = new InvalidOperationException("resolver canary");
        var context = new UnhandledExceptionContext(new ExecutionOptions(), exception);
        var existingHandlerCalls = 0;
        using var activity = new Activity("XAPI XCatalog UcpSearchProducts").Start();

        await executor.HandleUnhandledException(
            context,
            activity,
            "XCatalog",
            "UcpSearchProducts",
            1,
            _ =>
            {
                existingHandlerCalls++;
                return Task.CompletedTask;
            });

        Assert.Equal(1, existingHandlerCalls);
        Assert.Single(logger.Entries);
    }

    [Fact]
    public void ExecutionResultFallback_RecordsOriginalUnhandledExceptionWhenCallbackWasSkipped()
    {
        var logger = new CapturingLogger();
        var executor = new TestableXApiInProcessExecutor(logger);
        var expected = CreateExceptionWithStackTrace();
        var executionResult = new ExecutionResult
        {
            Errors = new ExecutionErrors
            {
                new UnhandledError("Error trying to resolve field 'products'.", expected),
            },
        };
        using var activity = new Activity("XAPI XCatalog UcpSearchProducts").Start();

        executor.RecordUnhandledExceptions(executionResult, activity, "XCatalog", "UcpSearchProducts", 1);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(2001, entry.EventId.Id);
        Assert.Same(expected, entry.Exception);
        Assert.Same(expected, UcpActivityExceptionRecorder.GetOriginalException(activity));
        Assert.Single(activity.Events, value => value.Name == "exception");
    }

    [Fact]
    public void ExecutionResultFallback_DoesNotTreatExpectedGraphQlErrorAsClrException()
    {
        var logger = new CapturingLogger();
        var executor = new TestableXApiInProcessExecutor(logger);
        var executionResult = new ExecutionResult
        {
            Errors = new ExecutionErrors
            {
                new ExecutionError("Expected validation error."),
            },
        };
        using var activity = new Activity("XAPI XCatalog UcpSearchProducts").Start();

        executor.RecordUnhandledExceptions(executionResult, activity, "XCatalog", "UcpSearchProducts", 1);

        Assert.Empty(logger.Entries);
        Assert.Empty(activity.Events);
        Assert.Null(UcpActivityExceptionRecorder.GetOriginalException(activity));
    }

    private sealed class TestableXApiInProcessExecutor : XApiInProcessExecutor
    {
        private readonly GraphQlExceptionLogState _logState = new();

        private TestableXApiInProcessExecutor()
            : base(new XApiDocumentExecuters(null, null, null), null, null, null, null, null)
        {
        }

        public TestableXApiInProcessExecutor(
            ILogger<XApiInProcessExecutor> logger,
            UcpOperationTelemetry operationTelemetry = null)
            : base(new XApiDocumentExecuters(null, null, null), null, null, null, operationTelemetry, logger)
        {
        }

        public static string ClassifyOperation(string query, string operationName = null)
        {
            return GetOperationType(query, operationName);
        }

        public Task HandleUnhandledException(
            UnhandledExceptionContext context,
            Activity activity,
            string schema,
            string operationName,
            int callIndex,
            Func<UnhandledExceptionContext, Task> existingHandler = null,
            IDictionary<string, object> variables = null,
            string schemaVersion = "3.1015.0+test")
        {
            var telemetry = new GraphQlExceptionTelemetryContext(
                activity,
                schema,
                operationName,
                callIndex,
                variables,
                schemaVersion,
                _logState);

            return HandleUnhandledGraphQlException(
                context,
                telemetry,
                existingHandler);
        }

        public void RecordUnhandledExceptions(
            ExecutionResult executionResult,
            Activity activity,
            string schema,
            string operationName,
            int callIndex)
        {
            var telemetry = new GraphQlExceptionTelemetryContext(
                activity,
                schema,
                operationName,
                callIndex,
                requestVariables: null,
                schemaVersion: "3.1015.0+test",
                _logState);

            RecordUnhandledGraphQlExceptions(executionResult, telemetry);
        }
    }

    private static NullReferenceException CreateExceptionWithStackTrace()
    {
        try
        {
            throw new NullReferenceException("resolver canary");
        }
        catch (NullReferenceException exception)
        {
            return exception;
        }
    }

    private sealed class CapturingLogger : ILogger<XApiInProcessExecutor>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
        {
            return NullScope.Instance;
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
            Entries.Add(new LogEntry(logLevel, eventId, exception, properties));
        }
    }

    private sealed record LogEntry(LogLevel Level, EventId EventId, Exception Exception, IReadOnlyDictionary<string, object> Properties);

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
