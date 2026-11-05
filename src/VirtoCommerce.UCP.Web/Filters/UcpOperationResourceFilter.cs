using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Attributes;
using VirtoCommerce.UCP.Core.Services;
using VirtoCommerce.UCP.Web.Diagnostics;
using VirtoCommerce.UCP.Web.Services;

namespace VirtoCommerce.UCP.Web.Filters;

public sealed class UcpOperationResourceFilter : IAsyncResourceFilter, IAsyncActionFilter, IOrderedFilter
{
    private const int TelemetryFilterOrder = -3000;
    private static readonly object TelemetryStartedKey = new();
    private readonly UcpOperationTelemetry _operationTelemetry;

    public UcpOperationResourceFilter(UcpOperationTelemetry operationTelemetry)
    {
        _operationTelemetry = operationTelemetry;
    }

    public int Order => TelemetryFilterOrder;

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var operation = GetOperation(context);
        if (operation == null)
        {
            await next();
            return;
        }

        var serverActivity = context.HttpContext.Features.Get<IHttpActivityFeature>()?.Activity;
        if (!_operationTelemetry.TryBegin(operation, "rest", serverActivity?.Context))
        {
            await next();
            return;
        }

        context.HttpContext.Items[TelemetryStartedKey] = true;
        if (!string.IsNullOrEmpty(_operationTelemetry.TraceId))
        {
            context.HttpContext.Response.Headers[ModuleConstants.Headers.TraceId] = _operationTelemetry.TraceId;
        }

        try
        {
            var executedContext = await next();
            if (executedContext.Exception != null)
            {
                MarkException(executedContext.Exception);
            }
            else if (context.HttpContext.Response.StatusCode >= StatusCodes.Status500InternalServerError)
            {
                _operationTelemetry.MarkError("HttpResponse", $"http_{context.HttpContext.Response.StatusCode}");
            }
            else if (context.HttpContext.Response.StatusCode >= StatusCodes.Status400BadRequest)
            {
                _operationTelemetry.MarkRejected($"http_{context.HttpContext.Response.StatusCode}");
            }
        }
        catch (Exception exception)
        {
            MarkException(exception);
            throw;
        }
        finally
        {
            _operationTelemetry.Complete();
        }
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (GetOperation(context) != null)
        {
            if (context.HttpContext.Items.ContainsKey(TelemetryStartedKey))
            {
                _operationTelemetry.CaptureRestArguments(context.ActionArguments);
            }

            // The operation runs with the options of the store it names, not of the store of the host it came to.
            var storeContextResolver = context.HttpContext.RequestServices?.GetService<UcpStoreContextResolver>();
            var storeId = FindStoreId(context.ActionArguments.Values);
            if (storeContextResolver != null && storeId != null)
            {
                await storeContextResolver.UseOperationStoreAsync(context.HttpContext, storeId);
            }
        }

        await next();
    }

    // The store_id of a request or query model, or of its context, as the UCP services read it
    internal static string FindStoreId(IEnumerable<object> arguments)
    {
        foreach (var argument in arguments.Where(x => x != null))
        {
            var storeId = GetString(argument, "StoreId") ?? GetString(argument.GetType().GetProperty("Context")?.GetValue(argument), "StoreId");
            if (storeId != null)
            {
                return storeId;
            }
        }

        return null;
    }

    private static string GetString(object model, string propertyName)
    {
        var value = model?.GetType().GetProperty(propertyName)?.GetValue(model) as string;

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string GetOperation(FilterContext context)
    {
        return context.ActionDescriptor.EndpointMetadata
            .OfType<UcpOperationAttribute>()
            .FirstOrDefault()?
            .Name;
    }

    private void MarkException(Exception exception)
    {
        switch (exception)
        {
            case XApiResponseException:
                _operationTelemetry.MarkError(nameof(XApiResponseException), "xapi_graphql_error");
                break;
            case UcpException ucpException when ucpException.StatusCode < StatusCodes.Status500InternalServerError:
                _operationTelemetry.MarkRejected(ucpException.Code);
                break;
            case UcpException ucpException:
                _operationTelemetry.MarkError(ucpException, ucpException.Code);
                break;
            case OperationCanceledException:
                _operationTelemetry.MarkCanceled();
                break;
            default:
                _operationTelemetry.MarkError(exception);
                break;
        }
    }
}
