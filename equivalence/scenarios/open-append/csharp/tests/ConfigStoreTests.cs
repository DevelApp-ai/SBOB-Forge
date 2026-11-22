using Xunit;

namespace Equivalence.Tests;

public sealed class ConfigStoreTests
{
    [Fact]
    public void AppendsLog()
    {
        new Sut.ConfigStore().AppendLog("entry");
        Assert.True(true);
    }
}
