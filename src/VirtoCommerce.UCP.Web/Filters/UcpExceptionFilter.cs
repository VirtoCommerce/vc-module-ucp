using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using VirtoCommerce.UCP.Core.Attributes;
using VirtoCommerce.UCP.Core.Services;
using VirtoCommerce.UCP.Web.Diagnostics;

namespace VirtoCommerce.UCP.Web.Filters;

public class UcpExceptionFilter : IExceptionFilter
{
    private static readonly EventId RestOperationExceptionEvent = new(2003, "UcpRestOperationException");

    private readonly UcpOperationTelemetry _operationTelemetry;
    private readonly ILogger<UcpExceptionFilter> _logger;

    public UcpExceptionFilter(
        UcpOperationTelemetry operationTelemetry,
        ILogger<UcpExceptionFilter> logger)
    {
        _operationTelemetry = operationTelemetry;
        _logger = logger;
    }

    public void OnException(ExceptionContext context)
    {
        var isUcpOperation = context.ActionDescriptor.EndpointMetadata?.OfType<UcpOperationAttribute>().Any() == true ||
            context.ActionDescriptor is ControllerActionDescriptor actionDescriptor &&
            actionDescriptor.MethodInfo.GetCustomAttribute<UcpOperationAttribute>(inherit: true) != null;
        if (!isUcpOperation)
        {
            return;
        }

        if (context.Exception is XApiResponseException xApiException)
        {
            _operationTelemetry.MarkError(nameof(XApiResponseException), "xapi_graphql_error");
            context.Result = new ContentResult
            {
                Content = xApiException.Result.Json,
                ContentType = "application/json",
                StatusCode = StatusCodes.Status200OK,
            };
            context.ExceptionHandled = true;
            return;
        }

        if (context.Exception is not UcpException exception)
        {
            return;
        }

        if (exception.StatusCode >= StatusCodes.Status500InternalServerError)
        {
            _operationTelemetry.MarkError(nameof(UcpException), exception.Code);
            _logger.LogError(
                RestOperationExceptionEvent,
                exception,
                "event:{EventName} error_code:{UcpErrorCode} trace_id:{TraceId}",
                "ucp.rest.operation.exception",
                exception.Code,
                _operationTelemetry.TraceId);
        }
        else
        {
            _operationTelemetry.MarkRejected(exception.Code);
        }

        context.Result = new ObjectResult(exception.Error)
        {
            StatusCode = exception.StatusCode,
        };
        context.ExceptionHandled = true;
    }
}
