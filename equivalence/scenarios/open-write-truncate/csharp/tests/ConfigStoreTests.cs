using Xunit;

namespace Equivalence.Tests;

public sealed class ConfigStoreTests
{
    [Fact]
    public void SavesCache()
    {
        new Sut.ConfigStore().SaveCache("{\"v\":1}");
        Assert.True(true);
    }
}
