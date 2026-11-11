using ModelContextProtocol.Protocol;
using VirtoCommerce.UCP.Core.Services;

namespace VirtoCommerce.UCP.Web.Mcp;

/// <summary>
/// A UCP error of a standard UCP tool call, with its result in the standard error_response shape. It keeps the code and
/// status of the original error, so UcpMcpCallToolFilter records and logs it like any other UCP error and returns
/// <see cref="Result"/>.
/// </summary>
public sealed class UcpMcpStandardException : UcpException
{
    public UcpMcpStandardException(UcpException exception, CallToolResult result)
        : base(exception.Code, exception.Message, exception.StatusCode, exception)
    {
        Error = exception.Error;
        Result = result;
    }

    public CallToolResult Result { get; }
}
