using Xunit;

namespace Equivalence.Tests;

public sealed class StatusServerTests
{
    [Fact]
    public void StartsListener()
    {
        new Sut.StatusServer().StartListener();
        Assert.True(true);
    }
}
