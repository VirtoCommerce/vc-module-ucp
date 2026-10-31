using GraphQL.MicrosoftDI;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Swashbuckle.AspNetCore.SwaggerGen;
using VirtoCommerce.MCP.Core.Extensions;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.MCP.Core.Services;
using VirtoCommerce.Platform.Core.Modularity;
using VirtoCommerce.Platform.Core.Settings;
using VirtoCommerce.StoreModule.Core.Model;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Diagnostics;
using VirtoCommerce.UCP.Core.Options;
using VirtoCommerce.UCP.Core.Services;
using VirtoCommerce.UCP.Data.Caching;
using VirtoCommerce.UCP.Data.Services;
using VirtoCommerce.UCP.ExperienceApi;
using VirtoCommerce.UCP.Web.Diagnostics;
using VirtoCommerce.UCP.Web.Filters;
using VirtoCommerce.UCP.Web.Mcp;
using VirtoCommerce.UCP.Web.Services;
using VirtoCommerce.UCP.Web.Swagger;
using VirtoCommerce.Xapi.Core.Extensions;
using VirtoCommerce.Xapi.Core.Infrastructure;

namespace VirtoCommerce.UCP.Web;

public class Module : IModule, IHasConfiguration
{
    public ManifestModuleInfo ModuleInfo { get; set; }
    public IConfiguration Configuration { get; set; }

    public void Initialize(IServiceCollection serviceCollection)
    {
        var ucpOptions = Configuration
            .GetSection("UCP")
            .Get<UcpOptions>() ?? new UcpOptions();

        serviceCollection.AddHttpContextAccessor();
        serviceCollection.Configure<UcpOptions>(Configuration.GetSection("UCP"));
        serviceCollection.Configure<MvcOptions>(options =>
        {
            options.Filters.Add<UcpOperationResourceFilter>();
            options.Filters.Add<UcpExceptionFilter>();
        });
        serviceCollection.Configure<SwaggerGenOptions>(options =>
            options.OperationFilter<UcpXApiResponseOperationFilter>());
        serviceCollection.AddScoped<UcpOperationTelemetry>();
        serviceCollection.AddScoped<IUcpOperationTelemetry>(serviceProvider => serviceProvider.GetRequiredService<UcpOperationTelemetry>());
        serviceCollection.AddMcpScope(new McpScope
        {
            Name = ModuleConstants.McpScopeName,
            Title = "Virto Commerce UCP",
            Version = ModuleConstants.UcpVersion,
            Instructions = BuildMcpInstructions(ucpOptions),
            RequireAuthenticatedUser = false,
        });
        serviceCollection.AddMcpToolsFromAssembly(typeof(UcpMcpCommerceTools).Assembly, UcpMcpSerialization.Options);
        serviceCollection.AddTransient<McpChallengeWriter, UcpMcpChallengeWriter>();
        serviceCollection.AddTransient<IMcpPublicOriginResolver, UcpMcpPublicOriginResolver>();

        // UCP's errors and telemetry for the calls of the ucp scope, inside the MCP module's checks and audit.
        serviceCollection.AddMcpCallToolFilter<UcpMcpCallToolFilter>(ModuleConstants.McpScopeName);

        // The gate of the ucp scope switch and the sign-in rule of a closed catalog run ahead of the MCP module's endpoints.
        serviceCollection.AddTransient<IStartupFilter, UcpStartupFilter>();

        // The store of each request, from its host, and the options it gives the UCP services.
        serviceCollection.AddTransient<UcpStoreContextResolver>();
        serviceCollection.AddTransient<IUcpRequestOptions, UcpRequestOptions>();
        serviceCollection.AddTransient<IUcpPublicOriginResolver, UcpPublicOriginResolver>();
        serviceCollection.AddTransient<IUcpProfileService, UcpProfileService>();
        serviceCollection.AddScoped<IUcpBuyerContextAccessor, UcpBuyerContextAccessor>();
        serviceCollection.AddTransient<IUcpCatalogService, UcpCatalogService>();
        serviceCollection.AddTransient<IUcpCartService, UcpCartService>();
        serviceCollection.AddUcpHandoffSessionStore();
        serviceCollection.AddTransient<IUcpCheckoutService, UcpCheckoutService>();
        serviceCollection.AddTransient<IUcpOrderService, UcpOrderService>();
        serviceCollection.AddTransient<IUcpGeographyService, UcpGeographyService>();
        serviceCollection.AddTransient<XApiDocumentExecuters>();
        serviceCollection.AddTransient<IXApiInProcessExecutor, XApiInProcessExecutor>();

        _ = new GraphQLBuilder(serviceCollection, builder =>
        {
            builder.AddSchema(serviceCollection, typeof(XapiAssemblyMarker));
        });

        serviceCollection.AddSingleton<ScopedSchemaFactory<XapiAssemblyMarker>>();
    }

