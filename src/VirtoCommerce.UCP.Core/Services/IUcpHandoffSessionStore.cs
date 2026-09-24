using System;
using System.Threading;
using System.Threading.Tasks;

namespace VirtoCommerce.UCP.Core.Services;

/// <summary>
/// Stores short-lived checkout handoff sessions. Sessions must be visible to every platform instance that can restore them.
/// </summary>
public interface IUcpHandoffSessionStore
{
    Task SetAsync(string key, string payload, DateTimeOffset expiresAt, CancellationToken cancellationToken = default);

    Task<string> GetAsync(string key, CancellationToken cancellationToken = default);

    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
}
