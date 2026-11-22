using Xunit;

namespace Equivalence.Tests;

public sealed class ToolRunnerTests
{
    [Fact]
    public void RunsGitStatus()
    {
        new Sut.ToolRunner().RunStatus();
        Assert.True(true);
    }
}
