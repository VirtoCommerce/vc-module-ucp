using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VirtoCommerce.MCP.Core.Extensions;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.MCP.Core.Services;
using VirtoCommerce.StoreModule.Core.Model;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Options;
using VirtoCommerce.UCP.Core.Services;
using VirtoCommerce.UCP.Web;
using VirtoCommerce.UCP.Web.Mcp;
using VirtoCommerce.UCP.Web.Services;
using VirtoCommerce.Xapi.Core.Models;
using Xunit;
using UcpModule = VirtoCommerce.UCP.Web.Module;

namespace VirtoCommerce.UCP.Tests;

// What the module wires at startup: the store of a request ahead of the UCP endpoints, the warnings for obsolete keys,
// and the MCP module's fixed origin of the ucp scope.
[Trait("Category", "Unit")]
public class UcpModuleTests
{
    private static readonly Store _store = new() { Id = "fashion", SecureUrl = "https://fashion.example", StoreState = StoreState.Open };

    [Theory]
    [InlineData("/ucp/v1/catalog/search", true)]
    [InlineData("/.well-known/ucp", true)]
    [InlineData("/api/platform/settings", false)]
    public async Task UcpRequests_ResolveTheStoreOfUcpRequests_AheadOfTheEndpoints(string path, bool resolved)
    {
        await using var services = new ServiceCollection()
            .AddSingleton<IMcpScopeRegistry>(new StubMcpScopeRegistry())
            .AddSingleton<UcpStoreContextResolver>(new FixedStoreContextResolver(_store))
            .AddSingleton(Options.Create(new UcpOptions()))
            .AddLogging()
            .BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        Store seenByEndpoint = null;
        app.UseUcpRequests();
        app.Run(context =>
        {
            seenByEndpoint = UcpRequestOptions.GetStore(context);
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Host = new HostString("fashion.example");
        context.Request.Path = path;

        await app.Build()(context);

        Assert.Equal(resolved ? _store : null, seenByEndpoint);
    }

    [Fact]
    public void LogObsoleteConfiguration_WarnsForEachObsoleteKeyThatIsSet()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["UCP:DefaultStoreId"] = "B2B-store",
                ["UCP:PublicOrigin"] = "https://store.example",
                ["UCP:AnonymousCatalog"] = "false",
            })
            .Build();
        var logger = new CapturingLogger();

        UcpModule.LogObsoleteConfiguration(configuration, logger);

        Assert.Equal(2, logger.Warnings.Count);
        Assert.Contains(logger.Warnings, x => x.StartsWith("UCP:DefaultStoreId is obsolete", StringComparison.Ordinal));
        Assert.Contains(logger.Warnings, x => x.StartsWith("UCP:PublicOrigin is obsolete", StringComparison.Ordinal) && x.Contains("MCP:Scopes:ucp:PublicOrigin"));
    }

    [Fact]
    public void Options_TakeTheFixedOriginOfTheUcpScope_FromTheMcpModule()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string> { ["UCP:PublicOrigin"] = "https://obsolete.example" })
            .Build();
        var services = new ServiceCollection();
        new UcpModule { Configuration = configuration }.Initialize(services);
        services.Configure<McpOptions>(options => options.Scopes[ModuleConstants.McpScopeName] = new McpScopeOptions { PublicOrigin = "https://store.example" });

        using var provider = services.BuildServiceProvider();

        Assert.Equal("https://store.example", provider.GetRequiredService<IOptions<UcpOptions>>().Value.PublicOrigin);
    }

    [Fact]
    public void Initialize_DeclaresTheChallengeWriterAndOriginOfTheUcpScope_And_LeavesOtherScopesTheMcpModulesOnes()
    {
        var services = new ServiceCollection();
        new UcpModule { Configuration = new ConfigurationBuilder().Build() }.Initialize(services);
        services.AddTransient<McpChallengeWriter>();
        services.AddTransient<IMcpPublicOriginResolver, McpPublicOriginResolver>();
        services.AddSingleton<IUcpPublicOriginResolver>(new FixedUcpOriginResolver());

        using var provider = services.BuildServiceProvider();
        var ucp = new McpScope { Name = ModuleConstants.McpScopeName };
        var admin = new McpScope { Name = "admin" };

        Assert.IsType<UcpMcpChallengeWriter>(provider.GetMcpScopeService<McpChallengeWriter>(ucp));
        Assert.IsType<UcpMcpPublicOriginResolver>(provider.GetMcpScopeService<IMcpPublicOriginResolver>(ucp));
        Assert.IsType<McpChallengeWriter>(provider.GetMcpScopeService<McpChallengeWriter>(admin));
        Assert.IsType<McpPublicOriginResolver>(provider.GetMcpScopeService<IMcpPublicOriginResolver>(admin));
    }

    [Fact]
    public void McpInstructions_LeaveTheAddressRulesToTheStore_When_ItAssignsAddressesItself()
    {
        var instructions = UcpModule.BuildMcpInstructions(new UcpOptions());

        Assert.Contains("store_managed_addresses true", instructions);
    }

    private sealed class FixedStoreContextResolver(Store store)
        : UcpStoreContextResolver(null, null, null, Options.Create(new StoresOptions()), Options.Create(new UcpOptions()))
    {
        public override Task<Store> ResolveStoreAsync(HttpContext context)
        {
            return Task.FromResult(store);
        }
    }

    private sealed class FixedUcpOriginResolver : IUcpPublicOriginResolver
    {
        public Task<string> GetOriginAsync() => Task.FromResult("https://store.example");
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
