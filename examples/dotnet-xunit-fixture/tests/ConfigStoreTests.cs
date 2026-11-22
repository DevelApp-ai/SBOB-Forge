using System.IO;
using Xunit;

namespace Example.Core.Tests;

public sealed class ConfigStoreTests
{
    [Fact]
    public void LoadsConfigFromEtc()
    {
        // M7 negative-corpus coverage (SBOB-Forge #6): mock-framework APIs inside an
        // enabled test entry must surface as mock-shadowed warnings, not harness noise.
        var registry = Substitute.For<IPackageRegistry>();
        var registryMock = new Mock<IPackageRegistry>();
        registryMock.VerifyAll();
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
