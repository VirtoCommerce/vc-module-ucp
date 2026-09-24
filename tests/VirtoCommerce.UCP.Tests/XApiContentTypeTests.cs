using System;
using System.Threading.Tasks;
using GraphQL;
using GraphQL.SystemTextJson;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using VirtoCommerce.UCP.Core.Models;
using VirtoCommerce.UCP.Web.Diagnostics;
using VirtoCommerce.UCP.Web.Services;
using Xunit;
using CartSchema = VirtoCommerce.Xapi.Core.Infrastructure.ScopedSchemaFactory<VirtoCommerce.XCart.Data.DataAssemblyMarker>;

namespace VirtoCommerce.UCP.Tests;

public class XApiContentTypeTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("application/json; charset=utf-8", false)]
    [InlineData("application/json; charset=utf-8", true)]
    public async Task ExecuteCart_UsesInternalJsonMediaTypeAndRestoresOuterRequest(string contentType, bool fail)
    {
        var context = new DefaultHttpContext();
        context.Request.ContentType = contentType;
        var executor = new XApiInProcessExecutor(
            new XApiDocumentExecuters(null, new CheckingExecuter(context, fail), null),
            new GraphQLSerializer(), null,
            new HttpContextAccessor { HttpContext = context },
            new UcpOperationTelemetry(NullLogger<UcpOperationTelemetry>.Instance),
            NullLogger<XApiInProcessExecutor>.Instance);

        if (fail)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteCart(new XApiExecutionRequest { Query = "{ cart { id } }" }, TestContext.Current.CancellationToken));
        }
        else
        {
            Assert.True((await executor.ExecuteCart(new XApiExecutionRequest { Query = "{ cart { id } }" }, TestContext.Current.CancellationToken)).Succeeded);
        }

        Assert.Equal(contentType, context.Request.ContentType);
    }

    private sealed class CheckingExecuter(HttpContext context, bool fail) : IDocumentExecuter<CartSchema>
    {
        public Task<ExecutionResult> ExecuteAsync(ExecutionOptions options)
        {
            Assert.Equal("application/json", context.Request.ContentType);
            return fail
                ? Task.FromException<ExecutionResult>(new InvalidOperationException("resolver failed"))
                : Task.FromResult(new ExecutionResult());
        }
    }
}
