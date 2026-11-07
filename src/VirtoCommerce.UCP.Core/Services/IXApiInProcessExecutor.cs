using System.Threading;
using System.Threading.Tasks;
using VirtoCommerce.UCP.Core.Models;

namespace VirtoCommerce.UCP.Core.Services;

public interface IXApiInProcessExecutor
{
    Task<XApiExecutionResult> Execute(XApiExecutionRequest request, CancellationToken cancellationToken = default);
    Task<XApiExecutionResult> ExecuteCart(XApiExecutionRequest request, CancellationToken cancellationToken = default);
    Task<XApiExecutionResult> ExecuteOrder(XApiExecutionRequest request, CancellationToken cancellationToken = default);

    // The name of the type of an argument of an XCart mutation in the executing schema, for the variable that passes
    // it: an installation that replaces an XCart input type with its own has that type's name there. Null when the
    // schema does not tell; the caller then uses XCart's own name.
    string GetCartMutationArgumentTypeName(string mutationName, string argumentName) => null;
}
