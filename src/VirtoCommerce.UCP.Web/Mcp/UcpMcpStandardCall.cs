using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Models;
using VirtoCommerce.UCP.Core.Services;

namespace VirtoCommerce.UCP.Web.Mcp;

/// <summary>One standard UCP tool call: the store profile, the mapper, and the error shape of the standard binding.</summary>
internal sealed class UcpMcpStandardCall
{
    private readonly IUcpProfileService _profileService;

    public UcpMcpStandardCall(IUcpProfileService profileService, UcpStandardMapper mapper, CancellationToken cancellationToken)
    {
        _profileService = profileService;
        Mapper = mapper ?? new UcpStandardMapper();
        CancellationToken = cancellationToken;
    }

    public UcpStandardMapper Mapper { get; }

    public CancellationToken CancellationToken { get; }

    public Task<UcpProfile> GetProfile()
    {
        return _profileService?.GetProfile(CancellationToken) ?? Task.FromResult<UcpProfile>(null);
    }

    /// <summary>
    /// Runs the call and returns its standard payload as the tool result. A UCP error, or an XAPI error response, becomes
    /// a <see cref="UcpMcpStandardException"/> with the standard error_response, which UcpMcpCallToolFilter returns.
    /// </summary>
    public async Task<object> Execute(string capability, Func<Task<object>> action)
    {
        try
        {
            return Mapper.CreateResult(await action());
        }
        catch (UcpMcpStandardException)
        {
            throw;
        }
        catch (UcpException exception)
        {
            throw new UcpMcpStandardException(exception, Mapper.CreateErrorResult(exception, capability));
        }
        catch (XApiResponseException exception)
        {
            var error = new UcpException(
                ModuleConstants.ErrorCodes.XApiExecutionFailed,
                exception.Message,
                StatusCodes.Status422UnprocessableEntity,
                exception);
            throw new UcpMcpStandardException(error, Mapper.CreateErrorResult(error, capability));
        }
    }
}
