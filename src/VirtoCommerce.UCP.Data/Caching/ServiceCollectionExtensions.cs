using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;
using VirtoCommerce.UCP.Core.Services;

namespace VirtoCommerce.UCP.Data.Caching;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the handoff session store: Redis when the Platform registered an <see cref="IConnectionMultiplexer"/>
    /// (<c>ConnectionStrings:RedisConnectionString</c>), otherwise the single-instance in-memory store.
    /// TryAdd lets a host supply its own store first.
    /// </summary>
    public static IServiceCollection AddUcpHandoffSessionStore(this IServiceCollection services)
    {
        services.TryAddSingleton<IUcpHandoffSessionStore>(serviceProvider =>
        {
            var connection = serviceProvider.GetService<IConnectionMultiplexer>();
            return connection != null
                ? new RedisUcpHandoffSessionStore(connection)
                : new InMemoryUcpHandoffSessionStore();
        });

        return services;
    }
}
