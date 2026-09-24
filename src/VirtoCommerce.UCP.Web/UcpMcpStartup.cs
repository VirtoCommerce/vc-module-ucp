using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Net.Http.Headers;
using ModelContextProtocol.AspNetCore;
using VirtoCommerce.Platform.Core.Modularity;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Web.Mcp;

namespace VirtoCommerce.UCP.Web;

public class UcpMcpStartup : IPlatformStartup
{
    public void ConfigureAppConfiguration(IConfigurationBuilder builder, IHostEnvironment env)
    {
    }

    public void ConfigureHostServices(IServiceCollection services, IConfiguration config)
    {
    }

    public void ConfigureServices(IServiceCollection services, IConfiguration config)
    {
    }

    public void Configure(IApplicationBuilder app, IConfiguration config)
    {
        app.MapWhen(
            context => context.Request.Path.StartsWithSegments(ModuleConstants.Endpoints.Mcp),
            branch =>
            {
                branch.Use(async (context, next) =>
                {
                    await HandleMcpProbeCompatibility(context, next);
                });
                branch.UseRouting();
                branch.UseAuthentication();
                branch.UseMiddleware<UcpMcpBuyerAuthenticationMiddleware>();
                branch.UseAuthorization();
                branch.UseEndpoints(endpoints =>
                {
                    endpoints.MapMcp(ModuleConstants.Endpoints.Mcp);
                });
            });
    }

    private static async Task HandleMcpProbeCompatibility(HttpContext context, Func<Task> next)
    {
        var jsonOnlyRequest = EnsureCompatibleMcpAcceptHeader(context.Request);
        if (!jsonOnlyRequest || !await IsToolsListRequest(context.Request))
        {
            await next();
            return;
        }

        await RewriteEventStreamResponseAsJson(context, next);
    }

    private static bool EnsureCompatibleMcpAcceptHeader(HttpRequest request)
    {
        if (!HttpMethods.IsPost(request.Method) || !request.HasJsonContentType())
        {
            return false;
        }

        var acceptedMediaTypes = request.GetTypedHeaders().Accept;
        var acceptsJson = acceptedMediaTypes?.Any(x => MediaTypeEquals(x.MediaType.Value, "application/json")) == true;
        var acceptsEventStream = acceptedMediaTypes?.Any(x => MediaTypeEquals(x.MediaType.Value, "text/event-stream")) == true;

        if (acceptsJson && !acceptsEventStream)
        {
            // Some read-only MCP probes request JSON only, while the SDK requires both media types for POST responses.
            request.Headers.Append(HeaderNames.Accept, "text/event-stream");
            return true;
        }

        return false;
    }

    private static async Task<bool> IsToolsListRequest(HttpRequest request)
    {
        request.EnableBuffering();

        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: request.HttpContext.RequestAborted);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("method", out var method) &&
                method.ValueKind == JsonValueKind.String &&
                method.ValueEquals("tools/list");
        }
        catch (JsonException)
        {
            return false;
        }
        finally
        {
            request.Body.Position = 0;
        }
    }

    private static async Task RewriteEventStreamResponseAsJson(HttpContext context, Func<Task> next)
    {
        var originalBody = context.Response.Body;
        await using var responseBuffer = new MemoryStream();
        var rewriteResponse = false;

        context.Response.Body = responseBuffer;
        context.Response.OnStarting(() =>
        {
            rewriteResponse = IsEventStream(context.Response.ContentType);
            if (rewriteResponse)
            {
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.ContentLength = null;
            }

            return Task.CompletedTask;
        });

        try
        {
            await next();

            if (!context.Response.HasStarted && IsEventStream(context.Response.ContentType))
            {
                rewriteResponse = true;
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.ContentLength = null;
            }

            responseBuffer.Position = 0;
            context.Response.Body = originalBody;

            if (rewriteResponse)
            {
                using var reader = new StreamReader(responseBuffer, Encoding.UTF8);
                var eventStream = await reader.ReadToEndAsync(context.RequestAborted);
                var json = ExtractEventStreamData(eventStream);
                await context.Response.WriteAsync(json, context.RequestAborted);
            }
            else
            {
                await responseBuffer.CopyToAsync(originalBody, context.RequestAborted);
            }
        }
        finally
        {
            context.Response.Body = originalBody;
        }
    }

    private static string ExtractEventStreamData(string eventStream)
    {
        return eventStream
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("data:", StringComparison.Ordinal))
            .Select(line => line["data:".Length..].TrimStart())
            .Single();
    }

    private static bool IsEventStream(string contentType)
    {
        return contentType?.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool MediaTypeEquals(string value, string expected)
    {
        return string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
    }
}
