using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace VirtoCommerce.UCP.Tests;

public class UcpCacheRegistrationTests
{
    [Fact]
    public void Initialize_PreservesHostCacheProvider()
    {
        var hostCache = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var services = new ServiceCollection();
        services.AddSingleton<IDistributedCache>(hostCache);
        var module = new Web.Module { Configuration = new ConfigurationBuilder().Build() };

        module.Initialize(services);

        using var provider = services.BuildServiceProvider();
        Assert.Same(hostCache, provider.GetRequiredService<IDistributedCache>());
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IDistributedCache));
    }

    [Fact]
    public void Initialize_WithoutHostCacheUsesMemoryWithoutRedis()
    {
        var services = new ServiceCollection();
        var module = new Web.Module { Configuration = new ConfigurationBuilder().Build() };

        module.Initialize(services);

        using var provider = services.BuildServiceProvider();
        Assert.IsType<MemoryDistributedCache>(provider.GetRequiredService<IDistributedCache>());
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType.FullName.Contains("StackExchange.Redis"));
    }
}
