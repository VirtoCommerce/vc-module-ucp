using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Diagnostics;
using VirtoCommerce.UCP.Core.Models;
using VirtoCommerce.UCP.Core.Services;

namespace VirtoCommerce.UCP.Data.Services;

public abstract class UcpServiceBase
{
    // The decimal digits of a currency the platform does not describe (most currencies have two: cents).
    protected const int DefaultDecimalDigits = 2;
    // The most decimal digits a currency is given; no ISO 4217 currency has more than 4.
    private const int MaxDecimalDigits = 8;

    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IUcpBuyerContextAccessor _buyerContextAccessor;

    protected UcpServiceBase(
        IHttpContextAccessor httpContextAccessor,
        IUcpBuyerContextAccessor buyerContextAccessor = null)
    {
        _httpContextAccessor = httpContextAccessor;
        _buyerContextAccessor = buyerContextAccessor ?? new UcpBuyerContextAccessor(httpContextAccessor);
    }

    protected IHttpContextAccessor HttpContextAccessor => _httpContextAccessor;

    protected virtual ClaimsPrincipal BuildBuyerPrincipal(string buyerUserId = null, string organizationId = null)
    {
        return ResolveBuyerContext(
            requestedBuyerIds: [buyerUserId],
            requestedOrganizationIds: [organizationId]).Principal;
    }

    protected virtual string GetBuyerUserId()
    {
        return ResolveBuyerContext().UserId;
    }

    protected virtual string GetBuyerOrganizationId()
    {
        return ResolveBuyerContext().OrganizationId;
    }

    protected virtual UcpBuyerContext ResolveBuyerContext(
        IEnumerable<string> requestedBuyerIds = null,
        IEnumerable<string> requestedOrganizationIds = null,
        bool createAnonymousBuyer = false,
        bool requireBuyer = false,
        bool requireAuthenticatedBuyer = false)
    {
        return _buyerContextAccessor.Resolve(new UcpBuyerContextRequest
        {
            RequestedBuyerIds = requestedBuyerIds,
            RequestedOrganizationIds = requestedOrganizationIds,
            CreateAnonymousBuyer = createAnonymousBuyer,
            RequireBuyer = requireBuyer,
            RequireAuthenticatedBuyer = requireAuthenticatedBuyer,
        });
    }

