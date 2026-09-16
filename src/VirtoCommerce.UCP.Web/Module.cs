using System.Text.Json;
using GraphQL.MicrosoftDI;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Swashbuckle.AspNetCore.SwaggerGen;
using VirtoCommerce.Platform.Core.Modularity;
using VirtoCommerce.Platform.Core.Security;
using VirtoCommerce.Platform.Core.Settings;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Diagnostics;
using VirtoCommerce.UCP.Core.Options;
using VirtoCommerce.UCP.Core.Services;
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
        var observabilityOptions = Configuration
            .GetSection("UCP:Observability")
            .Get<UcpObservabilityOptions>() ?? new UcpObservabilityOptions();

        serviceCollection.AddHttpContextAccessor();
        serviceCollection.AddDistributedMemoryCache();
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
        serviceCollection.AddScoped<UcpMcpCallToolFilter>();
        serviceCollection
            .AddMcpServer(options =>
            {
                options.ServerInfo = new()
                {
                    Name = "Virto Commerce UCP Instructions",
                    Version = ModuleConstants.UcpVersion,
                };
                options.ServerInstructions = ModuleConstants.McpInstructions;
            })
            .WithHttpTransport(options =>
            {
                options.Stateless = true;
            })
            .WithToolsFromAssembly(typeof(UcpMcpCommerceTools).Assembly, CreateMcpToolSerializerOptions())
            .WithMessageFilters(filters =>
            {
                filters.AddIncomingFilter(UcpMcpActivityStatusFilter.Create());
            })
            .WithRequestFilters(filters =>
            {
                filters.AddCallToolFilter(next => (context, cancellationToken) =>
                    context.Services.GetRequiredService<UcpMcpCallToolFilter>().InvokeAsync(next, context, cancellationToken));
            });

        serviceCollection.AddTransient<IUcpProfileService, UcpProfileService>();
        serviceCollection.AddScoped<IUcpBuyerContextAccessor, UcpBuyerContextAccessor>();
        serviceCollection.AddTransient<IUcpCatalogService, UcpCatalogService>();
        serviceCollection.AddTransient<IUcpCartService, UcpCartService>();
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

    private static JsonSerializerOptions CreateMcpToolSerializerOptions()
    {
        return UcpMcpSerialization.Options;
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
