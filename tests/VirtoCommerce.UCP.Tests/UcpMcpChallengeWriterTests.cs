using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using VirtoCommerce.MCP.Core.Extensions;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Services;
using VirtoCommerce.UCP.Web.Mcp;
using Xunit;
using McpScopes = VirtoCommerce.MCP.Core.McpScopes;

namespace VirtoCommerce.UCP.Tests;

[Trait("Category", "Unit")]
public class UcpMcpChallengeWriterTests
{
    private static readonly McpScope _ucpScope = new() { Name = McpScopes.Ucp };

    [Fact]
    public async Task WriteChallengeAsync_IdentityLinkingToolKeepsUcpChallengeAndBody()
    {
        var context = CreateContext();

        await new UcpMcpChallengeWriter().WriteChallengeAsync(context, new McpChallenge
        {
            Scope = _ucpScope,
            Origin = "https://store.example",
            EndpointPath = _ucpScope.Path,
            ToolName = ModuleConstants.McpTools.LinkBuyerIdentity,
            ErrorDescription = "Sign in to use tool 'link_buyer_identity'.",
        });

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        var challenge = context.Response.Headers.WWWAuthenticate.ToString();
        Assert.Contains("realm=\"https://store.example\"", challenge);
        Assert.Contains("resource_metadata=\"https://store.example/.well-known/oauth-protected-resource/ucp/mcp\"", challenge);
        Assert.Contains("scope=\"openid profile offline_access\"", challenge);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());

        var message = Assert.Single(ReadBody(context).GetProperty("messages").EnumerateArray());
        Assert.Equal("error", message.GetProperty("type").GetString());
        Assert.Equal(ModuleConstants.ErrorCodes.IdentityRequired, message.GetProperty("code").GetString());
        Assert.Equal("Platform OAuth user identity is required to link the buyer account.", message.GetProperty("content").GetString());
        Assert.Equal("requires_buyer_review", message.GetProperty("severity").GetString());
    }

    [Fact]
    public async Task WriteChallengeAsync_InvalidTokenKeepsTheReasonInUcpBody()
    {
        var context = CreateContext();

        await new UcpMcpChallengeWriter().WriteChallengeAsync(context, new McpChallenge
        {
            Scope = _ucpScope,
            Origin = "https://store.example",
            EndpointPath = _ucpScope.Path,
            Error = "invalid_token",
            ErrorDescription = "The OAuth session has ended. Sign in again to continue.",
        });

        Assert.Contains("error=\"invalid_token\"", context.Response.Headers.WWWAuthenticate.ToString());
        var message = Assert.Single(ReadBody(context).GetProperty("messages").EnumerateArray());
        Assert.Equal(ModuleConstants.ErrorCodes.IdentityRequired, message.GetProperty("code").GetString());
        Assert.Equal("The OAuth session has ended. Sign in again to continue.", message.GetProperty("content").GetString());
    }

    [Fact]
    public async Task WriteChallengeAsync_OtherScopeKeepsMcpModuleBody()
    {
        var context = CreateContext();
        var adminScope = new McpScope { Name = McpScopes.Admin };

        await new UcpMcpChallengeWriter().WriteChallengeAsync(context, new McpChallenge
        {
            Scope = adminScope,
            Origin = "https://admin.example",
            EndpointPath = adminScope.Path,
            ErrorDescription = "Sign in to use this MCP server.",
        });

        var body = ReadBody(context);
        Assert.False(body.TryGetProperty("messages", out _));
        Assert.Equal("unauthorized", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task GetOriginAsync_UcpScopeUsesStoreOriginAndOtherScopesTheMcpModuleOrigin()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("backend.example");
        var resolver = new UcpMcpPublicOriginResolver(
            Options.Create(new McpOptions { PublicOrigin = "https://admin.example" }),
            new FixedUcpOriginResolver("https://store.example"));

        Assert.Equal("https://store.example", await resolver.GetOriginAsync(context, _ucpScope));
        Assert.Equal("https://admin.example", await resolver.GetOriginAsync(context, new McpScope { Name = McpScopes.Admin }));
    }

    [Theory]
    [InlineData(McpScopes.Ucp, true)]
    [InlineData(McpScopes.Admin, false)]
    [InlineData(null, false)]
    public void IsUcpScope_LimitsTheUcpCallFilterToTheUcpScope(string scopeName, bool expected)
    {
        var context = new DefaultHttpContext();
        if (scopeName != null)
        {
            context.SetMcpScope(new McpScope { Name = scopeName });
        }

        using var services = new ServiceCollection()
            .AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = context })
            .BuildServiceProvider();

        Assert.Equal(expected, VirtoCommerce.UCP.Web.Module.IsUcpScope(services));
    }

    private static DefaultHttpContext CreateContext()
    {
        return new DefaultHttpContext { Response = { Body = new MemoryStream() } };
    }

    private static JsonElement ReadBody(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return JsonDocument.Parse(context.Response.Body).RootElement;
    }

    private sealed class FixedUcpOriginResolver(string origin) : IUcpPublicOriginResolver
    {
        public Task<string> GetOriginAsync() => Task.FromResult(origin);
    }
}
