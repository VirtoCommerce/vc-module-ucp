using System;
using GraphQL.MicrosoftDI;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Swashbuckle.AspNetCore.SwaggerGen;
using VirtoCommerce.MCP.Core.Extensions;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.MCP.Core.Services;
using VirtoCommerce.Platform.Core.Modularity;
using VirtoCommerce.Platform.Core.Security;
using VirtoCommerce.Platform.Core.Settings;
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
using McpScopes = VirtoCommerce.MCP.Core.McpScopes;

namespace VirtoCommerce.UCP.Web;

public class Module : IModule, IHasConfiguration
{
    public ManifestModuleInfo ModuleInfo { get; set; }
    public IConfiguration Configuration { get; set; }

    public void Initialize(IServiceCollection serviceCollection)
    {
        var observabilityOptions = Configuration
            .GetSection("UCP:Observability")
            .Get<UcpObservabilityOptions>() ?? new UcpObservabilityOptions();

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
        if (observabilityOptions.EnableApplicationInsightsCompatibilityBridge)
        {
            serviceCollection.AddHostedService<UcpApplicationInsightsActivityBridge>();
        }
        serviceCollection.AddMcpScope(new McpScope
        {
            Name = McpScopes.Ucp,
            Title = "Virto Commerce UCP",
            Version = ModuleConstants.UcpVersion,
            Instructions = ModuleConstants.McpInstructions,
            RequireAuthenticatedUser = false,
        });
        serviceCollection.AddMcpToolsFromAssembly(typeof(UcpMcpCommerceTools).Assembly, UcpMcpSerialization.Options);
        serviceCollection.AddTransient<McpChallengeWriter, UcpMcpChallengeWriter>();
        serviceCollection.AddTransient<IMcpPublicOriginResolver, UcpMcpPublicOriginResolver>();

        // Added after the MCP module's call filter, so it runs inside it: the MCP module checks the arguments and audits the call.
        // It applies to the ucp scope only.
        serviceCollection.AddScoped<UcpMcpCallToolFilter>();
        serviceCollection.AddMcpServer()
            .WithMessageFilters(filters =>
            {
                filters.AddIncomingFilter(UcpMcpActivityStatusFilter.Create());
            })
            .WithRequestFilters(filters =>
            {
                filters.AddCallToolFilter(next => (context, cancellationToken) => IsUcpScope(context.Services)
                    ? context.Services.GetRequiredService<UcpMcpCallToolFilter>().InvokeAsync(next, context, cancellationToken)
                    : next(context, cancellationToken));
            });

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

    public void PostInitialize(IApplicationBuilder appBuilder)
    {
        var serviceProvider = appBuilder.ApplicationServices;

        var observabilityOptions = Configuration
            .GetSection("UCP:Observability")
            .Get<UcpObservabilityOptions>() ?? new UcpObservabilityOptions();
        if (observabilityOptions.EnableApplicationInsightsCompatibilityBridge && HasConfiguredOtelExporter(Configuration))
        {
            serviceProvider.GetRequiredService<ILogger<Module>>().LogWarning(
                "The UCP Application Insights compatibility bridge and an OpenTelemetry exporter are both configured. " +
                "Disable UCP:Observability:EnableApplicationInsightsCompatibilityBridge when both pipelines export to the same Application Insights resource to avoid duplicate dependencies.");
        }

        var settingsRegistrar = serviceProvider.GetRequiredService<ISettingsRegistrar>();
        settingsRegistrar.RegisterSettings(ModuleConstants.Settings.AllSettings, ModuleInfo.Id);

        var permissionsRegistrar = serviceProvider.GetRequiredService<IPermissionsRegistrar>();
        permissionsRegistrar.RegisterPermissions(ModuleInfo.Id, "UCP", ModuleConstants.Security.Permissions.AllPermissions);

        appBuilder.UseScopedSchema<XapiAssemblyMarker>("ucp");
    }

    internal static bool IsUcpScope(IServiceProvider services)
    {
        var scope = services?.GetService<IHttpContextAccessor>()?.HttpContext.GetMcpScope();

        return string.Equals(scope?.Name, McpScopes.Ucp, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool HasConfiguredOtelExporter(IConfiguration configuration)
    {
        return !string.IsNullOrWhiteSpace(configuration["OpenTelemetry:Endpoint"])
            || !string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_ENDPOINT"])
            || !string.IsNullOrWhiteSpace(configuration["OTEL_EXPORTER_OTLP_TRACES_ENDPOINT"]);
    }

    public void Uninstall()
    {
    }
}
