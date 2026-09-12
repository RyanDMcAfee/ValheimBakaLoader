using System;
using Newtonsoft.Json;
using ValheimBakaLoader.Tools.Models;
using Xunit;

namespace ValheimBakaLoader.Tests.Tools
{
    /// <summary>
    /// The "possibly outdated" hint needs the release date of a mod's newest version, which
    /// the Thunderstore v1 index carries per version as "date_created". These cover that the
    /// field maps onto <see cref="ThunderstorePackageVersion.DateCreated"/>.
    /// </summary>
    public class ThunderstorePackageVersionTests
    {
        [Fact]
        public void date_created_parses_into_DateCreated()
        {
            const string json =
                "{\"version_number\":\"1.2.3\",\"download_url\":\"https://example/x\"," +
                "\"date_created\":\"2026-06-15T10:20:30Z\"}";

            var version = JsonConvert.DeserializeObject<ThunderstorePackageVersion>(json);

            Assert.NotNull(version.DateCreated);
            Assert.Equal(
                new DateTime(2026, 6, 15, 10, 20, 30, DateTimeKind.Utc),
                version.DateCreated.Value.ToUniversalTime());
        }

        [Fact]
        public void A_version_with_no_date_leaves_DateCreated_null()
        {
            const string json = "{\"version_number\":\"1.2.3\",\"download_url\":\"https://example/x\"}";

            var version = JsonConvert.DeserializeObject<ThunderstorePackageVersion>(json);

            Assert.Null(version.DateCreated);
        }

        [Fact]
        public void date_created_flows_through_the_package_latest_accessor()
        {
            const string json =
                "{\"namespace\":\"denikson\",\"name\":\"BepInExPack_Valheim\"," +
                "\"latest\":{\"version_number\":\"5.4.2202\",\"download_url\":\"https://example/x\"," +
                "\"date_created\":\"2026-01-02T00:00:00Z\"}}";

            var package = JsonConvert.DeserializeObject<ThunderstorePackage>(json);

            Assert.NotNull(package.Latest.DateCreated);
            Assert.Equal(
                new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
                package.Latest.DateCreated.Value.ToUniversalTime());
        }
    }
}
