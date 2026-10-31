using GraphQL.MicrosoftDI;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Swashbuckle.AspNetCore.SwaggerGen;
using VirtoCommerce.MCP.Core.Extensions;
using VirtoCommerce.MCP.Core.Models;
using VirtoCommerce.MCP.Core.Services;
using VirtoCommerce.Platform.Core.Modularity;
using VirtoCommerce.Platform.Core.Security;
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

        var permissionsRegistrar = serviceProvider.GetRequiredService<IPermissionsRegistrar>();
        permissionsRegistrar.RegisterPermissions(ModuleInfo.Id, "UCP", ModuleConstants.Security.Permissions.AllPermissions);

        appBuilder.UseScopedSchema<XapiAssemblyMarker>("ucp");
    }

    public void Uninstall()
    {
    }
}
