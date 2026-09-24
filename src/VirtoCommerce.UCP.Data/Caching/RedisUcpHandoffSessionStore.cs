using System;
using System.Threading;
using System.Threading.Tasks;
using StackExchange.Redis;
using VirtoCommerce.UCP.Core.Services;

namespace VirtoCommerce.UCP.Data.Caching;

/// <summary>
/// Shares handoff sessions between platform instances through the Platform Redis connection.
/// </summary>
public sealed class RedisUcpHandoffSessionStore : IUcpHandoffSessionStore
{
    private readonly IConnectionMultiplexer _connection;

    public RedisUcpHandoffSessionStore(IConnectionMultiplexer connection)
    {
        _connection = connection;
    }

    public Task SetAsync(string key, string payload, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        var ttl = expiresAt - DateTimeOffset.UtcNow;
        if (ttl <= TimeSpan.Zero)
        {
            return Task.CompletedTask;
        }

        return _connection.GetDatabase().StringSetAsync(key, payload, ttl);
    }

    public async Task<string> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        return await _connection.GetDatabase().StringGetAsync(key);
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        return _connection.GetDatabase().KeyDeleteAsync(key);
    }
}
