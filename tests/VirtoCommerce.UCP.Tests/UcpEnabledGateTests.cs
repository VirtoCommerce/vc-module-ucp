using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using VirtoCommerce.Platform.Core.Settings;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Web;
using Xunit;

namespace VirtoCommerce.UCP.Tests;

[Trait("Category", "Unit")]
public class UcpEnabledGateTests
{
    private const string _settingName = "UCP.Enabled";

    [Fact]
    public void UcpEnabled_Descriptor_DefaultsToTrue()
    {
        Assert.Equal(_settingName, ModuleConstants.Settings.General.UcpEnabled.Name);
        Assert.Equal(true, ModuleConstants.Settings.General.UcpEnabled.DefaultValue);
    }

    [Fact]
    public async Task InvokeAsync_SettingOff_Answers404WithoutCallingNext()
    {
        var nextCalled = false;
        var middleware = new UcpEnabledMiddleware(
            _ =>
            {
                nextCalled = true;

                return Task.CompletedTask;
            },
            new TestSettingsManager((_settingName, false)));
        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public async Task InvokeAsync_SettingOnOrNeverStored_CallsNext(bool? storedValue)
    {
        var nextCalled = false;
        var middleware = new UcpEnabledMiddleware(
            _ =>
            {
                nextCalled = true;

                return Task.CompletedTask;
            },
            new TestSettingsManager((_settingName, storedValue)));
        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context);

        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("/ucp")]
    [InlineData("/ucp/v1/carts")]
    [InlineData("/UCP/v1/carts")]
    [InlineData("/ucp/mcp")]
    [InlineData("/.well-known/ucp")]
    [InlineData("/.well-known/UCP")]
    [InlineData("/.well-known/oauth-protected-resource/ucp/mcp")]
    [InlineData("/graphql/ucp")]
    [InlineData("/GraphQL/ucp")]
    [InlineData("/ui/graphiql/ucp")]
    public void IsGatedPath_UcpPrefix_ReturnsTrue(string path)
    {
        Assert.True(UcpEnabledGateExtensions.IsGatedPath(path));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/ucpx")]
    [InlineData("/ucpx/v1")]
    [InlineData("/api/platform/modules")]
    [InlineData("/graphql")]
    [InlineData("/graphql/other")]
    [InlineData("/.well-known/openid-configuration")]
    [InlineData("/.well-known/ucpx")]
    [InlineData("/ui/graphiql")]
    public void IsGatedPath_NonUcpPath_ReturnsFalse(string path)
    {
        Assert.False(UcpEnabledGateExtensions.IsGatedPath(path));
    }

    [Fact]
    public async Task UseUcpEnabledGate_SettingOff_MixedCasePrefixAnswers404()
    {
        var settings = new TestSettingsManager((_settingName, false));
        var (pipeline, terminal) = BuildPipeline(settings);

        var context = await Send(pipeline, "GET", "/UCP/v1/carts");

        Assert.False(terminal.Reached);
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/ucpx")]
    [InlineData("GET", "/api/platform/modules")]
    [InlineData("POST", "/graphql")]
    public async Task UseUcpEnabledGate_NonUcpPath_PassesThroughWithoutReadingSetting(string method, string path)
    {
        var settings = new TestSettingsManager((_settingName, false));
        var (pipeline, terminal) = BuildPipeline(settings);

        await Send(pipeline, method, path);

        Assert.True(terminal.Reached);
        Assert.Empty(settings.ReadNames);
    }

    [Theory]
    [InlineData("GET", "/ucp/v1/catalog/search")]
    [InlineData("POST", "/ucp/v1/carts")]
    [InlineData("GET", "/ucp/mcp")]
    [InlineData("DELETE", "/ucp/mcp")]
    [InlineData("POST", "/ucp/mcp")]
    [InlineData("GET", "/.well-known/ucp")]
    [InlineData("GET", "/.well-known/oauth-protected-resource/ucp/mcp")]
    [InlineData("POST", "/graphql/ucp")]
    [InlineData("GET", "/ui/graphiql/ucp")]
    public async Task UseUcpEnabledGate_SettingOff_UcpSurfaceAnswers404(string method, string path)
    {
        var settings = new TestSettingsManager((_settingName, false));
        var (pipeline, terminal) = BuildPipeline(settings);

        var context = await Send(pipeline, method, path, malformedJsonBody: method == "POST");

        Assert.False(terminal.Reached);
        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        Assert.Equal([_settingName], settings.ReadNames);
    }

    [Theory]
    [InlineData("GET", "/ucp/v1/catalog/search")]
    [InlineData("POST", "/ucp/v1/carts")]
    [InlineData("GET", "/ucp/mcp")]
    [InlineData("DELETE", "/ucp/mcp")]
    [InlineData("POST", "/ucp/mcp")]
    [InlineData("GET", "/.well-known/ucp")]
    [InlineData("GET", "/.well-known/oauth-protected-resource/ucp/mcp")]
    [InlineData("POST", "/graphql/ucp")]
    [InlineData("GET", "/ui/graphiql/ucp")]
    public async Task UseUcpEnabledGate_SettingOn_UcpSurfaceReachesTerminal(string method, string path)
    {
        var settings = new TestSettingsManager((_settingName, true));
        var (pipeline, terminal) = BuildPipeline(settings);

        var context = await Send(pipeline, method, path, malformedJsonBody: method == "POST");

        Assert.True(terminal.Reached);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    private static (RequestDelegate Pipeline, Terminal Terminal) BuildPipeline(TestSettingsManager settings)
    {
        var services = new ServiceCollection()
            .AddSingleton<ISettingsManager>(settings)
            .BuildServiceProvider();
        var terminal = new Terminal();
        var app = new ApplicationBuilder(services);
        app.UseUcpEnabledGate();
        app.Run(terminal.Handle);

        return (app.Build(), terminal);
    }

    private static async Task<DefaultHttpContext> Send(RequestDelegate pipeline, string method, string path, bool malformedJsonBody = false)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        if (malformedJsonBody)
        {
            context.Request.ContentType = "application/json";
            context.Request.Body = new MemoryStream("{ not json"u8.ToArray());
        }

        await pipeline(context);

        return context;
    }

    private sealed class Terminal
    {
        public bool Reached { get; private set; }

        public Task Handle(HttpContext context)
        {
            Reached = true;

            return Task.CompletedTask;
        }
    }
}
