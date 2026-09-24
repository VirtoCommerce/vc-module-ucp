using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using VirtoCommerce.UCP.Core.Services;
using VirtoCommerce.UCP.Data.Caching;
using Xunit;

namespace VirtoCommerce.UCP.Tests;

[Trait("Category", "Unit")]
public class UcpHandoffSessionStoreTests
{
    private const string Key = "vc:ucp:handoff:ABC";

    [Fact]
    public void AddUcpHandoffSessionStore_WithoutPlatformRedis_UsesInMemoryStore()
    {
        using var provider = new ServiceCollection().AddUcpHandoffSessionStore().BuildServiceProvider();

        Assert.IsType<InMemoryUcpHandoffSessionStore>(provider.GetRequiredService<IUcpHandoffSessionStore>());
    }

    [Fact]
    public void AddUcpHandoffSessionStore_WithPlatformRedis_UsesRedisStore()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new FakeRedis().Connection);
        using var provider = services.AddUcpHandoffSessionStore().BuildServiceProvider();

        Assert.IsType<RedisUcpHandoffSessionStore>(provider.GetRequiredService<IUcpHandoffSessionStore>());
    }

    [Fact]
    public void AddUcpHandoffSessionStore_PreservesHostStore()
    {
        var hostStore = new InMemoryUcpHandoffSessionStore();
        var services = new ServiceCollection();
        services.AddSingleton<IUcpHandoffSessionStore>(hostStore);
        services.AddSingleton(new FakeRedis().Connection);
        using var provider = services.AddUcpHandoffSessionStore().BuildServiceProvider();

        Assert.Same(hostStore, provider.GetRequiredService<IUcpHandoffSessionStore>());
    }

    [Fact]
    public async Task InMemoryStore_SetGetRemove()
    {
        using var store = new InMemoryUcpHandoffSessionStore();
        var cancellationToken = TestContext.Current.CancellationToken;

        await store.SetAsync(Key, "payload", DateTimeOffset.UtcNow.AddMinutes(15), cancellationToken);
        Assert.Equal("payload", await store.GetAsync(Key, cancellationToken));

        await store.RemoveAsync(Key, cancellationToken);
        Assert.Null(await store.GetAsync(Key, cancellationToken));
    }

    [Fact]
    public async Task InMemoryStore_IgnoresExpiredSession()
    {
        using var store = new InMemoryUcpHandoffSessionStore();
        var cancellationToken = TestContext.Current.CancellationToken;

        await store.SetAsync(Key, "payload", DateTimeOffset.UtcNow.AddSeconds(-1), cancellationToken);

        Assert.Null(await store.GetAsync(Key, cancellationToken));
    }

    [Fact]
    public async Task RedisStore_WritesSessionWithTtlReadsAndDeletesByKey()
    {
        var redis = new FakeRedis();
        var store = new RedisUcpHandoffSessionStore(redis.Connection);
        var cancellationToken = TestContext.Current.CancellationToken;

        await store.SetAsync(Key, "payload", DateTimeOffset.UtcNow.AddMinutes(15), cancellationToken);
        Assert.Equal("payload", await store.GetAsync(Key, cancellationToken));
        await store.RemoveAsync(Key, cancellationToken);

        var set = Assert.Single(redis.Calls, x => x.Method == nameof(IDatabase.StringSetAsync));
        Assert.Equal(Key, (string)(RedisKey)set.Args[0]);
        Assert.Equal("payload", (string)(RedisValue)set.Args[1]);
        Assert.InRange(GetTtl(Assert.Single(set.Args.OfType<Expiration>())), TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(15));
        Assert.Contains(redis.Calls, x => x.Method == nameof(IDatabase.KeyDeleteAsync) && (string)(RedisKey)x.Args[0] == Key);
        Assert.Null(await store.GetAsync(Key, cancellationToken));
    }

    [Fact]
    public async Task RedisStore_DoesNotWriteExpiredSession()
    {
        var redis = new FakeRedis();
        var store = new RedisUcpHandoffSessionStore(redis.Connection);

        await store.SetAsync(Key, "payload", DateTimeOffset.UtcNow.AddSeconds(-1), TestContext.Current.CancellationToken);

        Assert.DoesNotContain(redis.Calls, x => x.Method == nameof(IDatabase.StringSetAsync));
    }

    // Expiration exposes its relative TTL only as the Redis argument text: "EX <seconds>" or "PX <milliseconds>".
    private static TimeSpan GetTtl(Expiration expiration)
    {
        var parts = expiration.ToString().Split(' ');
        var value = long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);

        return parts[0] switch
        {
            "EX" => TimeSpan.FromSeconds(value),
            "PX" => TimeSpan.FromMilliseconds(value),
            _ => throw new InvalidOperationException($"Expected a relative TTL, got '{expiration}'."),
        };
    }

    /// <summary>
    /// Minimal Redis double: implements only the string commands used by the handoff store.
    /// </summary>
    private sealed class FakeRedis
    {
        private readonly ConcurrentDictionary<string, string> _values = new();

        public FakeRedis()
        {
            var database = DispatchProxy.Create<IDatabase, Proxy>();
            ((Proxy)(object)database).Handler = HandleDatabase;
            Connection = DispatchProxy.Create<IConnectionMultiplexer, Proxy>();
            ((Proxy)(object)Connection).Handler = (method, _) => method.Name == nameof(IConnectionMultiplexer.GetDatabase)
                ? database
                : throw new NotSupportedException(method.Name);
        }

        public IConnectionMultiplexer Connection { get; }

        public ConcurrentQueue<(string Method, object[] Args)> Calls { get; } = new();

        private object HandleDatabase(MethodInfo method, object[] args)
        {
            Calls.Enqueue((method.Name, args));
            var key = (string)(RedisKey)args[0];

            switch (method.Name)
            {
                case nameof(IDatabase.StringSetAsync):
                    _values[key] = (RedisValue)args[1];
                    return Task.FromResult(true);
                case nameof(IDatabase.StringGetAsync):
                    return Task.FromResult(_values.TryGetValue(key, out var value) ? (RedisValue)value : RedisValue.Null);
                case nameof(IDatabase.KeyDeleteAsync):
                    return Task.FromResult(_values.TryRemove(key, out _));
                default:
                    throw new NotSupportedException(method.Name);
            }
        }
    }

    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object[], object> Handler { get; set; }

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            return Handler(targetMethod, args);
        }
    }
}