    protected static ClaimsPrincipal BuildAnonymousBuyerPrincipal(string buyerUserId)
    {
        var identity = new ClaimsIdentity();
        if (!string.IsNullOrWhiteSpace(buyerUserId))
        {
            identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, buyerUserId));
            identity.AddClaim(new Claim("sub", buyerUserId));
            identity.AddClaim(new Claim("user_id", buyerUserId));
        }

        return new ClaimsPrincipal(identity);
    }

    protected virtual string GetHeader(string name)
    {
        var headers = _httpContextAccessor.HttpContext?.Request.Headers;
        return headers != null && headers.TryGetValue(name, out var values) ? values.FirstOrDefault() : null;
    }

    protected virtual string GetCorrelationId()
    {
        return FirstNotEmpty(GetHeader(ModuleConstants.Headers.CorrelationId), _httpContextAccessor.HttpContext?.TraceIdentifier);
    }

    /// <summary>
    /// Creates a UCP exception without an inner exception. This overload is retained for compatibility.
    /// Override the four-parameter overload to customize all exception creation performed by this base class.
    /// </summary>
    protected virtual UcpException CreateException(string code, string message, int statusCode = StatusCodes.Status400BadRequest)
    {
        return CreateException(code, message, statusCode, null);
    }

    /// <summary>
    /// Creates a UCP exception. Override this overload to customize all exception creation performed by this base class.
    /// </summary>
    protected virtual UcpException CreateException(string code, string message, int statusCode, Exception innerException)
    {
        var exception = new UcpException(code, message, statusCode, innerException);
        exception.Error.CorrelationId = GetCorrelationId();

        return exception;
    }

    protected virtual UcpResponseMetadata CreateMetadata(string status, string capability)
    {
        return new UcpResponseMetadata
        {
            Version = ModuleConstants.UcpVersion,
            Status = status,
            CorrelationId = GetCorrelationId(),
            Capabilities =
            {
                [capability] =
                [
                    new UcpCapabilityVersion { Version = ModuleConstants.UcpVersion },
                ],
            },
        };
    }

    protected virtual JsonDocument ParseGraphQlResult(XApiExecutionResult result, string source)
    {
        return ParseGraphQlResult(result, source, null);
    }

    protected virtual JsonDocument ParseGraphQlResult(
        XApiExecutionResult result,
        string source,
        Func<JsonElement, bool> canTolerateError)
    {
        if (result == null || string.IsNullOrWhiteSpace(result.Json))
        {
            throw CreateException(ModuleConstants.ErrorCodes.XApiInvalidResponse, $"{source} returned an empty response.", StatusCodes.Status500InternalServerError);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(result.Json);
        }
        catch (JsonException exception)
        {
            throw CreateException(
                ModuleConstants.ErrorCodes.XApiInvalidResponse,
                $"{source} returned invalid JSON.",
                StatusCodes.Status500InternalServerError,
                exception);
        }

        try
        {
            ValidateGraphQlResult(document, result, source, canTolerateError);
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private void ValidateGraphQlResult(
        JsonDocument document,
        XApiExecutionResult result,
        string source,
        Func<JsonElement, bool> canTolerateError)
    {
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw CreateException(ModuleConstants.ErrorCodes.XApiInvalidResponse, $"{source} returned a non-object GraphQL response.", StatusCodes.Status500InternalServerError);
        }

        var errorCount = GetGraphQlErrorCount(root, source, out var errors);
        if (HasBlockingErrors(errors, errorCount, canTolerateError))
        {
            throw new XApiResponseException(source, result, errorCount);
        }

        if (!result.Succeeded && errorCount == 0)
        {
            throw CreateException(ModuleConstants.ErrorCodes.XApiInvalidResponse, $"{source} failed without a GraphQL error response.", StatusCodes.Status500InternalServerError);
        }

        EnsureObjectData(root, source);
        MarkDegradedForRecoverableErrors(errorCount);
    }

    private int GetGraphQlErrorCount(JsonElement root, string source, out JsonElement errors)
    {
        if (!root.TryGetProperty("errors", out errors))
        {
            return 0;
        }

        if (errors.ValueKind != JsonValueKind.Array)
        {
            throw CreateException(ModuleConstants.ErrorCodes.XApiInvalidResponse, $"{source} returned an invalid GraphQL errors field.", StatusCodes.Status500InternalServerError);
        }

        return errors.GetArrayLength();
    }

    private static bool HasBlockingErrors(
        JsonElement errors,
        int errorCount,
        Func<JsonElement, bool> canTolerateError)
    {
        return errorCount > 0 &&
            (canTolerateError == null || errors.EnumerateArray().Any(error => !canTolerateError(error)));
    }

    private void EnsureObjectData(JsonElement root, string source)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            throw CreateException(ModuleConstants.ErrorCodes.XApiInvalidResponse, $"{source} returned a GraphQL response without object data.", StatusCodes.Status500InternalServerError);
        }
    }

    private void MarkDegradedForRecoverableErrors(int errorCount)
    {
        if (errorCount == 0)
        {
            return;
        }

        HttpContextAccessor.HttpContext?.RequestServices?
            .GetService<IUcpOperationTelemetry>()?
            .MarkDegraded(nameof(XApiResponseException), "xapi_recoverable_graphql_error");
    }

    protected static string FirstNotEmpty(params string[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    protected static string ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.ToString()
            : null;
    }

    protected static bool ReadBoolean(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.True;
    }

    protected static decimal ReadDecimal(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.TryGetDecimal(out var result)
            ? result
            : 0;
    }

    protected static int ReadInt(JsonElement element, string propertyName, int defaultValue = 0)
    {
        return element.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var result)
            ? result
            : defaultValue;
    }

    protected static long ToMinorUnits(decimal amount)
    {
        return ToMinorUnits(amount, DefaultDecimalDigits);
    }

    // The amount in the currency's minor units: 21.00 USD (2 digits) is 2100, 1500 JPY (0 digits) is 1500, 1.500 KWD (3 digits) is 1500.
    protected static long ToMinorUnits(decimal amount, int decimalDigits)
    {
        return Convert.ToInt64(decimal.Round(amount * GetMinorUnitScale(decimalDigits), 0, MidpointRounding.AwayFromZero));
    }

    protected static decimal FromMinorUnits(long minorUnits, int decimalDigits)
    {
        return minorUnits / GetMinorUnitScale(decimalDigits);
    }

    private static decimal GetMinorUnitScale(int decimalDigits)
    {
        var scale = 1m;
        for (var i = 0; i < Math.Clamp(decimalDigits, 0, MaxDecimalDigits); i++)
        {
            scale *= 10m;
        }

        return scale;
    }
}
