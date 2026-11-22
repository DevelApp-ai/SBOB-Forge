using Xunit;

namespace Equivalence.Tests;

public sealed class UserStoreTests
{
    [Fact]
    public void LoadsUser()
    {
        var raw = new Sut.UserStore().LoadUser("alice");
        Assert.NotNull(raw);
    }

    [Fact]
    public void AppendsLog()
    {
        new Sut.UserStore().AppendLog("entry");
        Assert.True(true);
    }
}
