using System.Linq;
using SBOBForge.Adapters.Bash;
using SBOBForge.Parsing;
using Xunit;

namespace SBOBForge.Tests;

public class BatsAdapterTests
{
    private static CodeModel Parse(string source) =>
        new BashSourceParser().Parse("tests/bash/status.bats", source);

    [Fact]
    public void TestBlocksBecomeEntries()
    {
        var model = Parse("""
            @test "runs git status" {
                run_git_status
            }

            @test "fetches status" {
                fetch_status
            }
            """);
        var adapter = new BatsAdapter();
        Assert.True(adapter.CanAdapt(model));
        Assert.Equal("bats", adapter.FrameworkId);

        var entries = adapter.ExtractEntries(model);
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.True(e.Enabled));
        Assert.Contains(entries, e => e.TestId == "tests/bash/status.bats::runs git status");
        Assert.Contains(entries, e => e.TestId == "tests/bash/status.bats::fetches status");
    }

    [Fact]
    public void SkipCommandDisablesTheEntry()
    {
        var model = Parse("""
            @test "legacy path" {
                skip
                rm "/tmp/legacy-cache"
            }
            """);
        var entries = new ShTestFrameworkRegistry().ExtractEntries(model);
        var entry = Assert.Single(entries);
        Assert.False(entry.Enabled);
        Assert.EndsWith("legacy path", entry.TestId);
    }

    [Fact]
    public void PlainScriptFileDoesNotAdapt()
    {
        var model = Parse("""
            run_git_status() {
                git status
            }
            """);
        var registry = new ShTestFrameworkRegistry();
        Assert.Empty(registry.Detect(model));
        Assert.Empty(registry.ExtractEntries(model));
    }
}
