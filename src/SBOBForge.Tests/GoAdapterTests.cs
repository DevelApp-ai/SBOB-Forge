using System.Linq;
using SBOBForge.Adapters.Go;
using SBOBForge.Parsing;
using Xunit;

namespace SBOBForge.Tests;

public class GoAdapterTests
{
    private static CodeModel Parse(string source) =>
        new GoSourceParser().Parse("tests/configstore_test.go", source);

    [Fact]
    public void GoTestNamingConventionIsTheEntryCue()
    {
        var model = Parse("""
            package core

            import "testing"

            func TestLoadsConfig(t *testing.T) {
            	LoadConfig()
            }

            func TestWithoutTestReceiver() {
            	LoadConfig()
            }

            func helper(t *testing.T) {
            	LoadConfig()
            }
            """);
        var adapter = new GoTestAdapter();
        Assert.True(adapter.CanAdapt(model));
        Assert.Equal("go-testing", adapter.FrameworkId);

        // TestXxx with a *testing.T param (conventionally named t); lowercase and
        // receiver-less funcs are not entries even with a t parameter
        var entry = Assert.Single(adapter.ExtractEntries(model));
        Assert.Equal("tests/configstore_test.go::TestLoadsConfig", entry.TestId);
        Assert.True(entry.Enabled);
        Assert.Equal("TestLoadsConfig", entry.EntryMethod);
    }

    [Fact]
    public void TSkipDisablesTheEntry()
    {
        var model = Parse("""
            package core

            import "testing"

            func TestLegacyPath(t *testing.T) {
            	t.Skip("legacy layout")
            }

            func TestSkippedWithFormat(t *testing.T) {
            	t.Skipf("skipping %s", "reason")
            }
            """);
        var entries = new GoTestFrameworkRegistry().ExtractEntries(model);
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.False(e.Enabled));
        Assert.Contains(entries, e => e.TestId.EndsWith("::TestLegacyPath"));
        Assert.Contains(entries, e => e.TestId.EndsWith("::TestSkippedWithFormat"));
    }

    [Fact]
    public void RegistryExtractsWhenNoFrameworkImportIsPresent()
    {
        // the go test convention needs no import cue; the entry cue is the signature
        var model = Parse("""
            package core

            func TestRuns(t *testing.T) {
            	Run()
            }
            """);
        var registry = new GoTestFrameworkRegistry();
        Assert.Single(registry.Detect(model));
        Assert.Single(registry.ExtractEntries(model));
    }
}
