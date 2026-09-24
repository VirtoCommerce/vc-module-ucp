using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using VirtoCommerce.Platform.Core.Common;
using VirtoCommerce.UCP.Core;
using VirtoCommerce.UCP.Core.Models;
using VirtoCommerce.UCP.Core.Services;
using VirtoCommerce.UCP.Data.Services;
using Xunit;

namespace VirtoCommerce.UCP.Tests;

[Trait("Category", "Unit")]
public class UcpGeographyServiceTests
{
    [Fact]
    public async Task ResolveCountry_UsesPlatformCountryServiceForIso2()
    {
        var service = CreateService();

        var response = await service.ResolveCountry("KZ", TestContext.Current.CancellationToken);

        Assert.Equal("success", response.Ucp.Status);
        Assert.Contains(ModuleConstants.Capabilities.Geography, response.Ucp.Capabilities.Keys);
        Assert.Equal("KAZ", response.Country.Id);
        Assert.Equal("Kazakhstan", response.Country.Name);
        Assert.Equal(1, response.Country.RegionCount);
    }

    [Fact]
    public async Task ListRegions_ReturnsPlatformRegionsForResolvedCountry()
    {
        var service = CreateService();

        var response = await service.ListRegions("KZ", TestContext.Current.CancellationToken);

        Assert.Equal("KAZ", response.Country.Id);
        Assert.Contains(response.Regions, x => x.Id == "ALA" && x.Name == "Алматы");
    }

    [Fact]
    public async Task ListCountries_FiltersByPlatformCountryName()
    {
        var service = CreateService();

        var response = await service.ListCountries(new UcpCountriesQuery
        {
            Query = "kaz",
            Limit = 10,
        }, TestContext.Current.CancellationToken);

        var country = Assert.Single(response.Countries);
        Assert.Equal("KAZ", country.Id);
    }

    [Fact]
    public async Task ListCountries_AppliesQueryAndLimit()
    {
        var service = CreateService();

        var response = await service.ListCountries(new UcpCountriesQuery
        {
            Query = "United",
            Limit = 2,
        }, TestContext.Current.CancellationToken);

        Assert.Equal(2, response.Countries.Count);
        Assert.All(response.Countries, country => Assert.Contains("United", country.Name, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ResolveCountry_MatchesCommonPartialCountryName()
    {
        var service = CreateService();

        var response = await service.ResolveCountry("United States", TestContext.Current.CancellationToken);

        Assert.Equal("USA", response.Country.Id);
        Assert.Equal("United States of America", response.Country.Name);
    }

    [Fact]
    public async Task ResolveCountry_Returns404WhenUnknown()
    {
        var service = CreateService();

        var exception = await Assert.ThrowsAsync<UcpException>(() => service.ResolveCountry("Neverland", TestContext.Current.CancellationToken));

        Assert.Equal(StatusCodes.Status404NotFound, exception.StatusCode);
        Assert.Equal(ModuleConstants.ErrorCodes.InvalidRequest, exception.Error.Code);
    }

    private static UcpGeographyService CreateService()
    {
        var httpContextAccessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext(),
        };
        httpContextAccessor.HttpContext.TraceIdentifier = "trace-geography";

        return new UcpGeographyService(new StubCountriesService(
        [
            new Country
            {
                Id = "KAZ",
                Name = "Kazakhstan",
                Regions =
                [
                    new CountryRegion { Id = "ALA", Name = "Алматы" },
                ],
            },
            new Country
            {
                Id = "USA",
                Name = "United States of America",
                Regions =
                [
                    new CountryRegion { Id = "WA", Name = "Washington" },
                ],
            },
            new Country
            {
                Id = "UMI",
                Name = "United States Minor Outlying Islands",
                Regions = [],
            },
        ]), httpContextAccessor);
    }

    private sealed class StubCountriesService : ICountriesService
    {
        private readonly List<Country> _countries;

        public StubCountriesService(IList<Country> countries)
        {
            _countries = countries.ToList();
        }

        public IList<Country> GetCountries()
        {
            return _countries;
        }

        public Task<IList<Country>> GetCountriesAsync()
        {
            return Task.FromResult<IList<Country>>(_countries);
        }

        public Task<IList<CountryRegion>> GetCountryRegionsAsync(string countryId)
        {
            return Task.FromResult(GetByCode(countryId).Regions ?? []);
        }

        public Country GetByCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                throw new ArgumentException("Country code is required.", nameof(code));
            }

            var normalized = code.Length == 2
                ? new RegionInfo(code).ThreeLetterISORegionName
                : code;

            return _countries.FirstOrDefault(x => string.Equals(x.Id, normalized, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Country with code {code} not found.", nameof(code));
        }

        public Country FindByName(string name)
        {
            return _countries.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        }
    }
}