    internal static string BuildMcpInstructions(UcpOptions options)
    {
        return options.StoreManagedAddresses
            ? ModuleConstants.McpInstructions + "\n" + ModuleConstants.StoreManagedAddressesInstruction
            : ModuleConstants.McpInstructions;
    }

    public void PostInitialize(IApplicationBuilder appBuilder)
    {
        var serviceProvider = appBuilder.ApplicationServices;

        // The UCP settings of a store, in the store's settings in the back office.
        var settingsRegistrar = serviceProvider.GetRequiredService<ISettingsRegistrar>();
        settingsRegistrar.RegisterSettings(ModuleConstants.Settings.AllSettings, ModuleInfo.Id);
        settingsRegistrar.RegisterSettingsForType(ModuleConstants.Settings.StoreLevelSettings, nameof(Store));

        LogObsoleteConfiguration(serviceProvider.GetRequiredService<ILogger<Module>>());

        appBuilder.UseScopedSchema<XapiAssemblyMarker>("ucp");
    }

    // UCP keys that the store, its settings or the MCP module's configuration now provide. They still work, so a deployment
    // moves them at its own pace: as the fallback for a request that no store is resolved for, and UCP:PublicOrigin as
    // before.
    private void LogObsoleteConfiguration(ILogger logger)
    {
        foreach (var (key, replacement) in ObsoleteKeys)
        {
            if (!string.IsNullOrWhiteSpace(Configuration[$"UCP:{key}"]))
            {
                logger.LogWarning("UCP:{Key} is obsolete: {Replacement}.", key, replacement);
            }
        }
    }

    internal static readonly (string Key, string Replacement)[] ObsoleteKeys =
    [
        ("DefaultStoreId", "the store is resolved from the request's host; set VirtoCommerce:Stores:DefaultStore for requests to other hosts; it applies only to a request that no store is resolved for"),
        ("DefaultCurrency", "the store's default currency is used; it applies only to a request that no store is resolved for"),
        ("DefaultCultureName", "the store's default language is used; it applies only to a request that no store is resolved for"),
        ("StorefrontOrigin", "the store's URL (SecureUrl, then Url) is used; it applies only to a request that no store is resolved for"),
        ("UcpBaseUrl", "it is built from the store's URL; it applies only to a request that no store is resolved for"),
        ("PublicOrigin", "set MCP:Scopes:ucp:PublicOrigin for a fixed origin, or nothing to use the store's URL; until then it still overrides the store's URL"),
        ("HandoffUrlTemplate", "set the store setting UCP.CheckoutUrlTemplate; it applies to a store that leaves the setting unset"),
        ("HandoffTokenTtlMinutes", "set the store setting UCP.CheckoutLinkLifetimeMinutes; it applies to a store that leaves the setting unset"),
        ("StoreManagedAddresses", "set the store setting UCP.StoreManagedAddresses; it applies to a store that leaves the setting unset"),
    ];

    public void Uninstall()
    {
    }
}
