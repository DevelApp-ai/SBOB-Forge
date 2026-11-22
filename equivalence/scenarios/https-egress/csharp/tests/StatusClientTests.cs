using Xunit;

namespace Equivalence.Tests;

public sealed class StatusClientTests
{
    [Fact]
    public void FetchesStatus()
    {
        var body = new Sut.StatusClient().FetchStatus();
        Assert.NotNull(body);
    }
}
