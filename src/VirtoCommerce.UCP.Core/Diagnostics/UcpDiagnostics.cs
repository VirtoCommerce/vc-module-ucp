using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;
using System.Threading.Tasks;

namespace VirtoCommerce.UCP.Core.Diagnostics;

public static class UcpDiagnostics
{
    public const string ActivitySourceName = "VirtoCommerce.UCP";
    public const string MeterName = "VirtoCommerce.UCP";

    public static readonly string ModuleVersion =
        typeof(UcpDiagnostics).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(UcpDiagnostics).Assembly.GetName().Version?.ToString();

    public static readonly ActivitySource ActivitySource = new(
        ActivitySourceName,
        ModuleVersion);

    public static readonly Meter Meter = new(MeterName, ModuleVersion);

    private static readonly Counter<long> OperationCounter = Meter.CreateCounter<long>("vc.ucp.operation.count");
    private static readonly Counter<long> XApiCallCounter = Meter.CreateCounter<long>("vc.xapi.call.count");
    private static readonly Counter<long> XApiFailedCallCounter = Meter.CreateCounter<long>("vc.xapi.failed_call.count");
    private static readonly Counter<long> XApiGraphQlErrorCounter = Meter.CreateCounter<long>("vc.xapi.graphql.error.count");
    private static readonly Counter<long> XApiCanceledCallCounter = Meter.CreateCounter<long>("vc.xapi.canceled_call.count");
    private static readonly Counter<long> XApiMutationCallCounter = Meter.CreateCounter<long>("vc.xapi.mutation.call.count");
    private static readonly Counter<long> XApiMutationFailedCallCounter = Meter.CreateCounter<long>("vc.xapi.mutation.failed_call.count");
    private static readonly Counter<long> DependencyCounter = Meter.CreateCounter<long>("vc.dependency.call.count");

    public static Activity StartOperation(string operation, string transport, ActivityContext? parentContext = null)
    {
        var activity = parentContext.HasValue
            ? ActivitySource.StartActivity($"UCP {operation}", ActivityKind.Internal, parentContext.Value)
            : ActivitySource.StartActivity($"UCP {operation}", ActivityKind.Internal);
        activity?.SetTag("vc.ucp.operation", operation);
        activity?.SetTag("vc.ucp.transport", transport);
        activity?.SetTag("vc.ucp.version", ModuleConstants.UcpVersion);
        activity?.SetTag("vc.ucp.module.version", ModuleVersion);

        return activity;
    }

    public static Activity StartXApi(string schema, string operationName, string operationType, int callIndex)
    {
        var activity = ActivitySource.StartActivity($"XAPI {schema} {operationName}", ActivityKind.Internal);
        activity?.SetTag("vc.dependency.system", "xapi");
        activity?.SetTag("vc.xapi.schema", schema);
        activity?.SetTag("graphql.operation.name", operationName);
        activity?.SetTag("graphql.operation.type", operationType);
        activity?.SetTag("vc.xapi.call.index", callIndex);

        return activity;
    }

    public static void RecordOperation(
        string operation,
        string transport,
        string outcome,
        in XApiMetricCounts xApi)
    {
        var tags = new TagList
        {
            { "vc.ucp.operation", operation },
            { "vc.ucp.transport", transport },
            { "vc.ucp.outcome", outcome },
        };

        OperationCounter.Add(1, tags);
        AddIfPositive(XApiCallCounter, xApi.CallCount, tags);
        AddIfPositive(XApiFailedCallCounter, xApi.FailedCallCount, tags);
        AddIfPositive(XApiGraphQlErrorCounter, xApi.GraphQlErrorCount, tags);
        AddIfPositive(XApiCanceledCallCounter, xApi.CanceledCallCount, tags);
        AddIfPositive(XApiMutationCallCounter, xApi.MutationCallCount, tags);
        AddIfPositive(XApiMutationFailedCallCounter, xApi.MutationFailedCallCount, tags);
    }

    public static async Task<T> ExecuteDependency<T>(string component, string operation, Func<Task<T>> execute)
    {
        return await ExecuteDependency("platform", component, operation, execute);
    }

    public static async Task<T> ExecuteDependency<T>(
        string system,
        string component,
        string operation,
        Func<Task<T>> execute,
        Func<T, string> classifyOutcome = null)
    {
        using var activity = StartDependency(system, component, operation);

        try
        {
            var result = await execute();
            var outcome = classifyOutcome?.Invoke(result) ?? "success";
            activity?.SetTag("vc.dependency.outcome", outcome);
            RecordDependency(system, component, operation, outcome);
            return result;
        }
        catch (OperationCanceledException)
        {
            activity?.SetTag("vc.dependency.outcome", "canceled");
            RecordDependency(system, component, operation, "canceled");
            throw;
        }
        catch (Exception exception)
        {
            activity?.SetTag("vc.dependency.outcome", "error");
            activity?.SetTag("error.type", exception.GetType().FullName);
            activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
            RecordDependency(system, component, operation, "error");
            throw;
        }
    }

    public static async Task ExecuteDependency(string component, string operation, Func<Task> execute)
    {
        await ExecuteDependency("platform", component, operation, execute);
    }

    public static async Task ExecuteDependency(string system, string component, string operation, Func<Task> execute)
    {
        using var activity = StartDependency(system, component, operation);

        try
        {
            await execute();
            activity?.SetTag("vc.dependency.outcome", "success");
            RecordDependency(system, component, operation, "success");
        }
        catch (OperationCanceledException)
        {
            activity?.SetTag("vc.dependency.outcome", "canceled");
            RecordDependency(system, component, operation, "canceled");
            throw;
        }
        catch (Exception exception)
        {
            activity?.SetTag("vc.dependency.outcome", "error");
            activity?.SetTag("error.type", exception.GetType().FullName);
            activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
            RecordDependency(system, component, operation, "error");
            throw;
        }
    }

    private static Activity StartDependency(string system, string component, string operation)
    {
        var activity = ActivitySource.StartActivity($"VC {component} {operation}", ActivityKind.Internal);
        activity?.SetTag("vc.dependency.system", system);
        activity?.SetTag("vc.dependency.component", component);
        activity?.SetTag("vc.dependency.operation", operation);

        return activity;
    }

    private static void RecordDependency(string system, string component, string operation, string outcome)
    {
        DependencyCounter.Add(1,
            new KeyValuePair<string, object>("vc.dependency.system", system),
            new KeyValuePair<string, object>("vc.dependency.component", component),
            new KeyValuePair<string, object>("vc.dependency.operation", operation),
            new KeyValuePair<string, object>("vc.dependency.outcome", outcome));
    }

    private static void AddIfPositive(Counter<long> counter, int value, in TagList tags)
    {
        if (value > 0)
        {
            counter.Add(value, tags);
        }
    }

    public readonly record struct XApiMetricCounts(
        int CallCount,
        int FailedCallCount,
        int GraphQlErrorCount,
        int CanceledCallCount,
        int MutationCallCount,
        int MutationFailedCallCount);
}
