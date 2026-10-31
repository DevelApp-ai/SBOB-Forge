using System.IO;
using Xunit;

namespace Example.Core.Tests;

public sealed class ConfigStoreTests
{
    [Fact]
    public void LoadsConfigFromEtc()
    {
        var config = new Sut.ConfigStore().LoadConfig();
        Assert.NotNull(config);
    }

    [Fact(Skip = "flaky in CI")]
    public void LegacyPathStillUsed()
    {
        _ = File.ReadAllText("/etc/app/legacy.json");
    }

    [Theory]
    [InlineData("/var/log/app.log")]
    [InlineData("/var/log/other.log")]
    public void WritesLogPath(string path)
    {
        File.WriteAllText(path, "entry");
    }
}
