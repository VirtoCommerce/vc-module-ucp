using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Models;
using VirtoCommerce.UCP.Core.Services;
using VirtoCommerce.UCP.Web.Diagnostics;
using VirtoCommerce.UCP.Web.Filters;
using VirtoCommerce.UCP.Web.Mcp;
using Xunit;

namespace VirtoCommerce.UCP.Tests;

[Trait("Category", "Unit")]
public class UcpInventoryErrorContractTests
{
    internal static async Task<CallToolResult> InvokeTool(string name, Func<Task<object>> action)
    {
        var context = (RequestContext<CallToolRequestParams>)RuntimeHelpers.GetUninitializedObject(typeof(RequestContext<CallToolRequestParams>));
        context.Params = new CallToolRequestParams { Name = name };
        var filter = new UcpMcpCallToolFilter(new UcpOperationTelemetry(NullLogger<UcpOperationTelemetry>.Instance), NullLogger<UcpMcpCallToolFilter>.Instance);
        return await filter.InvokeAsync(async (_, _) => new CallToolResult
        {
            StructuredContent = JsonSerializer.SerializeToElement(await action(), UcpMcpSerialization.Options),
        }, context, TestContext.Current.CancellationToken);
    }

    internal sealed class ProfileService : IUcpProfileService
    {
        public Task<UcpProfile> GetProfile(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new UcpProfile());
        }
    }

    [Fact]
    public void Mcp_PreservesInventoryDetailsInStructuredAndTextContent()
    {
        var result = UcpMcpErrorResultFactory.FromUcp(CreateException());
        Assert.True(result.IsError);
        var content = result.StructuredContent.Value;
        Assert.Equal("insufficient_stock", content.GetProperty("code").GetString());
        Assert.Equal(5, content.GetProperty("details").GetProperty("requested_quantity").GetInt64());
        Assert.Equal(2, content.GetProperty("details").GetProperty("available_quantity").GetInt64());
        Assert.Equal("product-1", content.GetProperty("details").GetProperty("product_id").GetString());
        Assert.Equal("line-1", content.GetProperty("details").GetProperty("line_item_id").GetString());
        Assert.True(content.GetProperty("details").GetProperty("retryable").GetBoolean());
        Assert.Equal(content.GetRawText(), Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    [Fact]
    public void Rest_ReturnsConflictWithSameInventoryDetails()
    {
        var exception = CreateException();
        var filter = new UcpExceptionFilter(new UcpOperationTelemetry(NullLogger<UcpOperationTelemetry>.Instance), NullLogger<UcpExceptionFilter>.Instance);
        var descriptor = new ActionDescriptor
        {
            EndpointMetadata = [new UcpOperationAttribute(ModuleConstants.Operations.CreateCart)],
        };
        var context = new ExceptionContext(new ActionContext(new DefaultHttpContext(), new RouteData(), descriptor), [])
        {
            Exception = exception,
        };
        filter.OnException(context);
        Assert.True(context.ExceptionHandled);
        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(409, result.StatusCode);
        Assert.Same(exception.Error, result.Value);
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(result.Value);
        Assert.Contains("\"requested_quantity\":5", json);
        Assert.Contains("\"available_quantity\":2", json);
    }

    private static UcpException CreateException()
    {
        var exception = new UcpException("insufficient_stock", "Only 2 units are available.", 409);
        exception.Error.Details = new Dictionary<string, object>
        {
            ["product_id"] = "product-1",
            ["line_item_id"] = "line-1",
            ["requested_quantity"] = 5L,
            ["available_quantity"] = 2L,
            ["retryable"] = true,
        };
        return exception;
    }
}
