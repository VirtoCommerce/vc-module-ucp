using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using VirtoCommerce.UCP.Core.Services;

namespace VirtoCommerce.UCP.Data.Caching;

/// <summary>
/// Keeps handoff sessions in the current process. Suitable only for a single instance; sessions are lost on restart.
/// </summary>
public sealed class InMemoryUcpHandoffSessionStore : IUcpHandoffSessionStore, IDisposable
{
    // A private cache keeps sessions out of the platform memory cache and its size limits and eviction.
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public Task SetAsync(string key, string payload, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        if (expiresAt > DateTimeOffset.UtcNow)
        {
            _cache.Set(key, payload, expiresAt);
        }

        return Task.CompletedTask;
    }

    public Task<string> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_cache.TryGetValue(key, out string payload) ? payload : null);
    }

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        _cache.Remove(key);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _cache.Dispose();
    }
}
