using Core.Settings;
using Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ChatApp.Server.IntegrationTests.Quality;

public sealed class GeoLocationDatabaseTests
{
    [Fact]
    public void LocalDatabase_UsesLongestPrefixAndSupportsIpv4MappedAddress()
    {
        var path = Path.Combine(Path.GetTempPath(), $"chatapp-geo-{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllLines(path,
            [
                "203.0.113.0/24|Example|Region",
                "203.0.113.42/32|Example|Exact City",
                "2001:db8::/32|Example6|Region6",
            ]);

            var database = new LocalGeoLocationDatabase(
                Options.Create(new GeoLocationOptions { LocalDatabasePath = path }),
                NullLogger<LocalGeoLocationDatabase>.Instance);

            Assert.True(database.TryGetLocation("203.0.113.42", out var exact));
            Assert.Equal("Example>Exact City", exact);
            Assert.True(database.TryGetLocation("::ffff:203.0.113.7", out var mapped));
            Assert.Equal("Example>Region", mapped);
            Assert.True(database.TryGetLocation("2001:db8::1", out var ipv6));
            Assert.Equal("Example6>Region6", ipv6);
            Assert.False(database.TryGetLocation("198.51.100.1", out _));
        }
        finally
        {
            try { File.Delete(path); }
            catch { /* test cleanup best effort */ }
        }
    }
}
