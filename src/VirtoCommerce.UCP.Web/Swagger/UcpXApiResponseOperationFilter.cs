using System.Collections.Generic;
using System.Reflection;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;
using VirtoCommerce.UCP.Core.Attributes;
using VirtoCommerce.UCP.Core.Models;

namespace VirtoCommerce.UCP.Web.Swagger;

public sealed class UcpXApiResponseOperationFilter : IOperationFilter
{
    private const string ApplicationJson = "application/json";
    private const string OkStatusCode = "200";
    private const string InternalServerErrorStatusCode = "500";

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var operationAttribute = context.MethodInfo.GetCustomAttribute<UcpOperationAttribute>(inherit: true);
        if (operationAttribute?.IsXApiBacked != true)
        {
            return;
        }

        DocumentSuccessfulOrGraphQlErrorResponse(operation);
        DocumentInvalidXApiResponse(operation, context);
    }

    private static void DocumentSuccessfulOrGraphQlErrorResponse(OpenApiOperation operation)
    {
        if (operation.Responses?.TryGetValue(OkStatusCode, out var response) != true ||
            response.Content?.TryGetValue(ApplicationJson, out var mediaType) != true ||
            mediaType.Schema == null)
        {
            return;
        }

        var successSchema = mediaType.Schema;
        mediaType.Schema = new OpenApiSchema
        {
            OneOf =
            [
                successSchema,
                XApiGraphQlErrorEnvelopeSchema.Create(),
            ],
        };

        response.Content.Clear();
        response.Content[ApplicationJson] = mediaType;
    }

    private static void DocumentInvalidXApiResponse(OpenApiOperation operation, OperationFilterContext context)
    {
        operation.Responses ??= new OpenApiResponses();
        if (operation.Responses.ContainsKey(InternalServerErrorStatusCode))
        {
            return;
        }

        var errorSchema = context.SchemaGenerator.GenerateSchema(typeof(UcpError), context.SchemaRepository);
        operation.Responses[InternalServerErrorStatusCode] = new OpenApiResponse
        {
            Description = "XAPI returned an invalid or non-GraphQL response (`xapi_invalid_response`).",
            Content = new Dictionary<string, OpenApiMediaType>
            {
                [ApplicationJson] = new OpenApiMediaType
                {
                    Schema = errorSchema,
                },
            },
        };
    }
}
