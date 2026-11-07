using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;
using VirtoCommerce.UCP.Core.Attributes;
using VirtoCommerce.UCP.Core.Models;
using VirtoCommerce.UCP.Web.Controllers.Api;
using VirtoCommerce.UCP.Web.Swagger;
using Xunit;

namespace VirtoCommerce.UCP.Tests;

[Trait("Category", "Unit")]
public class UcpOpenApiContractTests
{
    private const string ApplicationJson = "application/json";

    [Fact]
    public void UcpOperations_MarkExactlyTheXApiBackedRestActions()
    {
        string[] expectedMarked =
        [
            "UcpCartController.CreateCart",
            "UcpCartController.GetCart",
            "UcpCartController.ListCarts",
            "UcpCartController.UpdateCart",
            "UcpCatalogController.GetProduct",
            "UcpCatalogController.SearchProducts",
            "UcpCheckoutController.CreateCheckout",
            "UcpCheckoutController.HandoffCheckout",
            "UcpCheckoutController.UpdateCheckout",
            "UcpHandoffController.Restore",
        ];
        string[] expectedUnmarked =
        [
            "UcpCheckoutController.GetPaymentHandlers",
            "UcpGeographyController.ListCountries",
            "UcpGeographyController.ListRegions",
            "UcpGeographyController.ResolveCountry",
            "UcpOrderController.TrackOrder",
            "UcpOrderController.TrackOrderByCart",
            "UcpProfileController.GetProfile",
        ];

        var operations = typeof(UcpCatalogController).Assembly
            .GetTypes()
            .Where(type => type.Namespace == typeof(UcpCatalogController).Namespace && typeof(ControllerBase).IsAssignableFrom(type))
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                .Select(method => new
                {
                    Name = $"{type.Name}.{method.Name}",
                    Attribute = method.GetCustomAttribute<UcpOperationAttribute>(inherit: true),
                }))
            .Where(operation => operation.Attribute != null)
            .ToArray();

        var actualMarked = operations
            .Where(operation => operation.Attribute.IsXApiBacked)
            .Select(operation => operation.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var actualUnmarked = operations
            .Where(operation => !operation.Attribute.IsXApiBacked)
            .Select(operation => operation.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expectedMarked, actualMarked);
        Assert.Equal(expectedUnmarked, actualUnmarked);
    }

    [Fact]
    public void Apply_XApiBackedAction_DocumentsTypedOrRawGraphQl200AndInvalidXApi500()
    {
        var typedResponseSchema = new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Description = "Typed UCP response",
        };
        var operation = CreateOperation(typedResponseSchema);
        var schemaGenerator = new StubSchemaGenerator();
        var filter = new UcpXApiResponseOperationFilter();

        filter.Apply(operation, CreateContext<UcpCatalogController>(nameof(UcpCatalogController.SearchProducts), schemaGenerator));

        var okResponse = operation.Responses["200"];
        var onlyContent = Assert.Single(okResponse.Content);
        Assert.Equal(ApplicationJson, onlyContent.Key);

        var combinedSchema = Assert.IsType<OpenApiSchema>(onlyContent.Value.Schema);
        Assert.Equal(2, combinedSchema.OneOf.Count);
        Assert.Same(typedResponseSchema, combinedSchema.OneOf[0]);

        var graphQlEnvelope = Assert.IsType<OpenApiSchema>(combinedSchema.OneOf[1]);
        Assert.Equal(JsonSchemaType.Object, graphQlEnvelope.Type);
        Assert.Contains("errors", graphQlEnvelope.Required);

        var errors = Assert.IsType<OpenApiSchema>(graphQlEnvelope.Properties["errors"]);
        Assert.Equal(JsonSchemaType.Array, errors.Type);
        Assert.Equal(1, errors.MinItems);
        var error = Assert.IsType<OpenApiSchema>(errors.Items);
        Assert.Contains("message", error.Required);

        var invalidResponse = operation.Responses["500"];
        Assert.Contains("xapi_invalid_response", invalidResponse.Description);
        var invalidContent = Assert.Single(invalidResponse.Content);
        Assert.Equal(ApplicationJson, invalidContent.Key);
        Assert.Same(schemaGenerator.UcpErrorSchema, invalidContent.Value.Schema);
        Assert.Equal(typeof(UcpError), Assert.Single(schemaGenerator.GeneratedTypes));
    }

    [Fact]
    public void Apply_NonXApiAction_DoesNotChangeResponses()
    {
        var typedResponseSchema = new OpenApiSchema { Type = JsonSchemaType.Object };
        var operation = CreateOperation(typedResponseSchema);
        var schemaGenerator = new StubSchemaGenerator();
        var filter = new UcpXApiResponseOperationFilter();

        filter.Apply(operation, CreateContext<UcpCheckoutController>(nameof(UcpCheckoutController.GetPaymentHandlers), schemaGenerator));

        Assert.Equal(3, operation.Responses["200"].Content.Count);
        Assert.Same(typedResponseSchema, operation.Responses["200"].Content[ApplicationJson].Schema);
        Assert.False(operation.Responses.ContainsKey("500"));
        Assert.Empty(schemaGenerator.GeneratedTypes);
    }

    [Fact]
    public void Apply_XApiBackedAction_DoesNotOverwriteAnExisting500Response()
    {
        var existing500 = new OpenApiResponse
        {
            Description = "Existing response",
        };
        var operation = CreateOperation(new OpenApiSchema { Type = JsonSchemaType.Object });
        operation.Responses["500"] = existing500;
        var schemaGenerator = new StubSchemaGenerator();
        var filter = new UcpXApiResponseOperationFilter();

        filter.Apply(operation, CreateContext<UcpCartController>(nameof(UcpCartController.GetCart), schemaGenerator));

        Assert.Same(existing500, operation.Responses["500"]);
        Assert.Empty(schemaGenerator.GeneratedTypes);
    }

    private static OpenApiOperation CreateOperation(IOpenApiSchema typedResponseSchema)
    {
        return new OpenApiOperation
        {
            Responses = new OpenApiResponses
            {
                ["200"] = new OpenApiResponse
                {
                    Description = "OK",
                    Content = new Dictionary<string, OpenApiMediaType>
                    {
                        ["text/plain"] = new OpenApiMediaType { Schema = typedResponseSchema },
                        [ApplicationJson] = new OpenApiMediaType { Schema = typedResponseSchema },
                        ["text/json"] = new OpenApiMediaType { Schema = typedResponseSchema },
                    },
                },
            },
        };
    }

    private static OperationFilterContext CreateContext<TController>(string methodName, ISchemaGenerator schemaGenerator)
    {
        var method = typeof(TController).GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(method);

        return new OperationFilterContext(
            new ApiDescription(),
            schemaGenerator,
            new SchemaRepository("VirtoCommerce.UCP"),
            new OpenApiDocument(),
            method);
    }

    private sealed class StubSchemaGenerator : ISchemaGenerator
    {
        public IList<Type> GeneratedTypes { get; } = [];

        public IOpenApiSchema UcpErrorSchema { get; } = new OpenApiSchema
        {
            Type = JsonSchemaType.Object,
            Description = nameof(UcpError),
        };

        public IOpenApiSchema GenerateSchema(
            Type modelType,
            SchemaRepository schemaRepository,
            MemberInfo memberInfo = null,
            ParameterInfo parameterInfo = null,
            ApiParameterRouteInfo routeInfo = null)
        {
            GeneratedTypes.Add(modelType);
            return modelType == typeof(UcpError)
                ? UcpErrorSchema
                : throw new InvalidOperationException($"Unexpected schema type {modelType}.");
        }
    }
}
