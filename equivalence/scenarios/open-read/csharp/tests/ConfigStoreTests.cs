using Xunit;

namespace Equivalence.Tests;

public sealed class ConfigStoreTests
{
    [Fact]
    public void LoadsConfigFromEtc()
    {
        var config = new Sut.ConfigStore().LoadConfig();
        Assert.NotNull(config);
    }
}
